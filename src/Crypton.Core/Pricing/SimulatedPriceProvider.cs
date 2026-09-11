using Crypton.Core.Common;
using Crypton.Core.Domain;
using Microsoft.Extensions.Options;

namespace Crypton.Core.Pricing;

/// <summary>Random-walk prices around configured anchors. For local development, demos and tests.</summary>
public sealed class SimulatedPriceProvider(IOptions<PricingOptions> options, TimeProvider clock) : IPriceProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, decimal> _usd = new();
    private readonly Dictionary<string, decimal> _open = new();

    public string Name => "simulated";

    public Task<IReadOnlyList<AssetPrice>> FetchAsync(IReadOnlyList<Asset> cryptoAssets, CancellationToken ct)
    {
        var o = options.Value.Simulated;
        var now = clock.GetUtcNow();
        var result = new List<AssetPrice>();

        lock (_gate)
        {
            foreach (var asset in cryptoAssets)
            {
                var anchor = asset.Code switch
                {
                    AssetCodes.BTC => o.BtcUsd,
                    AssetCodes.ETH => o.EthUsd,
                    AssetCodes.USDT => o.UsdtUsd,
                    _ => 0m,
                };

                if (anchor <= 0)
                {
                    continue;
                }

                if (!_usd.TryGetValue(asset.Code, out var current))
                {
                    current = anchor;
                    _open[asset.Code] = anchor;
                }
                else if (o.Volatility > 0 && asset.Code != AssetCodes.USDT)
                {
                    var move = ((decimal)Random.Shared.NextDouble() * 2m - 1m) * o.Volatility;
                    current *= 1m + move;

                    // Mean-revert so long sessions stay near the anchor.
                    current += (anchor - current) * 0.05m;
                }

                _usd[asset.Code] = current;
                var usd = MoneyMath.RoundHalfUp(current, 6);
                var ngn = MoneyMath.RoundHalfUp(current * o.UsdNgn, 2);
                var open = _open[asset.Code];
                var change = open == 0 ? 0 : MoneyMath.RoundHalfUp((current - open) / open * 100m, 2);
                result.Add(new AssetPrice(asset.Code, ngn, usd, change, now, Name));
            }
        }

        return Task.FromResult<IReadOnlyList<AssetPrice>>(result);
    }
}
