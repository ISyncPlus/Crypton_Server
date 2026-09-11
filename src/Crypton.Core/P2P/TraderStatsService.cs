using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.P2P;

public sealed class TraderStatsService(CryptonDbContext db, TimeProvider clock)
{
    public async Task<string> EnsureDisplayNameAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (!string.IsNullOrEmpty(user.DisplayName))
        {
            return user.DisplayName;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = $"trader{Random.Shared.Next(10_000, 99_999)}";
            if (!await db.Users.AnyAsync(u => u.DisplayName == candidate, ct))
            {
                user.DisplayName = candidate;
                await db.SaveChangesAsync(ct);
                return candidate;
            }
        }

        user.DisplayName = $"trader{Ids.RandomToken(8)}";
        await db.SaveChangesAsync(ct);
        return user.DisplayName;
    }

    public async Task<TraderStats> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var all = await GetManyAsync([userId], ct);
        return all.TryGetValue(userId, out var stats) ? stats : throw AppException.NotFound("Trader");
    }

    public async Task<IReadOnlyDictionary<Guid, TraderStats>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        var since = clock.GetUtcNow().AddDays(-30);

        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.KycTier, u.CreatedAt })
            .ToListAsync(ct);

        var orders = await db.P2POrders.AsNoTracking()
            .Where(o => (ids.Contains(o.BuyerId) || ids.Contains(o.SellerId)) && o.CreatedAt >= since)
            .Select(o => new { o.BuyerId, o.SellerId, o.Status, o.CancelledBy, o.PaidAt, o.CompletedAt, o.CreatedAt })
            .ToListAsync(ct);

        var feedback = await db.P2PFeedback.AsNoTracking()
            .Where(f => ids.Contains(f.ToUserId))
            .GroupBy(f => new { f.ToUserId, f.Positive })
            .Select(g => new { g.Key.ToUserId, g.Key.Positive, Count = g.Count() })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, TraderStats>();
        foreach (var user in users)
        {
            var mine = orders.Where(o => o.BuyerId == user.Id || o.SellerId == user.Id).ToList();
            var completed = mine.Count(o => o.Status == P2POrderStatus.Completed);
            var failedByUser = mine.Count(o =>
                (o.Status == P2POrderStatus.Cancelled && o.CancelledBy == user.Id) ||
                (o.Status == P2POrderStatus.Expired && o.BuyerId == user.Id));
            var denominator = completed + failedByUser;
            var releaseTimes = mine
                .Where(o => o.SellerId == user.Id && o.Status == P2POrderStatus.Completed && o.CompletedAt is not null)
                .Select(o => (o.CompletedAt!.Value - (o.PaidAt ?? o.CreatedAt)).TotalMinutes)
                .ToList();

            result[user.Id] = new TraderStats(
                user.Id,
                user.DisplayName ?? "trader",
                user.KycTier >= 1,
                user.CreatedAt,
                completed,
                denominator == 0 ? 1m : Math.Round((decimal)completed / denominator, 4),
                releaseTimes.Count == 0 ? null : Math.Round(releaseTimes.Average(), 1),
                feedback.Where(f => f.ToUserId == user.Id && f.Positive).Sum(f => f.Count),
                feedback.Where(f => f.ToUserId == user.Id && !f.Positive).Sum(f => f.Count));
        }

        return result;
    }
}
