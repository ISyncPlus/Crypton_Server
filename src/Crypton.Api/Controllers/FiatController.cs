using Crypton.Api.Contracts;
using Crypton.Api.Infrastructure;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Fiat;
using Crypton.Core.Settings;
using Crypton.Integrations.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers;

[Route("api/fiat")]
public sealed class FiatController(FiatService fiat, SettingsService settings, CryptonDbContext db, IServiceProvider services) : ApiControllerBase
{
    [HttpGet("config")]
    public async Task<FiatConfigDto> Config(CancellationToken ct)
    {
        var f = await settings.GetAsync<FiatSettings>(ct);
        var w = await settings.GetAsync<WithdrawalSettings>(ct);
        return new FiatConfigDto(fiat.ProviderName, fiat.IsSimulated, f.DepositFeeBps, f.DepositFeeCapNgn, f.MinDepositNgn, f.MaxDepositNgn, w.FiatWithdrawalFeeNgn, w.MinFiatWithdrawalNgn, w.MaxFiatWithdrawalNgn);
    }

    [HttpGet("banks")]
    public async Task<IReadOnlyList<BankDto>> Banks(CancellationToken ct) =>
        (await fiat.ListBanksAsync(ct)).Select(b => new BankDto(b.Code, b.Name)).ToList();

    [HttpPost("bank-accounts/resolve")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<ResolvedAccountDto> Resolve(ResolveAccountRequest request, CancellationToken ct)
    {
        var resolved = await fiat.ResolveAccountAsync(request.AccountNumber, request.BankCode, ct);
        return new ResolvedAccountDto(resolved.AccountNumber, resolved.AccountName, resolved.BankCode);
    }

    [HttpGet("bank-accounts")]
    public async Task<IReadOnlyList<BankAccountDto>> BankAccounts(CancellationToken ct)
    {
        var userId = CurrentUserId;
        var accounts = await db.BankAccounts.AsNoTracking().Where(b => b.UserId == userId && !b.IsDeleted).OrderBy(b => b.CreatedAt).ToListAsync(ct);
        return accounts.Select(b => b.ToDto()).ToList();
    }

    [HttpPost("bank-accounts")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<BankAccountDto> AddBankAccount(ResolveAccountRequest request, CancellationToken ct) =>
        (await fiat.AddBankAccountAsync(CurrentUserId, request.AccountNumber, request.BankCode, HttpContext.Audit(), ct)).ToDto();

    [HttpDelete("bank-accounts/{id:guid}")]
    public async Task<IActionResult> RemoveBankAccount(Guid id, CancellationToken ct)
    {
        await fiat.RemoveBankAccountAsync(CurrentUserId, id, HttpContext.Audit(), ct);
        return NoContent();
    }

    [HttpPost("deposits")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<FiatDepositDto> Deposit(CreateFiatDepositRequest request, CancellationToken ct) =>
        (await fiat.InitiateDepositAsync(CurrentUserId, request.Amount, ct)).ToDto();

    [HttpGet("deposits")]
    public async Task<PageDto<FiatDepositDto>> Deposits([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var request = Page(page, pageSize);
        var query = db.FiatDeposits.AsNoTracking().Where(d => d.UserId == userId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(d => d.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return items.Select(d => d.ToDto()).ToList().ToPage(request, total);
    }

    /// <summary>Called by the payment return page: verifies with the provider and credits if paid.</summary>
    [HttpPost("deposits/{reference}/verify")]
    public async Task<FiatDepositDto> Verify(string reference, CancellationToken ct)
    {
        var owned = await db.FiatDeposits.AsNoTracking().AnyAsync(d => d.Reference == reference && d.UserId == CurrentUserId, ct);
        if (!owned)
        {
            throw AppException.NotFound("Deposit");
        }

        return (await fiat.ConfirmDepositAsync(reference, ct)).ToDto();
    }

    [HttpPost("withdrawals")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<FiatWithdrawalDto> Withdraw(CreateFiatWithdrawalRequest request, CancellationToken ct) =>
        (await fiat.RequestWithdrawalAsync(CurrentUserId, new FiatWithdrawalRequest(request.BankAccountId, request.Amount, request.TwoFactorCode, request.IdempotencyKey), HttpContext.RequestInfo(), ct)).ToDto();

    [HttpGet("withdrawals")]
    public async Task<PageDto<FiatWithdrawalDto>> Withdrawals([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var request = Page(page, pageSize);
        var query = db.FiatWithdrawals.AsNoTracking().Where(w => w.UserId == userId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(w => w.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return items.Select(w => w.ToDto()).ToList().ToPage(request, total);
    }

    [HttpPost("withdrawals/{id:guid}/cancel")]
    public async Task<FiatWithdrawalDto> CancelWithdrawal(Guid id, CancellationToken ct) =>
        (await fiat.CancelWithdrawalAsync(CurrentUserId, id, ct)).ToDto();

    [HttpGet("simulated-checkout/{reference}")]
    public async Task<SimulatedCheckoutDto> SimulatedCheckout(string reference, CancellationToken ct)
    {
        RequireSimulated();
        var userId = CurrentUserId;
        var deposit = await db.FiatDeposits.AsNoTracking().FirstOrDefaultAsync(d => d.Reference == reference && d.UserId == userId, ct) ?? throw AppException.NotFound("Payment");
        var payment = await db.SimulatedPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Reference == reference, ct) ?? throw AppException.NotFound("Payment");
        var email = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstAsync(ct);
        return new SimulatedCheckoutDto(reference, deposit.Amount, payment.Status, email ?? "");
    }

    [HttpPost("simulated-checkout/{reference}")]
    public async Task<FiatDepositDto> CompleteSimulatedCheckout(string reference, CompleteSimulatedCheckoutRequest request, CancellationToken ct)
    {
        RequireSimulated();
        var owned = await db.FiatDeposits.AsNoTracking().AnyAsync(d => d.Reference == reference && d.UserId == CurrentUserId, ct);
        if (!owned)
        {
            throw AppException.NotFound("Payment");
        }

        var simulator = services.GetRequiredService<SimulatedFiatGateway>();
        await simulator.CompleteChargeAsync(reference, request.Success, ct);
        return (await fiat.ConfirmDepositAsync(reference, ct)).ToDto();
    }

    private void RequireSimulated()
    {
        if (!fiat.IsSimulated)
        {
            throw AppException.NotFound("Page");
        }
    }
}
