using Crypton.Core.Domain;
using Crypton.Integrations.Blockchain;
using Crypton.Integrations.Compliance;
using Crypton.Integrations.Pricing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crypton.Tests.Unit;

/// <summary>
/// Read-only checks against real public endpoints. They need internet access and are excluded from the default run:
/// dotnet test --filter "Category=Live"
/// </summary>
[Trait("Category", "Live")]
public class LiveProviderTests
{
    private sealed class DefaultFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(30) };
    }

    [Fact]
    public async Task CoinGecko_returns_naira_prices()
    {
        var provider = new CoinGeckoPriceProvider(new DefaultFactory(), Options.Create(new CoinGeckoOptions()), TimeProvider.System);
        var assets = new[] { new Asset { Code = AssetCodes.BTC }, new Asset { Code = AssetCodes.ETH }, new Asset { Code = AssetCodes.USDT } };
        var prices = await provider.FetchAsync(assets, CancellationToken.None);
        Assert.Equal(3, prices.Count);
        Assert.All(prices, p => Assert.True(p.PriceNgn > p.PriceUsd && p.PriceUsd > 0));
    }

    [Fact]
    public async Task Mempool_testnet4_reports_tip_height()
    {
        var gateway = new BitcoinGateway(new DefaultFactory(), Options.Create(new BitcoinOptions()), NullLogger<BitcoinGateway>.Instance);
        Assert.True(await gateway.GetTipHeightAsync(CancellationToken.None) > 1_000);
    }

    [Fact]
    public async Task Sepolia_rpc_reports_block_number()
    {
        var url = Environment.GetEnvironmentVariable("CRYPTON_LIVE_SEPOLIA_RPC") ?? "https://ethereum-sepolia-rpc.publicnode.com";
        var gateway = new EthereumGateway(Options.Create(new EthereumOptions { RpcUrl = url }), NullLogger<EthereumGateway>.Instance);
        Assert.True(await gateway.GetTipHeightAsync(CancellationToken.None) > 1_000_000);
    }

    [Fact]
    public async Task Chainalysis_oracle_screens_addresses()
    {
        var url = Environment.GetEnvironmentVariable("CRYPTON_LIVE_MAINNET_RPC") ?? "https://ethereum-rpc.publicnode.com";
        var screening = new ChainalysisOracleScreening(Options.Create(new SanctionsOracleOptions { Enabled = true, RpcUrl = url }), new MemoryCache(new MemoryCacheOptions()));

        // Ronin bridge exploiter address, OFAC-listed in April 2022.
        Assert.True(await screening.IsSanctionedAsync(Networks.Ethereum, "0x098B716B8Aaf21512996dC57EB0615e2383E2f96", CancellationToken.None));
        Assert.False(await screening.IsSanctionedAsync(Networks.Ethereum, HdKeys.EthereumAddress(new NBitcoin.Key().PubKey), CancellationToken.None));
    }
}
