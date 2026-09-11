using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Crypton.Api.Auth;

/// <summary>Rejects access tokens whose session was revoked or whose account was closed, within seconds.</summary>
public sealed class SessionValidator(CryptonDbContext db, IMemoryCache cache)
{
    public async Task<bool> IsValidAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var key = $"session:{sessionId:N}";
        if (cache.TryGetValue(key, out bool valid))
        {
            return valid;
        }

        var active = await db.RefreshTokens.AsNoTracking().AnyAsync(t => t.FamilyId == sessionId && t.UserId == userId && t.RevokedAt == null, ct);
        if (active)
        {
            var status = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (UserStatus?)u.Status).FirstOrDefaultAsync(ct);
            active = status is not null && status != UserStatus.Closed;
        }

        cache.Set(key, active, active ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(3));
        return active;
    }

    public void Invalidate(Guid sessionId) => cache.Remove($"session:{sessionId:N}");
}
