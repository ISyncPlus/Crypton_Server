using Crypton.Core.Assets;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Ledger;
using Crypton.Core.Wallets;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Admin;

public sealed record TreasuryAssetView(
    string Asset,
    decimal CustodyHeld,
    decimal TreasuryInventory,
    decimal FeesEarned,
    decimal P2PEscrow,
    decimal NetworkFeesPaid,
    decimal PaymentCosts,
    decimal Adjustments,
    decimal UserAvailable,
    decimal UserLocked,
    decimal PendingWithdrawals,
    string? HotWalletAddress,
    decimal? HotWalletBalance,
    string? HotWalletError);

public sealed class TreasuryService(
    CryptonDbContext db,
    AssetCatalog assets,
    LedgerService ledger,
    ChainGatewayRegistry chains,
    AuditService audit)
{
    public async Task<IReadOnlyList<TreasuryAssetView>> GetOverviewAsync(bool includeOnChain, CancellationToken ct = default)
    {
        var accounts = await db.LedgerAccounts.AsNoTracking()
            .GroupBy(a => new { a.Asset, a.OwnerType, a.SystemCode, a.Kind })
            .Select(g => new { g.Key.Asset, g.Key.OwnerType, g.Key.SystemCode, g.Key.Kind, Balance = g.Sum(a => a.Balance) })
            .ToListAsync(ct);

        var pendingCrypto = await db.CryptoWithdrawals.AsNoTracking()
            .Where(w => w.Status == CryptoWithdrawalStatus.PendingReview || w.Status == CryptoWithdrawalStatus.Approved || w.Status == CryptoWithdrawalStatus.Broadcasting || w.Status == CryptoWithdrawalStatus.NeedsAttention)
            .GroupBy(w => w.Asset)
            .Select(g => new { Asset = g.Key, Total = g.Sum(w => w.Amount) })
            .ToListAsync(ct);
        var pendingFiat = await db.FiatWithdrawals.AsNoTracking()
            .Where(w => w.Status == FiatWithdrawalStatus.PendingReview || w.Status == FiatWithdrawalStatus.Approved || w.Status == FiatWithdrawalStatus.Processing || w.Status == FiatWithdrawalStatus.NeedsAttention)
            .SumAsync(w => (decimal?)w.Amount, ct) ?? 0m;

        decimal Sys(string asset, string code) => accounts.Where(a => a.Asset == asset && a.SystemCode == code).Sum(a => a.Balance);

        var result = new List<TreasuryAssetView>();
        foreach (var asset in await assets.GetAllAsync(ct))
        {
            string? hotAddress = null;
            decimal? hotBalance = null;
            string? hotError = null;
            if (includeOnChain && asset.Network is not null && chains.Find(asset.Network) is { IsConfigured: true } gateway)
            {
                hotAddress = gateway.HotWalletAddress;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    hotBalance = await gateway.GetHotWalletBalanceAsync(asset.Code, timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    hotError = ex.Message;
                }
            }

            result.Add(new TreasuryAssetView(
                asset.Code,
                -Sys(asset.Code, SystemAccounts.Custody),
                Sys(asset.Code, SystemAccounts.Treasury),
                Sys(asset.Code, SystemAccounts.Fees),
                Sys(asset.Code, SystemAccounts.P2PEscrow),
                -Sys(asset.Code, SystemAccounts.NetworkFees),
                -Sys(asset.Code, SystemAccounts.PaymentCosts),
                Sys(asset.Code, SystemAccounts.Adjustments),
                accounts.Where(a => a.Asset == asset.Code && a.OwnerType == AccountOwnerType.User && a.Kind == AccountKind.Available).Sum(a => a.Balance),
                accounts.Where(a => a.Asset == asset.Code && a.OwnerType == AccountOwnerType.User && a.Kind != AccountKind.Available).Sum(a => a.Balance),
                asset.IsFiat ? pendingFiat : pendingCrypto.Where(p => p.Asset == asset.Code).Sum(p => p.Total),
                hotAddress,
                hotBalance,
                hotError));
        }

        return result;
    }

    /// <summary>Records house funds added to custody and made available as trading inventory.</summary>
    public async Task FundAsync(Guid adminId, string assetCode, decimal amount, string reference, string note, bool defund, CancellationToken ct = default)
    {
        var asset = await assets.GetAsync(assetCode, ct);
        if (amount <= 0 || !MoneyMath.HasMaxDecimals(amount, asset.Precision))
        {
            throw new AppException(ErrorCodes.InvalidAmount, $"Amount must be positive with at most {asset.Precision} decimals.");
        }

        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 100)
        {
            throw AppException.Validation("A reference (e.g. on-chain tx hash or bank reference) is required.");
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            throw AppException.Validation("A note is required.");
        }

        await db.InTransactionAsync(async token =>
        {
            var journal = new JournalBuilder(defund ? JournalTypes.TreasuryDefunding : JournalTypes.TreasuryFunding)
                .Idempotent($"treasury:{(defund ? "defund" : "fund")}:{asset.Code}:{reference.Trim()}")
                .Describe($"{note.Trim()} (ref {reference.Trim()})");

            if (defund)
            {
                journal.System(SystemAccounts.Treasury, asset.Code, -amount).System(SystemAccounts.Custody, asset.Code, amount);
            }
            else
            {
                journal.System(SystemAccounts.Custody, asset.Code, -amount).System(SystemAccounts.Treasury, asset.Code, amount);
            }

            try
            {
                await ledger.PostAsync(journal, token);
            }
            catch (DuplicateJournalException)
            {
                throw AppException.Conflict(ErrorCodes.DuplicateRequest, "That reference was already recorded.");
            }

            audit.Record(defund ? AuditActions.AdminTreasuryDefunded : AuditActions.AdminTreasuryFunded, null, new AuditContext(adminId, null),
                "treasury", asset.Code, new { amount, reference, note });
            await db.SaveChangesAsync(token);
        }, ct);
    }
}
