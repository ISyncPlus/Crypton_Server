using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Crypton.Core.Settings;

public sealed class SettingsService(CryptonDbContext db, IMemoryCache cache, TimeProvider clock)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);

    public static string KeyFor<T>() => typeof(T).Name switch
    {
        nameof(TradingSettings) => "trading",
        nameof(WithdrawalSettings) => "withdrawals",
        nameof(FiatSettings) => "fiat",
        nameof(P2PSettings) => "p2p",
        nameof(KycLimitSettings) => "kyc_limits",
        nameof(AmlSettings) => "aml",
        _ => throw new InvalidOperationException($"No settings key for {typeof(T).Name}"),
    };

    public async Task<T> GetAsync<T>(CancellationToken ct = default)
        where T : class, IValidatableSetting, new()
    {
        var key = KeyFor<T>();
        if (cache.TryGetValue(CacheKey(key), out T? cached) && cached is not null)
        {
            return cached;
        }

        var row = await db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        var value = Json.Deserialize<T>(row?.Value) ?? new T();
        cache.Set(CacheKey(key), value, CacheDuration);
        return value;
    }

    public async Task<T> SaveAsync<T>(T value, Guid? updatedBy, CancellationToken ct = default)
        where T : class, IValidatableSetting, new()
    {
        var errors = value.Validate().ToList();
        if (errors.Count > 0)
        {
            throw new AppException(ErrorCodes.Validation, string.Join(" ", errors));
        }

        var key = KeyFor<T>();
        var row = await db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new PlatformSetting { Key = key };
            db.PlatformSettings.Add(row);
        }

        row.Value = Json.Serialize(value);
        row.UpdatedBy = updatedBy;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey(key));
        return value;
    }

    public void Invalidate<T>() => cache.Remove(CacheKey(KeyFor<T>()));

    private static string CacheKey(string key) => $"settings:{key}";
}
