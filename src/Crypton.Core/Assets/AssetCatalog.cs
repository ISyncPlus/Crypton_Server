using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Crypton.Core.Assets;

public sealed class AssetCatalog(CryptonDbContext db, IMemoryCache cache)
{
    private const string CacheKey = "assets:all";

    public async Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<Asset>? cached) && cached is not null)
        {
            return cached;
        }

        var assets = await db.Assets.AsNoTracking().OrderBy(a => a.SortOrder).ToListAsync(ct);
        cache.Set(CacheKey, (IReadOnlyList<Asset>)assets, TimeSpan.FromSeconds(15));
        return assets;
    }

    public async Task<Asset> GetAsync(string code, CancellationToken ct = default)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant();
        var asset = (await GetAllAsync(ct)).FirstOrDefault(a => a.Code == normalized);
        return asset ?? throw AppException.Validation($"Unsupported asset '{code}'.", "unsupported_asset");
    }

    public async Task<int> PrecisionAsync(string code, CancellationToken ct = default) => (await GetAsync(code, ct)).Precision;

    public void Invalidate() => cache.Remove(CacheKey);
}
