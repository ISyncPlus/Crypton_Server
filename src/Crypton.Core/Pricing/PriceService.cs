using System.Collections.Concurrent;
using Crypton.Core.Assets;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crypton.Core.Pricing;

/// <summary>Process-wide cache of the latest prices. Refreshed by the price job, or on demand when empty.</summary>
public sealed class PriceCache
{
    private readonly ConcurrentDictionary<string, AssetPrice> _prices = new();

    public SemaphoreSlim RefreshLock { get; } = new(1, 1);

    public DateTimeOffset? LastRefreshAt { get; private set; }

    public string? LastError { get; set; }

    public IReadOnlyDictionary<string, AssetPrice> Snapshot => _prices;

    public void Set(IEnumerable<AssetPrice> prices, DateTimeOffset at)
    {
        foreach (var price in prices)
        {
            _prices[price.Asset] = price;
        }

        LastRefreshAt = at;
        LastError = null;
    }

    public void Clear()
    {
        _prices.Clear();
        LastRefreshAt = null;
    }
}

public sealed class PriceService(
    PriceCache cache,
    IPriceProvider provider,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<PriceService> logger)
{
    public async Task RefreshAsync(bool persistTicks, CancellationToken ct)
    {
        await cache.RefreshLock.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<AssetCatalog>();
            var crypto = (await catalog.GetAllAsync(ct)).Where(a => !a.IsFiat).ToList();
            var prices = await provider.FetchAsync(crypto, ct);
            var now = clock.GetUtcNow();
            var valid = prices.Where(p => p.PriceNgn > 0 && p.PriceUsd > 0).ToList();
            if (valid.Count == 0)
            {
                throw new InvalidOperationException($"Price provider '{provider.Name}' returned no usable prices.");
            }

            cache.Set(valid, now);

            if (persistTicks)
            {
                var db = scope.ServiceProvider.GetRequiredService<CryptonDbContext>();
                db.PriceTicks.AddRange(valid.Select(p => new PriceTick
                {
                    Asset = p.Asset,
                    PriceNgn = p.PriceNgn,
                    PriceUsd = p.PriceUsd,
                    Source = p.Source,
                    CreatedAt = now,
                }));
                await db.SaveChangesAsync(ct);
                await db.PriceTicks.Where(t => t.CreatedAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cache.LastError = ex.Message;
            logger.LogWarning(ex, "Price refresh from {Provider} failed", provider.Name);
            throw;
        }
        finally
        {
            cache.RefreshLock.Release();
        }
    }

    /// <summary>Latest prices; refreshes synchronously if nothing has been loaded yet.</summary>
    public async Task<IReadOnlyDictionary<string, AssetPrice>> GetPricesAsync(CancellationToken ct = default)
    {
        if (cache.Snapshot.Count == 0)
        {
            try
            {
                await RefreshAsync(persistTicks: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Callers decide how to handle missing prices.
            }
        }

        return cache.Snapshot;
    }

    /// <summary>NGN price of one unit of <paramref name="asset"/>, failing if it is missing or older than <paramref name="maxAge"/>.</summary>
    public async Task<AssetPrice> GetFreshPriceAsync(string asset, TimeSpan maxAge, CancellationToken ct = default)
    {
        if (asset == AssetCodes.NGN)
        {
            var usdNgn = await UsdNgnAsync(ct);
            return new AssetPrice(AssetCodes.NGN, 1m, usdNgn > 0 ? 1m / usdNgn : 0m, 0m, clock.GetUtcNow(), "fixed");
        }

        var prices = await GetPricesAsync(ct);
        if (!prices.TryGetValue(asset, out var price))
        {
            throw new AppException(ErrorCodes.PriceUnavailable, $"No price is available for {asset} right now.", 503);
        }

        if (clock.GetUtcNow() - price.UpdatedAt > maxAge)
        {
            try
            {
                await RefreshAsync(persistTicks: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AppException(ErrorCodes.PriceUnavailable, $"The {asset} price is out of date and could not be refreshed. Try again shortly.", 503);
            }

            price = cache.Snapshot[asset];
        }

        return price;
    }

    /// <summary>Best-effort NGN valuation used for limits, analytics and AML thresholds.</summary>
    public async Task<decimal> ToNgnAsync(string asset, decimal amount, CancellationToken ct = default)
    {
        if (asset == AssetCodes.NGN)
        {
            return amount;
        }

        var prices = await GetPricesAsync(ct);
        if (prices.TryGetValue(asset, out var price))
        {
            return MoneyMath.RoundHalfUp(amount * price.PriceNgn, 2);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CryptonDbContext>();
        var last = await db.PriceTicks.AsNoTracking()
            .Where(t => t.Asset == asset)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => (decimal?)t.PriceNgn)
            .FirstOrDefaultAsync(ct);

        if (last is null)
        {
            throw new AppException(ErrorCodes.PriceUnavailable, $"No price is available for {asset} right now.", 503);
        }

        return MoneyMath.RoundHalfUp(amount * last.Value, 2);
    }

    private async Task<decimal> UsdNgnAsync(CancellationToken ct)
    {
        var prices = await GetPricesAsync(ct);
        return prices.TryGetValue(AssetCodes.USDT, out var usdt) && usdt.PriceUsd > 0
            ? usdt.PriceNgn / usdt.PriceUsd
            : 0m;
    }
}
