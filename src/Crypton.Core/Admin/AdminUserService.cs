using Crypton.Core.Assets;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Ledger;
using Crypton.Core.Notifications;
using Crypton.Core.Settings;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Admin;

public sealed record AdminUserListItem(Guid Id, string Email, string FullName, string? DisplayName, int KycTier, UserStatus Status, bool TwoFactorEnabled, bool EmailConfirmed, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

public sealed class AdminUserService(
    CryptonDbContext db,
    UserManager<AppUser> users,
    LedgerService ledger,
    AssetCatalog assets,
    SettingsService settings,
    AuditService audit,
    NotificationService notifications,
    TimeProvider clock)
{
    public async Task<PagedResult<AdminUserListItem>> SearchAsync(string? query, UserStatus? status, int? tier, PageRequest page, CancellationToken ct = default)
    {
        var q = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToUpperInvariant();
            if (Guid.TryParse(term, out var id))
            {
                q = q.Where(u => u.Id == id);
            }
            else
            {
                var pattern = $"%{term.Replace("%", "").Replace("_", "")}%";
                q = q.Where(u => EF.Functions.ILike(u.Email!, pattern) || EF.Functions.ILike(u.FirstName + " " + u.LastName, pattern) || EF.Functions.ILike(u.DisplayName ?? "", pattern));
            }
        }

        if (status is not null)
        {
            q = q.Where(u => u.Status == status);
        }

        if (tier is not null)
        {
            q = q.Where(u => u.KycTier == tier);
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(u => u.CreatedAt)
            .Skip(page.Skip).Take(page.SafePageSize)
            .Select(u => new AdminUserListItem(u.Id, u.Email!, u.FirstName + " " + u.LastName, u.DisplayName, u.KycTier, u.Status, u.TwoFactorEnabled, u.EmailConfirmed, u.CreatedAt, u.LastLoginAt))
            .ToListAsync(ct);
        return new PagedResult<AdminUserListItem>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<AppUser> FreezeAsync(Guid adminId, Guid userId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw AppException.Validation("A reason is required.");
        }

        if (adminId == userId)
        {
            throw AppException.Validation("You cannot freeze your own account.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound("User");
        user.Status = UserStatus.Frozen;
        user.FrozenReason = reason.Trim();
        await RevokeSessionsInternalAsync(userId, "account_frozen", ct);
        audit.Record(AuditActions.AdminUserFrozen, userId, new AuditContext(adminId, null), "user", userId.ToString(), new { reason });
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<AppUser> UnfreezeAsync(Guid adminId, Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound("User");
        user.Status = UserStatus.Active;
        user.FrozenReason = null;
        audit.Record(AuditActions.AdminUserUnfrozen, userId, new AuditContext(adminId, null), "user", userId.ToString());
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task ResetTwoFactorAsync(Guid adminId, Guid userId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw AppException.Validation("A reason is required (e.g. identity re-verified via video call).");
        }

        var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.NotFound("User");
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        var lockHours = (await settings.GetAsync<WithdrawalSettings>(ct)).SecurityLockHours;
        user.WithdrawalsLockedUntil = clock.GetUtcNow().AddHours(Math.Max(lockHours, 24));
        await users.UpdateAsync(user);
        await RevokeSessionsInternalAsync(userId, "admin_2fa_reset", ct);
        audit.Record(AuditActions.AdminTwoFactorReset, userId, new AuditContext(adminId, null), "user", userId.ToString(), new { reason });
        await notifications.QueueAsync(userId, NotificationTypes.Security, "Two-factor authentication was reset",
            "Our support team reset two-factor authentication on your account at your request. Withdrawals are paused for 24 hours. If you did not request this, contact support immediately.",
            "/account/security", email: true, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetRolesAsync(Guid adminId, Guid userId, IReadOnlyCollection<string> roles, CancellationToken ct = default)
    {
        var invalid = roles.Where(r => !Roles.All.Contains(r)).ToList();
        if (invalid.Count > 0)
        {
            throw AppException.Validation($"Unknown roles: {string.Join(", ", invalid)}.");
        }

        var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.NotFound("User");
        var current = await users.GetRolesAsync(user);
        if (adminId == userId && current.Contains(Roles.Admin) && !roles.Contains(Roles.Admin))
        {
            throw AppException.Validation("You cannot remove your own Admin role.");
        }

        if (roles.Count > 0 && !user.TwoFactorEnabled)
        {
            throw AppException.Validation("Staff accounts must have two-factor authentication enabled first.");
        }

        var toRemove = current.Except(roles).ToList();
        var toAdd = roles.Except(current).ToList();
        if (toRemove.Count > 0)
        {
            await users.RemoveFromRolesAsync(user, toRemove);
        }

        if (toAdd.Count > 0)
        {
            await users.AddToRolesAsync(user, toAdd);
        }

        await RevokeSessionsInternalAsync(userId, "roles_changed", ct);
        audit.Record(AuditActions.AdminRolesChanged, userId, new AuditContext(adminId, null), "user", userId.ToString(), new { from = current, to = roles });
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeSessionsAsync(Guid adminId, Guid userId, CancellationToken ct = default)
    {
        await RevokeSessionsInternalAsync(userId, "admin_revoked", ct);
        audit.Record(AuditActions.AdminSessionsRevoked, userId, new AuditContext(adminId, null), "user", userId.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task AdjustBalanceAsync(Guid adminId, Guid userId, string assetCode, decimal amount, string reason, CancellationToken ct = default)
    {
        var asset = await assets.GetAsync(assetCode, ct);
        if (amount == 0 || !MoneyMath.HasMaxDecimals(amount, asset.Precision))
        {
            throw new AppException(ErrorCodes.InvalidAmount, $"Amount must be non-zero with at most {asset.Precision} decimals.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
        {
            throw AppException.Validation("Describe the reason for this adjustment (at least 10 characters).");
        }

        if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
        {
            throw AppException.NotFound("User");
        }

        var adjustmentId = Ids.New();
        await db.InTransactionAsync(async token =>
        {
            await ledger.PostAsync(
                new JournalBuilder(JournalTypes.AdminAdjustment)
                    .ForUser(userId)
                    .Reference("admin_adjustment", adjustmentId)
                    .Idempotent($"admin_adjustment:{adjustmentId}")
                    .Describe(reason.Trim())
                    .System(SystemAccounts.Adjustments, asset.Code, -amount)
                    .User(userId, asset.Code, AccountKind.Available, amount),
                token);
            audit.Record(AuditActions.AdminBalanceAdjusted, userId, new AuditContext(adminId, null), "user", userId.ToString(), new { asset = asset.Code, amount, reason });
            await notifications.QueueAsync(userId, NotificationTypes.System, "Balance adjustment",
                $"Your {asset.Code} balance was adjusted by {MoneyMath.ToPlainString(amount)}. Reason: {reason.Trim()}", "/wallets", email: true, token);
            await db.SaveChangesAsync(token);
        }, ct);
    }

    private async Task RevokeSessionsInternalAsync(Guid userId, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, reason), ct);
    }
}
