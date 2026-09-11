using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Security;

/// <summary>Common account-state checks performed before money moves.</summary>
public sealed class AccountGuard(CryptonDbContext db, TimeProvider clock)
{
    public async Task<AppUser> RequireActiveUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw AppException.NotFound("User");

        if (user.Status == UserStatus.Frozen)
        {
            throw AppException.Forbidden("Your account is frozen. Please contact support.", ErrorCodes.AccountFrozen);
        }

        if (user.Status == UserStatus.Closed)
        {
            throw AppException.Forbidden("This account is closed.", ErrorCodes.AccountFrozen);
        }

        return user;
    }

    public async Task<AppUser> RequireCanWithdrawAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await RequireActiveUserAsync(userId, ct);
        if (user.WithdrawalsLockedUntil is { } until && until > clock.GetUtcNow())
        {
            throw new AppException(
                ErrorCodes.WithdrawalsLocked,
                $"Withdrawals are paused until {until:yyyy-MM-dd HH:mm} UTC after a recent security change.",
                403,
                new Dictionary<string, object?> { ["lockedUntil"] = until });
        }

        return user;
    }
}
