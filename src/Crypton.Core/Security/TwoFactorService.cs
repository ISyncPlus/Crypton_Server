using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Security;

public sealed class TwoFactorService(CryptonDbContext db, UserManager<AppUser> users, TimeProvider clock)
{
    /// <summary>Verifies an authenticator code and consumes its time step so it cannot be replayed.</summary>
    public async Task<bool> VerifyAuthenticatorCodeAsync(AppUser user, string? code, CancellationToken ct = default)
    {
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var step = Totp.Match(Base32.Decode(key), code, clock.GetUtcNow());
        if (step is null)
        {
            return false;
        }

        var matchedStep = step.Value;
        var userId = user.Id;
        var consumed = await db.Users
            .Where(u => u.Id == userId && u.LastTotpTimeStep < matchedStep)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastTotpTimeStep, matchedStep), ct);

        if (consumed == 1)
        {
            user.LastTotpTimeStep = matchedStep;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Step-up check for money movement and security changes. If the user has 2FA, a valid code is required.
    /// If they don't and <paramref name="requiredByPolicy"/> is set, the action is refused until 2FA is enabled.
    /// </summary>
    public async Task RequireAsync(Guid userId, string? code, bool requiredByPolicy, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.NotFound("User");
        if (!user.TwoFactorEnabled)
        {
            if (requiredByPolicy)
            {
                throw AppException.Forbidden("Turn on two-factor authentication before doing this.", ErrorCodes.TwoFactorRequired);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new AppException(ErrorCodes.InvalidTwoFactorCode, "Enter the 6-digit code from your authenticator app.", 400);
        }

        if (!await VerifyAuthenticatorCodeAsync(user, code, ct))
        {
            throw new AppException(ErrorCodes.InvalidTwoFactorCode, "That authentication code is invalid or has already been used.", 400);
        }
    }
}
