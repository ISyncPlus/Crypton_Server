using Crypton.Api.Auth;
using Crypton.Api.Contracts;
using Crypton.Core.Admin;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Fiat;
using Crypton.Core.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers.Admin;

[Route("api/admin")]
[Authorize(Policy = Policies.Staff)]
public sealed class AdminMoneyController(
    CryptonDbContext db,
    CryptoWithdrawalService cryptoWithdrawals,
    FiatService fiat,
    TreasuryService treasury) : ApiControllerBase
{
    [HttpGet("withdrawals/crypto")]
    public async Task<PageDto<AdminCryptoWithdrawalDto>> CryptoWithdrawals([FromQuery] CryptoWithdrawalStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = from w in db.CryptoWithdrawals.AsNoTracking()
                    join u in db.Users.AsNoTracking() on w.UserId equals u.Id
                    select new { w, u.Email };
        if (status is not null)
        {
            query = query.Where(x => x.w.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.w.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return rows.Select(x => ToDto(x.w, x.Email)).ToList().ToPage(request, total);
    }

    [HttpPost("withdrawals/crypto/{id:guid}/approve")]
    [Authorize(Policy = Policies.Compliance)]
    public async Task<AdminCryptoWithdrawalDto> ApproveCrypto(Guid id, AdminNoteRequest request, CancellationToken ct) =>
        await CryptoDtoAsync((await cryptoWithdrawals.ApproveAsync(CurrentUserId, id, request.Note, ct)).Id, ct);

    [HttpPost("withdrawals/crypto/{id:guid}/reject")]
    [Authorize(Policy = Policies.Compliance)]
    public async Task<AdminCryptoWithdrawalDto> RejectCrypto(Guid id, AdminReasonRequest request, CancellationToken ct) =>
        await CryptoDtoAsync((await cryptoWithdrawals.RejectAsync(CurrentUserId, id, request.Reason, ct)).Id, ct);

    [HttpPost("withdrawals/crypto/{id:guid}/retry")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<AdminCryptoWithdrawalDto> RetryCrypto(Guid id, AdminReasonRequest request, CancellationToken ct) =>
        await CryptoDtoAsync((await cryptoWithdrawals.RetryAsync(CurrentUserId, id, request.Reason, ct)).Id, ct);

    [HttpPost("withdrawals/crypto/{id:guid}/refund")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<AdminCryptoWithdrawalDto> RefundCrypto(Guid id, AdminReasonRequest request, CancellationToken ct) =>
        await CryptoDtoAsync((await cryptoWithdrawals.RefundAsync(CurrentUserId, id, request.Reason, ct)).Id, ct);

    [HttpPost("withdrawals/crypto/{id:guid}/mark-sent")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<AdminCryptoWithdrawalDto> MarkSent(Guid id, AdminMarkSentRequest request, CancellationToken ct) =>
        await CryptoDtoAsync((await cryptoWithdrawals.MarkSentAsync(CurrentUserId, id, request.TxHash, request.Note, ct)).Id, ct);

    [HttpGet("withdrawals/fiat")]
    public async Task<PageDto<AdminFiatWithdrawalDto>> FiatWithdrawals([FromQuery] FiatWithdrawalStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = from w in db.FiatWithdrawals.AsNoTracking()
                    join u in db.Users.AsNoTracking() on w.UserId equals u.Id
                    select new { w, u.Email };
        if (status is not null)
        {
            query = query.Where(x => x.w.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.w.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return rows.Select(x => ToDto(x.w, x.Email)).ToList().ToPage(request, total);
    }

    [HttpPost("withdrawals/fiat/{id:guid}/approve")]
    [Authorize(Policy = Policies.Compliance)]
    public async Task<AdminFiatWithdrawalDto> ApproveFiat(Guid id, AdminNoteRequest request, CancellationToken ct) =>
        await FiatDtoAsync((await fiat.ApproveWithdrawalAsync(CurrentUserId, id, request.Note, ct)).Id, ct);

    [HttpPost("withdrawals/fiat/{id:guid}/reject")]
    [Authorize(Policy = Policies.Compliance)]
    public async Task<AdminFiatWithdrawalDto> RejectFiat(Guid id, AdminReasonRequest request, CancellationToken ct) =>
        await FiatDtoAsync((await fiat.RejectWithdrawalAsync(CurrentUserId, id, request.Reason, ct)).Id, ct);

    [HttpGet("deposits/crypto")]
    public async Task<PageDto<object>> CryptoDeposits([FromQuery] CryptoDepositStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = from d in db.CryptoDeposits.AsNoTracking()
                    join u in db.Users.AsNoTracking() on d.UserId equals u.Id
                    select new { d, u.Email };
        if (status is not null)
        {
            query = query.Where(x => x.d.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.d.DetectedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return rows.Select(x => (object)new { deposit = x.d.ToDto(), userId = x.d.UserId, userEmail = x.Email, address = x.d.Address, ngnValue = x.d.NgnValue }).ToList().ToPage(request, total);
    }

    [HttpGet("deposits/fiat")]
    public async Task<PageDto<object>> FiatDeposits([FromQuery] FiatDepositStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = from d in db.FiatDeposits.AsNoTracking()
                    join u in db.Users.AsNoTracking() on d.UserId equals u.Id
                    select new { d, u.Email };
        if (status is not null)
        {
            query = query.Where(x => x.d.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.d.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return rows.Select(x => (object)new { deposit = x.d.ToDto(), userId = x.d.UserId, userEmail = x.Email, provider = x.d.Provider, providerFee = x.d.ProviderFee, gatewayResponse = x.d.GatewayResponse }).ToList().ToPage(request, total);
    }

    [HttpGet("trades")]
    public async Task<PageDto<object>> Trades([FromQuery] Guid? userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = from o in db.TradeOrders.AsNoTracking()
                    join u in db.Users.AsNoTracking() on o.UserId equals u.Id
                    select new { o, u.Email };
        if (userId is not null)
        {
            query = query.Where(x => x.o.UserId == userId);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.o.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return rows.Select(x => (object)new { trade = x.o.ToDto(), userId = x.o.UserId, userEmail = x.Email }).ToList().ToPage(request, total);
    }

    [HttpGet("treasury")]
    [Authorize(Policy = Policies.Compliance)]
    public Task<IReadOnlyList<TreasuryAssetView>> Treasury([FromQuery] bool onChain = true, CancellationToken ct = default) =>
        treasury.GetOverviewAsync(onChain, ct);

    [HttpPost("treasury/fund")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Fund(AdminTreasuryRequest request, CancellationToken ct)
    {
        await treasury.FundAsync(CurrentUserId, request.Asset, request.Amount, request.Reference, request.Note, defund: false, ct);
        return NoContent();
    }

    [HttpPost("treasury/defund")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Defund(AdminTreasuryRequest request, CancellationToken ct)
    {
        await treasury.FundAsync(CurrentUserId, request.Asset, request.Amount, request.Reference, request.Note, defund: true, ct);
        return NoContent();
    }

    private async Task<AdminCryptoWithdrawalDto> CryptoDtoAsync(Guid id, CancellationToken ct)
    {
        var row = await (from w in db.CryptoWithdrawals.AsNoTracking()
                         join u in db.Users.AsNoTracking() on w.UserId equals u.Id
                         where w.Id == id
                         select new { w, u.Email }).FirstOrDefaultAsync(ct) ?? throw AppException.NotFound("Withdrawal");
        return ToDto(row.w, row.Email);
    }

    private async Task<AdminFiatWithdrawalDto> FiatDtoAsync(Guid id, CancellationToken ct)
    {
        var row = await (from w in db.FiatWithdrawals.AsNoTracking()
                         join u in db.Users.AsNoTracking() on w.UserId equals u.Id
                         where w.Id == id
                         select new { w, u.Email }).FirstOrDefaultAsync(ct) ?? throw AppException.NotFound("Withdrawal");
        return ToDto(row.w, row.Email);
    }

    private static AdminCryptoWithdrawalDto ToDto(CryptoWithdrawal w, string? email) => new(w.Id, w.UserId, email ?? "", w.Asset, w.Network, w.ToAddress, w.Amount, w.Fee, w.NgnValue,
        w.Status, w.RiskSummary, w.TxHash, w.Confirmations, w.NetworkFee, w.BroadcastAttempts, w.FailureReason, w.ReviewNote, w.BroadcastAt is not null, w.CreatedAt, w.BroadcastAt, w.ConfirmedAt);

    private static AdminFiatWithdrawalDto ToDto(FiatWithdrawal w, string? email) => new(w.Id, w.UserId, email ?? "", w.Amount, w.Fee, w.Status, w.Reference, w.TransferCode,
        w.BankName, w.AccountNumber, w.AccountName, w.RiskSummary, w.FailureReason, w.ReviewNote, w.CreatedAt, w.CompletedAt);
}
