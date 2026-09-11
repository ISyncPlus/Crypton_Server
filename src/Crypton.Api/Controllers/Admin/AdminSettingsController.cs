using Crypton.Api.Auth;
using Crypton.Api.Contracts;
using Crypton.Core.Assets;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers.Admin;

[Route("api/admin")]
[Authorize(Policy = Policies.Staff)]
public sealed class AdminSettingsController(CryptonDbContext db, SettingsService settings, AssetCatalog catalog, AuditService audit, TimeProvider clock) : ApiControllerBase
{
    [HttpGet("settings")]
    public async Task<AdminSettingsDto> Get(CancellationToken ct) => new(
        await settings.GetAsync<TradingSettings>(ct),
        await settings.GetAsync<WithdrawalSettings>(ct),
        await settings.GetAsync<FiatSettings>(ct),
        await settings.GetAsync<P2PSettings>(ct),
        await settings.GetAsync<KycLimitSettings>(ct));

    [HttpPut("settings/trading")]
    [Authorize(Policy = Policies.Admin)]
    public Task<TradingSettings> Trading(TradingSettings value, CancellationToken ct) => SaveAsync(value, ct);

    [HttpPut("settings/withdrawals")]
    [Authorize(Policy = Policies.Admin)]
    public Task<WithdrawalSettings> Withdrawals(WithdrawalSettings value, CancellationToken ct) => SaveAsync(value, ct);

    [HttpPut("settings/fiat")]
    [Authorize(Policy = Policies.Admin)]
    public Task<FiatSettings> Fiat(FiatSettings value, CancellationToken ct) => SaveAsync(value, ct);

    [HttpPut("settings/p2p")]
    [Authorize(Policy = Policies.Admin)]
    public Task<P2PSettings> P2P(P2PSettings value, CancellationToken ct) => SaveAsync(value, ct);

    [HttpPut("settings/kyc-limits")]
    [Authorize(Policy = Policies.Admin)]
    public Task<KycLimitSettings> KycLimits(KycLimitSettings value, CancellationToken ct) => SaveAsync(value, ct);

    [HttpGet("assets")]
    public async Task<IReadOnlyList<AssetDto>> Assets(CancellationToken ct) =>
        (await db.Assets.AsNoTracking().OrderBy(a => a.SortOrder).ToListAsync(ct)).Select(a => a.ToDto()).ToList();

    [HttpPut("assets/{code}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<AssetDto> UpdateAsset(string code, AdminAssetUpdateRequest request, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Code == code.ToUpperInvariant(), ct) ?? throw AppException.NotFound("Asset");
        if (request.MinDeposit < 0 || request.MinWithdrawal < 0 || request.WithdrawalFee < 0 || request.RequiredConfirmations is < 0 or > 1000)
        {
            throw AppException.Validation("Values cannot be negative.");
        }

        foreach (var value in new[] { request.MinDeposit, request.MinWithdrawal, request.WithdrawalFee })
        {
            if (!MoneyMath.HasMaxDecimals(value, asset.Precision))
            {
                throw AppException.Validation($"{asset.Code} values can have at most {asset.Precision} decimals.");
            }
        }

        var before = new { asset.MinDeposit, asset.MinWithdrawal, asset.WithdrawalFee, asset.RequiredConfirmations, asset.DepositsEnabled, asset.WithdrawalsEnabled, asset.TradingEnabled };
        asset.MinDeposit = request.MinDeposit;
        asset.MinWithdrawal = request.MinWithdrawal;
        asset.WithdrawalFee = request.WithdrawalFee;
        asset.RequiredConfirmations = asset.IsFiat ? 0 : Math.Max(1, request.RequiredConfirmations);
        asset.DepositsEnabled = request.DepositsEnabled;
        asset.WithdrawalsEnabled = request.WithdrawalsEnabled;
        asset.TradingEnabled = request.TradingEnabled;
        asset.UpdatedAt = clock.GetUtcNow();
        audit.Record(AuditActions.AdminAssetChanged, null, new AuditContext(CurrentUserId, null), "asset", asset.Code, new { before, after = request });
        await db.SaveChangesAsync(ct);
        catalog.Invalidate();
        return asset.ToDto();
    }

    [HttpGet("audit")]
    [Authorize(Policy = Policies.Compliance)]
    public async Task<PageDto<AdminAuditDto>> AuditLog([FromQuery] Guid? userId, [FromQuery] string? action, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = db.AuditLogs.AsNoTracking();
        if (userId is not null)
        {
            query = query.Where(a => a.UserId == userId || a.ActorUserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action.StartsWith(action));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.Id).Skip(request.Skip).Take(request.SafePageSize)
            .Select(a => new AdminAuditDto(a.Id, a.UserId, a.ActorUserId, a.Action, a.EntityType, a.EntityId, a.IpAddress, a.Data, a.CreatedAt))
            .ToListAsync(ct);
        return items.ToPage(request, total);
    }

    private async Task<T> SaveAsync<T>(T value, CancellationToken ct)
        where T : class, IValidatableSetting, new()
    {
        var saved = await settings.SaveAsync(value, CurrentUserId, ct);
        await audit.RecordNowAsync(AuditActions.AdminSettingsChanged, null, new AuditContext(CurrentUserId, null), "settings", SettingsService.KeyFor<T>(), value, ct);
        return saved;
    }
}
