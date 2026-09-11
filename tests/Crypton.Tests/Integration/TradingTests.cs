using System.Net;
using Crypton.Api.Contracts;
using Crypton.Core.Domain;
using Crypton.Tests.Infrastructure;

namespace Crypton.Tests.Integration;

[Collection(ApiCollection.Name)]
public class TradingTests(CryptonFactory factory)
{
    private const decimal BtcNgn = 147_250_000m; // simulated anchor: 95,000 USD x 1,550 NGN
    private const decimal EthNgn = 5_425_000m;

    [Fact]
    public async Task Buy_sell_and_swap_move_exact_amounts()
    {
        await TestAccounts.FundTreasuryAsync(factory, AssetCodes.BTC, 10m);
        await TestAccounts.FundTreasuryAsync(factory, AssetCodes.ETH, 100m);
        await TestAccounts.FundTreasuryAsync(factory, AssetCodes.NGN, 500_000_000m);
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 5_000_000m });
        var feesBefore = await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Fees, AssetCodes.NGN);

        // Buy BTC spending exactly NGN 1,000,000.
        var buyQuote = await user.Client.PostOk<QuoteDto>("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "BTC", amount = "1000000", side = "From" });
        Assert.Equal(1_000_000m, buyQuote.FromAmount);
        Assert.Equal(BtcNgn, buyQuote.Rate);
        var buy = await user.Client.PostOk<TradeOrderDto>("/api/trade/orders", new { quoteId = buyQuote.Id });
        Assert.Equal(buyQuote.ToAmount, buy.ToAmount);

        Assert.Equal(4_000_000m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));
        Assert.Equal(buyQuote.ToAmount, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.BTC));
        Assert.Equal(feesBefore + buyQuote.Fee, await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Fees, AssetCodes.NGN));

        // Swap part of the BTC to ETH.
        var swapAmount = 0.005m;
        var swapQuote = await user.Client.PostOk<QuoteDto>("/api/trade/quotes", new { kind = "Swap", fromAsset = "BTC", toAsset = "ETH", amount = swapAmount });
        await user.Client.PostOk<TradeOrderDto>("/api/trade/orders", new { quoteId = swapQuote.Id });
        Assert.Equal(buyQuote.ToAmount - swapAmount, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.BTC));
        Assert.Equal(swapQuote.ToAmount, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.ETH));

        // Sell all ETH back to naira.
        var sellQuote = await user.Client.PostOk<QuoteDto>("/api/trade/quotes", new { kind = "Sell", fromAsset = "ETH", toAsset = "NGN", amount = swapQuote.ToAmount });
        await user.Client.PostOk<TradeOrderDto>("/api/trade/orders", new { quoteId = sellQuote.Id });
        Assert.Equal(0m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.ETH));
        Assert.Equal(4_000_000m + sellQuote.ToAmount, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));

        var history = await user.Client.GetOk<PageDto<TradeOrderDto>>("/api/trade/orders");
        Assert.Equal(3, history.TotalCount);
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Quotes_expire_and_execute_only_once()
    {
        await TestAccounts.FundTreasuryAsync(factory, AssetCodes.USDT, 100_000m);
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 1_000_000m });

        var quote = await user.Client.PostOk<QuoteDto>("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "USDT", amount = 100_000m });
        var first = await user.Client.PostOk<TradeOrderDto>("/api/trade/orders", new { quoteId = quote.Id, clientOrderId = "order-1" });
        var again = await user.Client.PostOk<TradeOrderDto>("/api/trade/orders", new { quoteId = quote.Id, clientOrderId = "order-1" });
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(900_000m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));

        var stale = await user.Client.PostOk<QuoteDto>("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "USDT", amount = 100_000m });
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        var expired = await user.Client.PostProblem("/api/trade/orders", new { quoteId = stale.Id });
        Assert.Equal("quote_expired", expired.Code);
        Assert.Equal(900_000m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));
    }

    [Fact]
    public async Task Concurrent_executions_never_overdraw()
    {
        await TestAccounts.FundTreasuryAsync(factory, AssetCodes.USDT, 1_000_000m);
        var user = await TestAccounts.CreateAsync(factory, kycTier: 2, balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 500_000m });

        var quotes = new List<QuoteDto>();
        for (var i = 0; i < 12; i++)
        {
            quotes.Add(await user.Client.PostOk<QuoteDto>("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "USDT", amount = 100_000m }));
        }

        var results = await Task.WhenAll(quotes.Select(async q =>
        {
            using var response = await user.Client.PostAsync("/api/trade/orders", new StringContent(System.Text.Json.JsonSerializer.Serialize(new { quoteId = q.Id }), System.Text.Encoding.UTF8, "application/json"));
            return response.StatusCode;
        }));

        Assert.Equal(5, results.Count(s => s == HttpStatusCode.OK));
        Assert.All(results.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Conflict, s));
        Assert.Equal(0m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Tier_limits_and_minimums_are_enforced()
    {
        await TestAccounts.FundTreasuryAsync(factory, AssetCodes.USDT, 1_000_000m);
        var user = await TestAccounts.CreateAsync(factory, kycTier: 0, balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 1_000_000m });

        var tooSmall = await user.Client.PostProblem("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "USDT", amount = 500m });
        Assert.Equal("amount_too_small", tooSmall.Code);

        var ok = await user.Client.PostOk<QuoteDto>("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "USDT", amount = 200_000m });
        await user.Client.PostOk<TradeOrderDto>("/api/trade/orders", new { quoteId = ok.Id });

        var overLimit = await user.Client.PostOk<QuoteDto>("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "USDT", amount = 100_000m });
        var refused = await user.Client.PostProblem("/api/trade/orders", new { quoteId = overLimit.Id });
        Assert.Equal("limit_exceeded", refused.Code);
    }

    [Fact]
    public async Task Insufficient_liquidity_is_reported()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 2, balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 49_000_000m });
        var treasuryBtc = await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Treasury, AssetCodes.BTC);
        var wanted = Math.Round(treasuryBtc + 1m, 8);
        var problem = await user.Client.PostProblem("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "BTC", amount = wanted, side = "To" });
        Assert.True(problem.Code is "insufficient_liquidity" or "amount_too_large", problem.Body);
    }

    [Fact]
    public async Task Frozen_accounts_cannot_trade()
    {
        await TestAccounts.FundTreasuryAsync(factory, AssetCodes.USDT, 100_000m);
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 100_000m });
        var admin = await TestAccounts.AdminAsync(factory);
        await admin.Client.PostNoContent($"/api/admin/users/{user.Id}/freeze", new { reason = "Investigation" });

        await TestAccounts.SignInAsync(factory, user);
        var problem = await user.Client.PostProblem("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "USDT", amount = 10_000m });
        Assert.Equal("account_frozen", problem.Code);
    }
}
