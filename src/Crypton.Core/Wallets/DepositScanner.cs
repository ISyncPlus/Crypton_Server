using Crypton.Core.Aml;
using Crypton.Core.Assets;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Ledger;
using Crypton.Core.Notifications;
using Crypton.Core.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crypton.Core.Wallets;

/// <summary>Finds incoming blockchain deposits, tracks confirmations and credits users exactly once.</summary>
public sealed class DepositScanner(
    CryptonDbContext db,
    ChainGatewayRegistry chains,
    AssetCatalog assets,
    LedgerService ledger,
    PriceService prices,
    AmlEngine aml,
    NotificationService notifications,
    IOptions<BlockchainOptions> options,
    TimeProvider clock,
    ILogger<DepositScanner> logger)
{
    public async Task RunAsync(string network, CancellationToken ct)
    {
        var gateway = chains.Find(network);
        if (gateway is null || !gateway.IsConfigured)
        {
            return;
        }

        var tip = await gateway.GetTipHeightAsync(ct);
        await ScanAsync(gateway, tip, ct);
        await RefreshPendingAsync(gateway, tip, ct);
        await CreditConfirmedAsync(gateway, ct);
    }

    private async Task ScanAsync(IChainGateway gateway, long tip, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cursor = await db.ChainCursors.FirstOrDefaultAsync(c => c.Network == gateway.Network, ct);

        var watched = (await db.DepositAddresses.AsNoTracking()
                .Where(a => a.Network == gateway.Network)
                .Select(a => a.Address)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        if (watched.Count == 0)
        {
            return;
        }

        List<DepositAddress> toPoll = [];
        if (gateway.PollsAddresses)
        {
            var batch = options.Value.DepositPollBatchSize;
            var urgent = await db.DepositAddresses
                .Where(a => a.Network == gateway.Network && a.WatchUntil > now)
                .OrderBy(a => a.LastCheckedAt)
                .Take(batch)
                .ToListAsync(ct);
            var routine = await db.DepositAddresses
                .Where(a => a.Network == gateway.Network && (a.WatchUntil == null || a.WatchUntil <= now))
                .OrderBy(a => a.LastCheckedAt == null ? 0 : 1)
                .ThenBy(a => a.LastCheckedAt)
                .Take(Math.Max(1, batch / 4))
                .ToListAsync(ct);
            toPoll = urgent.Concat(routine).DistinctBy(a => a.Id).ToList();
        }

        var context = new DepositScanContext(tip, cursor?.LastProcessedBlock, watched, toPoll.Select(a => a.Address).ToList());
        var result = await gateway.ScanAsync(context, ct);

        foreach (var observed in result.Deposits)
        {
            await RecordObservedAsync(gateway, observed, ct);
        }

        foreach (var polled in toPoll)
        {
            polled.LastCheckedAt = now;
        }

        if (result.NewCursorBlock is { } newCursor)
        {
            if (cursor is null)
            {
                db.ChainCursors.Add(new ChainCursor { Network = gateway.Network, LastProcessedBlock = newCursor, UpdatedAt = now });
            }
            else
            {
                cursor.LastProcessedBlock = newCursor;
                cursor.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task RecordObservedAsync(IChainGateway gateway, ObservedDeposit observed, CancellationToken ct)
    {
        var address = gateway.NormalizeAddress(observed.Address);
        var owner = await db.DepositAddresses.AsNoTracking()
            .Where(a => a.Network == gateway.Network && a.Address == address)
            .Select(a => (Guid?)a.UserId)
            .FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            return;
        }

        var asset = await assets.GetAsync(observed.Asset, ct);
        var existing = await db.CryptoDeposits.FirstOrDefaultAsync(d =>
            d.Network == gateway.Network && d.TxHash == observed.TxHash && d.OutputIndex == observed.OutputIndex && d.Asset == asset.Code, ct);

        var amount = MoneyMath.Floor(observed.Amount, asset.Precision);
        if (existing is not null)
        {
            if (existing.Status == CryptoDepositStatus.Pending)
            {
                existing.Confirmations = Math.Max(existing.Confirmations, observed.Confirmations);
                existing.BlockNumber = observed.BlockNumber ?? existing.BlockNumber;
            }

            return;
        }

        var deposit = new CryptoDeposit
        {
            Id = Ids.New(),
            UserId = owner.Value,
            Asset = asset.Code,
            Network = gateway.Network,
            TxHash = observed.TxHash,
            OutputIndex = observed.OutputIndex,
            Address = address,
            Amount = amount,
            BlockNumber = observed.BlockNumber,
            Confirmations = observed.Confirmations,
            RequiredConfirmations = asset.RequiredConfirmations,
            Status = CryptoDepositStatus.Pending,
            DetectedAt = clock.GetUtcNow(),
        };

        if (amount <= 0 || amount < asset.MinDeposit)
        {
            deposit.Status = CryptoDepositStatus.Rejected;
            deposit.RejectionReason = $"Below the minimum deposit of {MoneyMath.ToPlainString(asset.MinDeposit)} {asset.Code}.";
        }

        db.CryptoDeposits.Add(deposit);
        await notifications.QueueAsync(
            deposit.UserId,
            NotificationTypes.Deposit,
            deposit.Status == CryptoDepositStatus.Rejected ? $"{asset.Code} deposit below minimum" : $"Incoming {asset.Code} deposit",
            deposit.Status == CryptoDepositStatus.Rejected
                ? $"We detected {MoneyMath.ToPlainString(amount)} {asset.Code}, which is below the minimum deposit and cannot be credited."
                : $"We detected {MoneyMath.ToPlainString(amount)} {asset.Code}. It will be credited after {asset.RequiredConfirmations} confirmations.",
            "/wallets",
            email: false,
            ct);
    }

    private async Task RefreshPendingAsync(IChainGateway gateway, long tip, CancellationToken ct)
    {
        var pending = await db.CryptoDeposits
            .Where(d => d.Network == gateway.Network && d.Status == CryptoDepositStatus.Pending)
            .OrderBy(d => d.DetectedAt)
            .Take(200)
            .ToListAsync(ct);

        foreach (var deposit in pending)
        {
            if (deposit.BlockNumber is { } block && !gateway.IsSimulated)
            {
                deposit.Confirmations = (int)Math.Max(0, tip - block + 1);
                continue;
            }

            try
            {
                var status = await gateway.GetTransactionStatusAsync(deposit.Asset, deposit.TxHash, ct);
                if (status.Found)
                {
                    deposit.Confirmations = status.Confirmations;
                    deposit.BlockNumber = status.BlockNumber ?? deposit.BlockNumber;
                }
                else if (clock.GetUtcNow() - deposit.DetectedAt > TimeSpan.FromDays(3))
                {
                    deposit.Status = CryptoDepositStatus.Rejected;
                    deposit.RejectionReason = "Transaction was dropped from the network.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not refresh deposit {TxHash}", deposit.TxHash);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task CreditConfirmedAsync(IChainGateway gateway, CancellationToken ct)
    {
        var ready = await db.CryptoDeposits.AsNoTracking()
            .Where(d => d.Network == gateway.Network && d.Status == CryptoDepositStatus.Pending && d.Confirmations >= d.RequiredConfirmations)
            .OrderBy(d => d.DetectedAt)
            .Select(d => d.Id)
            .Take(100)
            .ToListAsync(ct);

        foreach (var id in ready)
        {
            try
            {
                await CreditAsync(gateway, id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Crediting deposit {DepositId} failed", id);
            }
        }
    }

    private async Task CreditAsync(IChainGateway gateway, Guid depositId, CancellationToken ct)
    {
        var snapshot = await db.CryptoDeposits.AsNoTracking().FirstAsync(d => d.Id == depositId, ct);

        // Re-verify on chain right before crediting (guards against re-orgs and bad scan data).
        var status = await gateway.GetTransactionStatusAsync(snapshot.Asset, snapshot.TxHash, ct);
        if (!status.Found || status.Failed || status.Confirmations < snapshot.RequiredConfirmations)
        {
            var tracked = await db.CryptoDeposits.FirstAsync(d => d.Id == depositId, ct);
            tracked.Confirmations = status.Found ? status.Confirmations : 0;
            if (status.Failed)
            {
                tracked.Status = CryptoDepositStatus.Rejected;
                tracked.RejectionReason = "Transaction failed on chain.";
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        var ngnValue = 0m;
        try
        {
            ngnValue = await prices.ToNgnAsync(snapshot.Asset, snapshot.Amount, ct);
        }
        catch (AppException)
        {
            // Valuation is informational; never block a credit on it.
        }

        var credited = await db.InTransactionAsync(async token =>
        {
            var deposit = (await db.CryptoDeposits
                    .FromSqlInterpolated($"SELECT * FROM crypto_deposits WHERE id = {depositId} FOR UPDATE")
                    .ToListAsync(token))
                .Single();

            if (deposit.Status != CryptoDepositStatus.Pending)
            {
                return false;
            }

            var entry = await ledger.PostAsync(
                new JournalBuilder(JournalTypes.CryptoDeposit)
                    .ForUser(deposit.UserId)
                    .Reference("crypto_deposit", deposit.Id)
                    .Idempotent($"deposit:{deposit.Network}:{deposit.TxHash}:{deposit.OutputIndex}:{deposit.Asset}")
                    .Describe($"Deposit {MoneyMath.ToPlainString(deposit.Amount)} {deposit.Asset} ({deposit.TxHash})")
                    .System(SystemAccounts.Custody, deposit.Asset, -deposit.Amount)
                    .User(deposit.UserId, deposit.Asset, AccountKind.Available, deposit.Amount),
                token);

            deposit.Status = CryptoDepositStatus.Credited;
            deposit.Confirmations = status.Confirmations;
            deposit.CreditedAt = clock.GetUtcNow();
            deposit.JournalEntryId = entry.Id;
            deposit.NgnValue = ngnValue;

            var decision = await aml.EvaluateDepositAsync(deposit.UserId, isFiat: false, ngnValue, token);
            aml.QueueAlerts(decision, deposit.UserId, AmlSubjectType.CryptoDeposit, deposit.Id);

            await notifications.QueueAsync(
                deposit.UserId,
                NotificationTypes.Deposit,
                $"{deposit.Asset} deposit credited",
                $"{MoneyMath.ToPlainString(deposit.Amount)} {deposit.Asset} has been added to your wallet.",
                "/wallets",
                email: true,
                token);

            await db.SaveChangesAsync(token);
            return true;
        }, ct);

        if (credited)
        {
            logger.LogInformation("Credited deposit {DepositId}", depositId);
        }
    }
}
