using Crypton.Core.Domain;
using Crypton.Core.P2P;
using Crypton.Core.Settings;
using Crypton.Core.Trading;

namespace Crypton.Tests.Unit;

public class TradePricingTests
{
    private static readonly Asset Ngn = new() { Code = "NGN", Precision = 2, IsFiat = true };
    private static readonly Asset Btc = new() { Code = "BTC", Precision = 8 };
    private static readonly Asset Eth = new() { Code = "ETH", Precision = 8 };
    private static readonly TradingSettings Settings = new() { BuyFeeBps = 50, SellFeeBps = 50, SwapFeeBps = 50, SpreadBps = 0 };

    [Fact]
    public void Buy_by_naira_spend_is_exact()
    {
        var quote = new Quote();
        TradeService.PriceBuy(quote, Ngn, Btc, 1_000_000m, AmountSide.From, 150_000_000m, Settings);
        Assert.Equal(1_000_000m, quote.FromAmount);
        Assert.Equal(4_975.13m, quote.Fee);
        Assert.Equal(995_024.87m, quote.TreasuryFromAmount);
        Assert.Equal(0.00663349m, quote.ToAmount);
        Assert.Equal(quote.FromAmount, quote.TreasuryFromAmount + quote.Fee);
    }

    [Fact]
    public void Buy_by_crypto_quantity_is_exact()
    {
        var quote = new Quote();
        TradeService.PriceBuy(quote, Ngn, Btc, 0.01m, AmountSide.To, 150_000_000m, Settings);
        Assert.Equal(0.01m, quote.ToAmount);
        Assert.Equal(1_500_000m, quote.TreasuryFromAmount);
        Assert.Equal(7_500m, quote.Fee);
        Assert.Equal(1_507_500m, quote.FromAmount);
    }

    [Fact]
    public void Sell_is_exact()
    {
        var quote = new Quote();
        TradeService.PriceSell(quote, Btc, Ngn, 0.01m, 150_000_000m, Settings);
        Assert.Equal(1_500_000m, quote.TreasuryToAmount);
        Assert.Equal(7_500m, quote.Fee);
        Assert.Equal(1_492_500m, quote.ToAmount);
        Assert.Equal(quote.TreasuryToAmount, quote.ToAmount + quote.Fee);
    }

    [Fact]
    public void Swap_is_exact()
    {
        var quote = new Quote();
        TradeService.PriceSwap(quote, Btc, Eth, 0.1m, 150_000_000m, 5_000_000m, Settings);
        Assert.Equal(3m, quote.TreasuryToAmount);
        Assert.Equal(0.015m, quote.Fee);
        Assert.Equal(2.985m, quote.ToAmount);
        Assert.Equal(30m, quote.Rate);
    }

    [Fact]
    public void Spread_moves_price_against_the_user()
    {
        var withSpread = Settings with { SpreadBps = 100 };
        var buy = new Quote();
        TradeService.PriceBuy(buy, Ngn, Btc, 0.01m, AmountSide.To, 150_000_000m, withSpread);
        Assert.Equal(151_500_000m, buy.Rate);
        var sell = new Quote();
        TradeService.PriceSell(sell, Btc, Ngn, 0.01m, 150_000_000m, withSpread);
        Assert.Equal(148_500_000m, sell.Rate);
    }

    [Fact]
    public void Rejects_dust_and_extra_decimals()
    {
        Assert.Throws<Crypton.Core.Common.AppException>(() => TradeService.PriceBuy(new Quote(), Ngn, Btc, 0.01m, AmountSide.From, 150_000_000m, Settings));
        Assert.Throws<Crypton.Core.Common.AppException>(() => TradeService.PriceSell(new Quote(), Btc, Ngn, 0.000000001m, 150_000_000m, Settings));
    }
}

public class P2PPricingTests
{
    [Fact]
    public void Sell_ad_fee_reserve_is_exhausted_exactly_by_partial_fills()
    {
        const int bps = 25;
        var reserved = P2PPricing.SellAdReserve(1m, bps, 8);
        Assert.Equal(1.0025m, reserved);

        var remaining = 1m;
        decimal totalFees = 0;
        foreach (var qty in new[] { 0.33333333m, 0.33333333m, 0.33333334m })
        {
            var fee = P2PPricing.SellOrderFee(qty, remaining, reserved, bps, 8);
            Assert.True(fee >= 0);
            reserved -= qty + fee;
            remaining -= qty;
            totalFees += fee;
            Assert.True(reserved >= 0);
        }

        Assert.Equal(0m, remaining);
        Assert.Equal(0m, reserved);
        Assert.Equal(0.0025m, totalFees);
    }

    [Fact]
    public void Sizing_by_fiat_rounds_quantity_down()
    {
        var (quantity, fiat) = P2PPricing.Size(1_551.25m, 100_000m, null, 6);
        Assert.Equal(64.464141m, quantity);
        Assert.Equal(100_000m, fiat);
    }

    [Fact]
    public void Floating_price_applies_margin()
    {
        var ad = new P2PAd { PriceType = P2PPriceType.Floating, FloatingMarginBps = 150 };
        Assert.Equal(1_573.25m, P2PPricing.EffectivePrice(ad, 1_550m));
    }
}
