using Crypton.Api.Auth;
using Crypton.Api.Contracts;
using Crypton.Api.Infrastructure;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Kyc;
using Crypton.Core.Notifications;
using Crypton.Core.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crypton.Api.Controllers;

[Route("api/me")]
public sealed class AccountController(
    CryptonDbContext db,
    UserManager<AppUser> users,
    TwoFactorService twoFactor,
    TokenService tokens,
    AuthService auth,
    SessionValidator sessions,
    LimitService limits,
    AuditService audit,
    NotificationService notifications,
    IOptions<AppOptions> app) : ApiControllerBase
{
    internal static UserDto ToDto(AppUser u, IEnumerable<string> roles) =>
        new(u.Id, u.Email ?? "", u.FirstName, u.LastName, u.DisplayName, u.KycTier, u.Status, u.TwoFactorEnabled, roles.ToList(), u.WithdrawalsLockedUntil, u.CreatedAt);

    [HttpGet]
    public async Task<MeResponse> Get(CancellationToken ct)
    {
        var user = await RequireUserAsync();
        var usage = await limits.GetUsageAsync(user.Id, user.KycTier, ct);
        var unread = await db.Notifications.CountAsync(n => n.UserId == user.Id && n.ReadAt == null, ct);
        return new MeResponse(ToDto(user, await users.GetRolesAsync(user)),
            usage.Select(u => new LimitUsageDto(u.Kind, u.DailyLimitNgn, u.UsedNgn, u.RemainingNgn)).ToList(), unread);
    }

    [HttpPatch]
    public async Task<UserDto> UpdateProfile(UpdateProfileRequest request, CancellationToken ct)
    {
        var user = await RequireUserAsync();
        if (request.FirstName is not null || request.LastName is not null)
        {
            if (user.KycTier >= 1)
            {
                throw AppException.Validation("Your name is locked after identity verification. Contact support to change it.");
            }

            if (request.FirstName is { } first)
            {
                user.FirstName = RequireName(first);
            }

            if (request.LastName is { } last)
            {
                user.LastName = RequireName(last);
            }
        }

        if (request.DisplayName is { } display && display != user.DisplayName)
        {
            if (await db.Users.AnyAsync(u => u.DisplayName == display && u.Id != user.Id, ct))
            {
                throw AppException.Conflict("display_name_taken", "That display name is taken.");
            }

            user.DisplayName = display;
        }

        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw AppException.Validation(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        audit.Record(AuditActions.ProfileUpdated, user.Id, HttpContext.Audit());
        await db.SaveChangesAsync(ct);
        return ToDto(user, await users.GetRolesAsync(user));
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await RequireUserAsync();
        if (user.TwoFactorEnabled && !await twoFactor.VerifyAuthenticatorCodeAsync(user, request.TwoFactorCode, ct))
        {
            throw new AppException(ErrorCodes.InvalidTwoFactorCode, "Enter a valid authentication code.", 400);
        }

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            var mismatch = result.Errors.Any(e => e.Code == "PasswordMismatch");
            throw AppException.Validation(mismatch ? "Your current password is incorrect." : string.Join(" ", result.Errors.Select(e => e.Description)),
                mismatch ? ErrorCodes.InvalidCredentials : "weak_password");
        }

        await auth.ApplySecurityLockAsync(user, ct);
        await tokens.RevokeAllAsync(user.Id, "password_changed", User.SessionId(), ct);
        audit.Record(AuditActions.PasswordChanged, user.Id, HttpContext.Audit());
        await notifications.QueueAsync(user.Id, NotificationTypes.Security, "Password changed",
            "Your password was changed and your other devices were signed out. Withdrawals are paused for 24 hours.", "/account/security", email: true, ct);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("2fa")]
    public async Task<TwoFactorStatusResponse> TwoFactorStatus()
    {
        var user = await RequireUserAsync();
        return new TwoFactorStatusResponse(user.TwoFactorEnabled, user.TwoFactorEnabled ? await users.CountRecoveryCodesAsync(user) : 0);
    }

    [HttpPost("2fa/setup")]
    public async Task<TwoFactorSetupResponse> SetupTwoFactor()
    {
        var user = await RequireUserAsync();
        if (user.TwoFactorEnabled)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "Two-factor authentication is already on.");
        }

        await users.ResetAuthenticatorKeyAsync(user);
        var key = await users.GetAuthenticatorKeyAsync(user) ?? throw new InvalidOperationException("No authenticator key.");
        var formatted = string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4)))).ToLowerInvariant();
        return new TwoFactorSetupResponse(formatted, Totp.OtpAuthUri(app.Value.Name, user.Email!, key));
    }

    [HttpPost("2fa/enable")]
    public async Task<RecoveryCodesResponse> EnableTwoFactor(TwoFactorCodeRequest request, CancellationToken ct)
    {
        var user = await RequireUserAsync();
        if (user.TwoFactorEnabled)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "Two-factor authentication is already on.");
        }

        if (!await twoFactor.VerifyAuthenticatorCodeAsync(user, request.Code, ct))
        {
            throw new AppException(ErrorCodes.InvalidTwoFactorCode, "That code didn't match. Check your authenticator app's time and try the latest code.", 400);
        }

        user = await RequireUserAsync();
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToList() ?? [];
        audit.Record(AuditActions.TwoFactorEnabled, user.Id, HttpContext.Audit());
        await notifications.QueueAsync(user.Id, NotificationTypes.Security, "Two-factor authentication enabled",
            "Two-factor authentication is now protecting your sign-ins and withdrawals. Keep your recovery codes somewhere safe.", "/account/security", email: true, ct);
        await db.SaveChangesAsync(ct);
        return new RecoveryCodesResponse(codes);
    }

    [HttpPost("2fa/disable")]
    public async Task<IActionResult> DisableTwoFactor(DisableTwoFactorRequest request, CancellationToken ct)
    {
        var user = await RequireUserAsync();
        if (!user.TwoFactorEnabled)
        {
            return NoContent();
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            throw AppException.Validation("Your password is incorrect.", ErrorCodes.InvalidCredentials);
        }

        var verified = !string.IsNullOrWhiteSpace(request.Code)
            ? await twoFactor.VerifyAuthenticatorCodeAsync(user, request.Code, ct)
            : !string.IsNullOrWhiteSpace(request.RecoveryCode) && (await users.RedeemTwoFactorRecoveryCodeAsync(user, request.RecoveryCode.Replace(" ", ""))).Succeeded;
        if (!verified)
        {
            throw new AppException(ErrorCodes.InvalidTwoFactorCode, "Enter a valid authentication or recovery code.", 400);
        }

        if (await users.IsInRoleAsync(user, Roles.Admin) || await users.IsInRoleAsync(user, Roles.Compliance) || await users.IsInRoleAsync(user, Roles.Support))
        {
            throw AppException.Forbidden("Staff accounts must keep two-factor authentication on.");
        }

        user = await RequireUserAsync();
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await auth.ApplySecurityLockAsync(user, ct);
        audit.Record(AuditActions.TwoFactorDisabled, user.Id, HttpContext.Audit());
        await notifications.QueueAsync(user.Id, NotificationTypes.Security, "Two-factor authentication turned off",
            "Two-factor authentication was turned off. Withdrawals are paused for 24 hours. If this wasn't you, reset your password now.", "/account/security", email: true, ct);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("2fa/recovery-codes")]
    public async Task<RecoveryCodesResponse> RegenerateRecoveryCodes(TwoFactorCodeRequest request, CancellationToken ct)
    {
        var user = await RequireUserAsync();
        if (!user.TwoFactorEnabled)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "Turn on two-factor authentication first.");
        }

        if (!await twoFactor.VerifyAuthenticatorCodeAsync(user, request.Code, ct))
        {
            throw new AppException(ErrorCodes.InvalidTwoFactorCode, "Enter a valid authentication code.", 400);
        }

        user = await RequireUserAsync();
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToList() ?? [];
        audit.Record(AuditActions.RecoveryCodesRegenerated, user.Id, HttpContext.Audit());
        await db.SaveChangesAsync(ct);
        return new RecoveryCodesResponse(codes);
    }

    [HttpGet("sessions")]
    public async Task<IReadOnlyList<SessionDto>> Sessions(CancellationToken ct)
    {
        var userId = CurrentUserId;
        var current = User.SessionId();
        var active = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .OrderByDescending(t => t.LastUsedAt)
            .ToListAsync(ct);
        var starts = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .GroupBy(t => t.FamilyId)
            .Select(g => new { FamilyId = g.Key, Started = g.Min(t => t.CreatedAt) })
            .ToDictionaryAsync(x => x.FamilyId, x => x.Started, ct);

        return active
            .GroupBy(t => t.FamilyId)
            .Select(g => g.First())
            .Select(t => new SessionDto(t.FamilyId, AuthService.DescribeDevice(t.UserAgent ?? ""), t.IpAddress, starts.GetValueOrDefault(t.FamilyId, t.CreatedAt), t.LastUsedAt, t.FamilyId == current))
            .ToList();
    }

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken ct)
    {
        var owns = await db.RefreshTokens.AnyAsync(t => t.FamilyId == sessionId && t.UserId == CurrentUserId, ct);
        if (!owns)
        {
            throw AppException.NotFound("Session");
        }

        await tokens.RevokeFamilyAsync(sessionId, "user_revoked", ct);
        sessions.Invalidate(sessionId);
        await audit.RecordNowAsync(AuditActions.SessionRevoked, CurrentUserId, HttpContext.Audit(), "session", sessionId.ToString(), ct: ct);
        return NoContent();
    }

    [HttpGet("activity")]
    public async Task<IReadOnlyList<ActivityDto>> Activity(CancellationToken ct)
    {
        var userId = CurrentUserId;
        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.UserId == userId && (a.Action.StartsWith("auth.") || a.Action.StartsWith("security.")))
            .OrderByDescending(a => a.CreatedAt)
            .Take(50)
            .Select(a => new ActivityDto(a.Action, a.IpAddress, a.CreatedAt))
            .ToListAsync(ct);
    }

    private async Task<AppUser> RequireUserAsync() =>
        await users.FindByIdAsync(CurrentUserId.ToString()) ?? throw new UnauthorizedAccessException();

    private static string RequireName(string value)
    {
        var name = value.Trim();
        if (name.Length is < 2 or > 100)
        {
            throw AppException.Validation("Names must be 2-100 characters.");
        }

        return name;
    }
}
