using System.Globalization;
using System.Text.Json;
using Crypton.Core.Common;
using Crypton.Core.Domain;
using Crypton.Core.Pricing;
using Microsoft.Extensions.Options;

namespace Crypton.Integrations.Pricing;

public sealed class CoinGeckoOptions
{
    public const string Section = "Pricing:CoinGecko";

    /// <summary>Demo | Pro</summary>
    public string Plan { get; set; } = "Demo";

    /// <summary>Optional for Demo (keyless works with lower limits), required for Pro.</summary>
    public string ApiKey { get; set; } = "";

    public Dictionary<string, string> CoinIds { get; set; } = new()
    {
        [AssetCodes.BTC] = "bitcoin",
        [AssetCodes.ETH] = "ethereum",
        [AssetCodes.USDT] = "tether",
    };
}

public sealed class CoinGeckoPriceProvider(IHttpClientFactory http, IOptions<CoinGeckoOptions> options, TimeProvider clock) : IPriceProvider
{
    public const string HttpClientName = "coingecko";

    public string Name => "coingecko";

    public async Task<IReadOnlyList<AssetPrice>> FetchAsync(IReadOnlyList<Asset> cryptoAssets, CancellationToken ct)
    {
        var o = options.Value;
        var ids = cryptoAssets
            .Where(a => o.CoinIds.ContainsKey(a.Code))
            .ToDictionary(a => o.CoinIds[a.Code], a => a.Code);
        if (ids.Count == 0)
        {
            return [];
        }

        var pro = string.Equals(o.Plan, "Pro", StringComparison.OrdinalIgnoreCase);
        var baseUrl = pro ? "https://pro-api.coingecko.com/api/v3/" : "https://api.coingecko.com/api/v3/";
        var url = $"{baseUrl}simple/price?ids={string.Join(',', ids.Keys)}&vs_currencies=ngn,usd&include_24hr_change=true&include_last_updated_at=true&precision=full";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("Crypton/1.0");
        if (!string.IsNullOrWhiteSpace(o.ApiKey))
        {
            request.Headers.Add(pro ? "x-cg-pro-api-key" : "x-cg-demo-api-key", o.ApiKey.Trim());
        }

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement, ids, clock.GetUtcNow());
    }

    internal static IReadOnlyList<AssetPrice> Parse(JsonElement root, IReadOnlyDictionary<string, string> coinIdToAsset, DateTimeOffset now)
    {
        var result = new List<AssetPrice>();
        foreach (var (coinId, asset) in coinIdToAsset)
        {
            if (!root.TryGetProperty(coinId, out var coin) || coin.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var ngn = ReadDecimal(coin, "ngn");
            var usd = ReadDecimal(coin, "usd");
            if (ngn is not > 0 || usd is not > 0)
            {
                continue;
            }

            var change = ReadDecimal(coin, "ngn_24h_change") ?? ReadDecimal(coin, "usd_24h_change");
            var updated = coin.TryGetProperty("last_updated_at", out var ts) && ts.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : now;

            result.Add(new AssetPrice(
                asset,
                MoneyMath.RoundHalfUp(ngn.Value, 2),
                MoneyMath.RoundHalfUp(usd.Value, 6),
                change is null ? null : MoneyMath.RoundHalfUp(change.Value, 2),
                updated > now ? now : updated,
                "coingecko"));
        }

        return result;
    }

    private static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetDecimal(out var d))
            {
                return d;
            }

            return decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        return null;
    }
}
