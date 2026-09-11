using Crypton.Api.Contracts;
using Crypton.Api.Infrastructure;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Ledger;
using Crypton.Core.Pricing;
using Crypton.Core.Wallets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers;

[Route("api/wallets")]
public sealed class WalletsController(
    CryptonDbContext db,
    LedgerService ledger,
    PriceService prices,
    DepositAddressService addresses,
    CryptoWithdrawalService withdrawals) : ApiControllerBase
{
    [HttpGet]
    public async Task<WalletsResponse> Get(CancellationToken ct)
    {
        var balances = await ledger.GetUserBalancesAsync(CurrentUserId, ct);
        var priceMap = await prices.GetPricesAsync(ct);
        var result = new List<BalanceDto>();
        var pricesAvailable = true;
        foreach (var b in balances)
        {
            decimal value;
            if (b.Asset == AssetCodes.NGN)
            {
                value = b.Total;
            }
            else if (priceMap.TryGetValue(b.Asset, out var price))
            {
                value = MoneyMath.RoundHalfUp(b.Total * price.PriceNgn, 2);
            }
            else
            {
                value = 0;
                pricesAvailable = false;
            }

            result.Add(new BalanceDto(b.Asset, b.Available, b.Locked, b.Total, value));
        }

        return new WalletsResponse(result, result.Sum(r => r.ValueNgn), pricesAvailable);
    }

    [HttpGet("transactions")]
    public async Task<PageDto<TransactionDto>> Transactions([FromQuery] string? asset, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var request = Page(page, pageSize);
        var query = db.Postings.AsNoTracking().Where(p => p.UserId == userId && p.Kind == AccountKind.Available);
        if (!string.IsNullOrWhiteSpace(asset))
        {
            var code = asset.ToUpperInvariant();
            query = query.Where(p => p.Asset == code);
        }

        var grouped = query
            .GroupBy(p => new { p.JournalEntryId, p.Asset })
            .Select(g => new { g.Key.JournalEntryId, g.Key.Asset, Amount = g.Sum(p => p.Amount), CreatedAt = g.Max(p => p.CreatedAt) })
            .Where(x => x.Amount != 0);

        var total = await grouped.CountAsync(ct);
        var rows = await grouped
            .OrderByDescending(x => x.CreatedAt)
            .Skip(request.Skip).Take(request.SafePageSize)
            .ToListAsync(ct);

        var ids = rows.Select(r => r.JournalEntryId).Distinct().ToList();
        var journals = await db.JournalEntries.AsNoTracking()
            .Where(j => ids.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, ct);

        return rows
            .Select(x =>
            {
                var j = journals[x.JournalEntryId];
                return new TransactionDto(j.Id, j.Type, x.Asset, x.Amount, j.Description, j.ReferenceType, j.ReferenceId, x.CreatedAt);
            })
            .ToList()
            .ToPage(request, total);
    }

    [HttpGet("{asset}/address")]
    public async Task<DepositAddressDto> Address(string asset, CancellationToken ct)
    {
        var view = await addresses.GetOrCreateAsync(CurrentUserId, asset, ct);
        return new DepositAddressDto(view.Asset, view.Network, view.Address, view.RequiredConfirmations, view.MinDeposit, view.Simulated);
    }

    [HttpGet("deposits")]
    public async Task<PageDto<CryptoDepositDto>> Deposits([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var request = Page(page, pageSize);
        var query = db.CryptoDeposits.AsNoTracking().Where(d => d.UserId == userId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(d => d.DetectedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return items.Select(d => d.ToDto()).ToList().ToPage(request, total);
    }

    [HttpGet("withdrawals")]
    public async Task<PageDto<CryptoWithdrawalDto>> Withdrawals([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var request = Page(page, pageSize);
        var query = db.CryptoWithdrawals.AsNoTracking().Where(w => w.UserId == userId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(w => w.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return items.Select(w => w.ToDto()).ToList().ToPage(request, total);
    }

    [HttpGet("withdrawals/preview")]
    public async Task<WithdrawalPreview> Preview([FromQuery] string asset, [FromQuery] decimal amount, CancellationToken ct) =>
        await withdrawals.PreviewAsync(CurrentUserId, asset, amount, ct);

    [HttpPost("withdrawals")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<CryptoWithdrawalDto> Withdraw(CreateCryptoWithdrawalRequest request, CancellationToken ct)
    {
        var withdrawal = await withdrawals.RequestAsync(CurrentUserId,
            new CryptoWithdrawalRequest(request.Asset, request.Address, request.Amount, request.TwoFactorCode, request.IdempotencyKey),
            HttpContext.RequestInfo(), ct);
        return withdrawal.ToDto();
    }

    [HttpPost("withdrawals/{id:guid}/cancel")]
    public async Task<CryptoWithdrawalDto> Cancel(Guid id, CancellationToken ct) =>
        (await withdrawals.CancelAsync(CurrentUserId, id, ct)).ToDto();
}
