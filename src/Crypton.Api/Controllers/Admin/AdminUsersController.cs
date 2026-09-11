using Crypton.Api.Auth;
using Crypton.Api.Contracts;
using Crypton.Api.Infrastructure;
using Crypton.Core.Admin;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Ledger;
using Crypton.Core.Pricing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers.Admin;

[Route("api/admin/users")]
[Authorize(Policy = Policies.Staff)]
public sealed class AdminUsersController(
    CryptonDbContext db,
    AdminUserService admin,
    UserManager<AppUser> users,
    LedgerService ledger,
    PriceService prices) : ApiControllerBase
{
    [HttpGet]
    public async Task<PageDto<AdminUserListItem>> Search([FromQuery] string? q, [FromQuery] UserStatus? status, [FromQuery] int? tier, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        (await admin.SearchAsync(q, status, tier, Page(page, pageSize), ct)).Map(x => x);

    [HttpGet("{id:guid}")]
    public async Task<AdminUserDetailDto> Detail(Guid id, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString()) ?? throw AppException.NotFound("User");
        var balances = await ledger.GetUserBalancesAsync(id, ct);
        var priceMap = await prices.GetPricesAsync(ct);
        var balanceDtos = balances.Select(b =>
        {
            var value = b.Asset == AssetCodes.NGN ? b.Total : priceMap.TryGetValue(b.Asset, out var p) ? MoneyMath.RoundHalfUp(b.Total * p.PriceNgn, 2) : 0m;
            return new BalanceDto(b.Asset, b.Available, b.Locked, b.Total, value);
        }).ToList();
        var kyc = await db.KycSubmissions.AsNoTracking().Where(s => s.UserId == id).OrderByDescending(s => s.CreatedAt).Take(10).ToListAsync(ct);
        var alerts = await db.AmlAlerts.CountAsync(a => a.UserId == id && a.Status == AmlAlertStatus.Open, ct);
        var sessionCount = await db.RefreshTokens.Where(t => t.UserId == id && t.RevokedAt == null).Select(t => t.FamilyId).Distinct().CountAsync(ct);
        var banks = await db.BankAccounts.AsNoTracking().Where(b => b.UserId == id && !b.IsDeleted).ToListAsync(ct);

        return new AdminUserDetailDto(AccountController.ToDto(user, await users.GetRolesAsync(user)), user.FrozenReason, user.EmailConfirmed, user.LastLoginAt, user.LockoutEnd,
            balanceDtos, kyc.Select(k => k.ToDto()).ToList(), alerts, sessionCount, banks.Select(b => b.ToDto()).ToList());
    }

    [HttpPost("{id:guid}/freeze")]
    [Authorize(Policy = Policies.Compliance)]
    public async Task<IActionResult> Freeze(Guid id, AdminReasonRequest request, CancellationToken ct)
    {
        await admin.FreezeAsync(CurrentUserId, id, request.Reason, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/unfreeze")]
    [Authorize(Policy = Policies.Compliance)]
    public async Task<IActionResult> Unfreeze(Guid id, CancellationToken ct)
    {
        await admin.UnfreezeAsync(CurrentUserId, id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reset-2fa")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> ResetTwoFactor(Guid id, AdminReasonRequest request, CancellationToken ct)
    {
        await admin.ResetTwoFactorAsync(CurrentUserId, id, request.Reason, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/revoke-sessions")]
    [Authorize(Policy = Policies.Compliance)]
    public async Task<IActionResult> RevokeSessions(Guid id, CancellationToken ct)
    {
        await admin.RevokeSessionsAsync(CurrentUserId, id, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/roles")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Roles(Guid id, AdminRolesRequest request, CancellationToken ct)
    {
        await admin.SetRolesAsync(CurrentUserId, id, request.Roles ?? [], ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/adjust-balance")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> AdjustBalance(Guid id, AdminAdjustBalanceRequest request, CancellationToken ct)
    {
        await admin.AdjustBalanceAsync(CurrentUserId, id, request.Asset, request.Amount, request.Reason, ct);
        return NoContent();
    }
}
