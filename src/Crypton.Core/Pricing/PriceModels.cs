using Crypton.Core.Domain;

namespace Crypton.Core.Pricing;

public sealed record AssetPrice(
    string Asset,
    decimal PriceNgn,
    decimal PriceUsd,
    decimal? Change24hPercent,
    DateTimeOffset UpdatedAt,
    string Source);

/// <summary>A market data source (CoinGecko, or the built-in simulator).</summary>
public interface IPriceProvider
{
    string Name { get; }

    /// <summary>Returns NGN and USD prices for the given crypto assets.</summary>
    Task<IReadOnlyList<AssetPrice>> FetchAsync(IReadOnlyList<Asset> cryptoAssets, CancellationToken ct);
}

public sealed class PricingOptions
{
    public const string Section = "Pricing";

    /// <summary>Simulated | CoinGecko</summary>
    public string Provider { get; set; } = "Simulated";

    public int RefreshSeconds { get; set; } = 60;

    public SimulatedPricingOptions Simulated { get; set; } = new();
}

public sealed class SimulatedPricingOptions
{
    public decimal BtcUsd { get; set; } = 95_000m;

    public decimal EthUsd { get; set; } = 3_500m;

    public decimal UsdtUsd { get; set; } = 1m;

    public decimal UsdNgn { get; set; } = 1_550m;

    /// <summary>Max relative move per refresh (0.002 = 0.2%). Set to 0 for fixed prices.</summary>
    public decimal Volatility { get; set; } = 0.002m;
}
