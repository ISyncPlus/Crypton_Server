using System.Text;
using Crypton.Api.Infrastructure;
using Crypton.Core.Aml;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Notifications;
using Crypton.Core.Security;
using Crypton.Core.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crypton.Api.Auth;

public sealed record LoginResult(bool RequiresTwoFactor, string? ChallengeToken, IssuedTokens? Tokens, AppUser? User);

public sealed class AuthService(
    CryptonDbContext db,
    UserManager<AppUser> users,
    TokenService tokens,
    TwoFactorService twoFactor,
    NotificationService notifications,
    AuditService audit,
    AmlEngine aml,
    SettingsService settings,
    IDataProtectionProvider dataProtection,
    IOptions<AppOptions> app,
    TimeProvider clock,
    ILogger<AuthService> logger)
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString()));

    private ITimeLimitedDataProtector ChallengeProtector => dataProtection.CreateProtector("Crypton.Auth.TwoFactorChallenge.v1").ToTimeLimitedDataProtector();

    public async Task RegisterAsync(string email, string password, string firstName, string lastName, HttpContext http, CancellationToken ct)
    {
        email = email.Trim();
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null)
        {
            // Same response either way so emails can't be enumerated; tell the owner instead.
            notifications.QueueEmail(existing.Email!, $"{app.Value.Name} sign-up attempt",
                EmailTemplates.Notification(app.Value, existing.FirstName, "You already have an account",
                    "Someone tried to create an account with this email address. If that was you, sign in or reset your password instead.", "/auth/sign-in"));
            await db.SaveChangesAsync(ct);
            return;
        }

        var user = new AppUser
        {
            Id = Ids.New(),
            UserName = email,
            Email = email,
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            CreatedAt = clock.GetUtcNow(),
        };

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code is "DuplicateUserName" or "DuplicateEmail"))
            {
                return;
            }

            throw AppException.Validation(string.Join(" ", result.Errors.Select(e => e.Description)), "weak_password");
        }

        audit.Record(AuditActions.Register, user.Id, new AuditContext(user.Id, http.ClientIp()));
        await QueueConfirmationEmailAsync(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task ResendConfirmationAsync(string email, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || user.EmailConfirmed)
        {
            return;
        }

        await QueueConfirmationEmailAsync(user);
        await db.SaveChangesAsync(ct);
    }

    public async Task ConfirmEmailAsync(Guid userId, string token, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw InvalidLink();
        if (user.EmailConfirmed)
        {
            return;
        }

        var result = await users.ConfirmEmailAsync(user, DecodeToken(token));
        if (!result.Succeeded)
        {
            throw InvalidLink();
        }

        audit.Record(AuditActions.EmailConfirmed, user.Id);
        await notifications.QueueAsync(user.Id, NotificationTypes.Security, $"Welcome to {app.Value.Name}",
            "Your email is confirmed. Turn on two-factor authentication and verify your identity to unlock naira payments and P2P trading.", "/account/security", email: false, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<LoginResult> LoginAsync(string email, string password, HttpContext http, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            // Spend comparable time to avoid revealing whether the account exists.
            users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, password);
            throw new AppException(ErrorCodes.InvalidCredentials, "Incorrect email or password.", 401);
        }

        if (await users.IsLockedOutAsync(user))
        {
            throw LockedOut(user);
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            await RecordFailedLoginAsync(user, http, ct);
            if (await users.IsLockedOutAsync(user))
            {
                throw LockedOut(user);
            }

            throw new AppException(ErrorCodes.InvalidCredentials, "Incorrect email or password.", 401);
        }

        if (!user.EmailConfirmed)
        {
            throw new AppException(ErrorCodes.EmailNotConfirmed, "Confirm your email address before signing in. We can resend the link.", 403);
        }

        if (user.Status == UserStatus.Closed)
        {
            throw AppException.Forbidden("This account is closed.");
        }

        if (user.TwoFactorEnabled)
        {
            var payload = $"{user.Id:N}|{user.SecurityStamp}";
            var challenge = ChallengeProtector.Protect(payload, ChallengeLifetime);
            return new LoginResult(true, challenge, null, null);
        }

        await users.ResetAccessFailedCountAsync(user);
        var issued = await CompleteLoginAsync(user, twoFactorVerified: false, http, ct);
        return new LoginResult(false, null, issued, user);
    }

    public async Task<LoginResult> CompleteTwoFactorAsync(string challengeToken, string? code, string? recoveryCode, HttpContext http, CancellationToken ct)
    {
        string payload;
        try
        {
            payload = ChallengeProtector.Unprotect(challengeToken);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            throw new AppException(ErrorCodes.InvalidToken, "Your sign-in attempt expired. Please enter your password again.", 401);
        }

        var parts = payload.Split('|');
        var user = parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var id) ? await users.FindByIdAsync(id.ToString()) : null;
        if (user is null || user.SecurityStamp != parts[1] || !user.TwoFactorEnabled)
        {
            throw new AppException(ErrorCodes.InvalidToken, "Your sign-in attempt expired. Please enter your password again.", 401);
        }

        if (await users.IsLockedOutAsync(user))
        {
            throw LockedOut(user);
        }

        var ok = false;
        if (!string.IsNullOrWhiteSpace(code))
        {
            ok = await twoFactor.VerifyAuthenticatorCodeAsync(user, code, ct);
        }
        else if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            ok = (await users.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode.Replace(" ", "").Trim())).Succeeded;
            if (ok)
            {
                await notifications.QueueAsync(user.Id, NotificationTypes.Security, "Recovery code used",
                    $"A recovery code was used to sign in. You have {await users.CountRecoveryCodesAsync(user)} left.", "/account/security", email: true, ct);
            }
        }

        if (!ok)
        {
            await users.AccessFailedAsync(user);
            await RecordFailedLoginAsync(user, http, ct);
            if (await users.IsLockedOutAsync(user))
            {
                throw LockedOut(user);
            }

            throw new AppException(ErrorCodes.InvalidTwoFactorCode, "That code is invalid or has already been used.", 401);
        }

        await users.ResetAccessFailedCountAsync(user);
        var issued = await CompleteLoginAsync(user, twoFactorVerified: true, http, ct);
        return new LoginResult(false, null, issued, user);
    }

    public async Task ForgotPasswordAsync(string email, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || !user.EmailConfirmed)
        {
            return;
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var url = EmailTemplates.Combine(app.Value.FrontendBaseUrl, $"/auth/reset-password?userId={user.Id}&token={EncodeToken(token)}");
        notifications.QueueEmail(user.Email!, "Reset your password", EmailTemplates.ResetPassword(app.Value, user.FirstName, url));
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetPasswordAsync(Guid userId, string token, string newPassword, HttpContext http, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw InvalidLink();
        var result = await users.ResetPasswordAsync(user, DecodeToken(token), newPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "InvalidToken"))
            {
                throw InvalidLink();
            }

            throw AppException.Validation(string.Join(" ", result.Errors.Select(e => e.Description)), "weak_password");
        }

        await ApplySecurityLockAsync(user, ct);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await tokens.RevokeAllAsync(user.Id, "password_reset", null, ct);
        audit.Record(AuditActions.PasswordReset, user.Id, new AuditContext(user.Id, http.ClientIp()));
        await notifications.QueueAsync(user.Id, NotificationTypes.Security, "Your password was reset",
            "Your password was changed and all devices were signed out. Withdrawals are paused for 24 hours as a precaution. If this wasn't you, contact support now.",
            "/account/security", email: true, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task ApplySecurityLockAsync(AppUser user, CancellationToken ct)
    {
        var hours = (await settings.GetAsync<WithdrawalSettings>(ct)).SecurityLockHours;
        if (hours > 0)
        {
            var until = clock.GetUtcNow().AddHours(hours);
            if (user.WithdrawalsLockedUntil is null || user.WithdrawalsLockedUntil < until)
            {
                user.WithdrawalsLockedUntil = until;
                await users.UpdateAsync(user);
            }
        }
    }

    private async Task<IssuedTokens> CompleteLoginAsync(AppUser user, bool twoFactorVerified, HttpContext http, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ip = http.ClientIp();
        var userAgent = http.UserAgent();
        var deviceHash = http.DeviceHash();

        var device = await db.KnownDevices.FirstOrDefaultAsync(d => d.UserId == user.Id && d.DeviceHash == deviceHash, ct);
        if (device is null)
        {
            var hasOtherDevices = await db.KnownDevices.AnyAsync(d => d.UserId == user.Id, ct);
            db.KnownDevices.Add(new KnownDevice
            {
                Id = Ids.New(),
                UserId = user.Id,
                DeviceHash = deviceHash,
                UserAgent = userAgent,
                LastIpAddress = ip,
                FirstSeenAt = now,
                LastSeenAt = now,
            });

            if (hasOtherDevices)
            {
                notifications.QueueEmail(user.Email!, "New sign-in to your account",
                    EmailTemplates.NewDeviceLogin(app.Value, user.FirstName, DescribeDevice(userAgent), ip ?? "unknown", now));
                await notifications.QueueAsync(user.Id, NotificationTypes.Security, "New device signed in", $"{DescribeDevice(userAgent)} from {ip ?? "an unknown IP"}.", "/account/security", email: false, ct);
            }
        }
        else
        {
            device.LastSeenAt = now;
            device.LastIpAddress = ip;
        }

        var tracked = await db.Users.FirstAsync(u => u.Id == user.Id, ct);
        tracked.LastLoginAt = now;
        audit.Record(AuditActions.LoginSucceeded, user.Id, new AuditContext(user.Id, ip), data: new { twoFactor = twoFactorVerified });
        await db.SaveChangesAsync(ct);

        return await tokens.CreateSessionAsync(user, twoFactorVerified, ip, userAgent, ct);
    }

    private async Task RecordFailedLoginAsync(AppUser user, HttpContext http, CancellationToken ct)
    {
        try
        {
            audit.Record(AuditActions.LoginFailed, user.Id, new AuditContext(null, http.ClientIp()));
            await db.SaveChangesAsync(ct);
            var decision = await aml.EvaluateFailedLoginsAsync(user.Id, ct);
            if (decision.Findings.Count > 0)
            {
                aml.QueueAlerts(decision, user.Id, AmlSubjectType.Login, null);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Recording failed login for {UserId} failed", user.Id);
        }
    }

    private async Task QueueConfirmationEmailAsync(AppUser user)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var url = EmailTemplates.Combine(app.Value.FrontendBaseUrl, $"/auth/confirm-email?userId={user.Id}&token={EncodeToken(token)}");
        notifications.QueueEmail(user.Email!, "Confirm your email", EmailTemplates.ConfirmEmail(app.Value, user.FirstName, url));
    }

    private static string EncodeToken(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private static string DecodeToken(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            throw InvalidLink();
        }
    }

    private static AppException InvalidLink() => new(ErrorCodes.InvalidToken, "This link is invalid or has expired. Request a new one.", 400);

    private static AppException LockedOut(AppUser user) => new(ErrorCodes.LockedOut,
        "Too many failed attempts. Your account is temporarily locked; try again in 15 minutes or reset your password.", 423,
        new Dictionary<string, object?> { ["lockedUntil"] = user.LockoutEnd });

    internal static string DescribeDevice(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown device";
        }

        var os = userAgent switch
        {
            _ when userAgent.Contains("iPhone") => "iPhone",
            _ when userAgent.Contains("iPad") => "iPad",
            _ when userAgent.Contains("Android") => "Android",
            _ when userAgent.Contains("Mac OS X") => "macOS",
            _ when userAgent.Contains("Windows") => "Windows",
            _ when userAgent.Contains("Linux") => "Linux",
            _ => "Unknown OS",
        };
        var browser = userAgent switch
        {
            _ when userAgent.Contains("Edg/") => "Edge",
            _ when userAgent.Contains("OPR/") => "Opera",
            _ when userAgent.Contains("Chrome/") => "Chrome",
            _ when userAgent.Contains("Firefox/") => "Firefox",
            _ when userAgent.Contains("Safari/") => "Safari",
            _ => "browser",
        };
        return $"{browser} on {os}";
    }
}
