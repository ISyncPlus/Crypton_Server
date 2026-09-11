using Crypton.Api.Contracts;
using Crypton.Core.Domain;
using Crypton.Tests.Infrastructure;

namespace Crypton.Tests.Integration;

[Collection(ApiCollection.Name)]
public class P2PTests(CryptonFactory factory)
{
    private async Task<(TestUser User, BankAccountDto Bank)> TraderAsync(decimal usdt = 0m, decimal ngn = 0m)
    {
        var balances = new Dictionary<string, decimal>();
        if (usdt > 0)
        {
            balances[AssetCodes.USDT] = usdt;
        }

        if (ngn > 0)
        {
            balances[AssetCodes.NGN] = ngn;
        }

        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, twoFactor: true, balances: balances);
        var bank = await user.Client.PostOk<BankAccountDto>("/api/fiat/bank-accounts", new { bankCode = "044", accountNumber = $"{Random.Shared.Next(100_000_000, 999_999_999)}1" });
        return (user, bank);
    }

    private async Task ReauthenticateAsync(params TestUser[] users)
    {
        foreach (var user in users)
        {
            await TestAccounts.SignInAsync(factory, user);
        }
    }

    private static object SellAd(Guid bankId, decimal quantity = 500m, decimal price = 1_560m) => new
    {
        side = "Sell",
        asset = "USDT",
        priceType = "Fixed",
        fixedPrice = price,
        floatingMarginBps = 0,
        totalQuantity = quantity,
        minOrderFiat = 10_000m,
        maxOrderFiat = 800_000m,
        paymentWindowMinutes = 15,
        paymentMethodIds = new[] { bankId },
        terms = "Pay from an account in your own name.",
    };

    [Fact]
    public async Task Sell_ad_order_is_escrowed_paid_and_released()
    {
        var (seller, sellerBank) = await TraderAsync(usdt: 1_000m);
        var (buyer, _) = await TraderAsync();
        var feesBefore = await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Fees, AssetCodes.USDT);

        var ad = await seller.Client.PostOk<MyAdDto>("/api/p2p/ads", SellAd(sellerBank.Id));
        Assert.Equal(501.25m, ad.ReservedAmount);
        Assert.Equal(498.75m, await TestAccounts.AvailableAsync(factory, seller.Id, AssetCodes.USDT));
        Assert.Equal(501.25m, await TestAccounts.BalanceAsync(factory, seller.Id, AssetCodes.USDT, AccountKind.P2PReserve));

        var market = await buyer.Client.GetOk<PageDto<MarketAdDto>>("/api/p2p/market?side=buy&asset=USDT&pageSize=100");
        var listed = Assert.Single(market.Items, a => a.Id == ad.Id);
        Assert.Equal(1_560m, listed.Price);
        Assert.Contains("Access Bank", listed.PaymentBanks);

        var own = await seller.Client.PostProblem("/api/p2p/orders", new { adId = ad.Id, fiatAmount = 156_000m });
        Assert.Equal("validation_error", own.Code);

        var order = await buyer.Client.PostOk<P2POrderDto>("/api/p2p/orders", new { adId = ad.Id, fiatAmount = 156_000m });
        Assert.Equal("buyer", order.MyRole);
        Assert.Equal(100m, order.Quantity);
        Assert.Equal(156_000m, order.FiatAmount);
        Assert.Equal(P2POrderStatus.PendingPayment, order.Status);
        Assert.Equal(sellerBank.AccountNumber, Assert.Single(order.PaymentDetails).AccountNumber);
        Assert.Equal(401m, await TestAccounts.BalanceAsync(factory, seller.Id, AssetCodes.USDT, AccountKind.P2PReserve));
        await LedgerAssert.InvariantsHoldAsync(factory);

        var sellerView = await seller.Client.GetOk<P2POrderDto>($"/api/p2p/orders/{order.Id}");
        Assert.Equal("seller", sellerView.MyRole);
        Assert.Equal(0.25m, sellerView.Fee);

        var sellerMarksPaid = await seller.Client.PostProblem($"/api/p2p/orders/{order.Id}/paid", new { });
        Assert.Equal("forbidden", sellerMarksPaid.Code);

        var paid = await buyer.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{order.Id}/paid", new { paymentReference = "GTB-8822" });
        Assert.Equal(P2POrderStatus.Paid, paid.Status);

        var buyerReleases = await buyer.Client.PostProblem($"/api/p2p/orders/{order.Id}/release", new { twoFactorCode = TestAccounts.NextCode(factory, buyer) });
        Assert.Equal("forbidden", buyerReleases.Code);

        var released = await seller.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{order.Id}/release", new { twoFactorCode = TestAccounts.NextCode(factory, seller) });
        Assert.Equal(P2POrderStatus.Completed, released.Status);
        Assert.Equal(100m, await TestAccounts.AvailableAsync(factory, buyer.Id, AssetCodes.USDT));
        Assert.Equal(feesBefore + 0.25m, await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Fees, AssetCodes.USDT));
        await LedgerAssert.InvariantsHoldAsync(factory);

        var withFeedback = await buyer.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{order.Id}/feedback", new { positive = true, comment = "Fast release" });
        Assert.True(withFeedback.FeedbackGiven);
        var profile = await buyer.Client.GetOk<TraderProfileDto>($"/api/p2p/traders/{seller.Id}");
        Assert.Equal(1, profile.Trader.PositiveFeedback);
        Assert.Equal(1, profile.Trader.CompletedOrders30d);

        var closed = await seller.Client.PostOk<MyAdDto>($"/api/p2p/ads/{ad.Id}/close");
        Assert.Equal(P2PAdStatus.Closed, closed.Status);
        Assert.Equal(899.75m, await TestAccounts.AvailableAsync(factory, seller.Id, AssetCodes.USDT));
        Assert.Equal(0m, await TestAccounts.BalanceAsync(factory, seller.Id, AssetCodes.USDT, AccountKind.P2PReserve));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Buy_ad_escrows_the_taker_and_charges_the_maker()
    {
        var (maker, _) = await TraderAsync();
        var (taker, takerBank) = await TraderAsync(usdt: 300m);

        var ad = await maker.Client.PostOk<MyAdDto>("/api/p2p/ads", new
        {
            side = "Buy",
            asset = "USDT",
            priceType = "Floating",
            floatingMarginBps = -100,
            totalQuantity = 200m,
            minOrderFiat = 10_000m,
            maxOrderFiat = 500_000m,
            paymentWindowMinutes = 30,
        });
        Assert.Equal(0m, ad.ReservedAmount);
        Assert.Equal(1_534.5m, ad.EffectivePrice);

        var needsBank = await taker.Client.PostProblem("/api/p2p/orders", new { adId = ad.Id, quantity = 50m });
        Assert.Equal("validation_error", needsBank.Code);

        var order = await taker.Client.PostOk<P2POrderDto>("/api/p2p/orders", new { adId = ad.Id, quantity = 50m, paymentMethodId = takerBank.Id });
        Assert.Equal("seller", order.MyRole);
        Assert.Equal(250m, await TestAccounts.AvailableAsync(factory, taker.Id, AssetCodes.USDT));

        await maker.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{order.Id}/paid", new { });
        await taker.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{order.Id}/release", new { twoFactorCode = TestAccounts.NextCode(factory, taker) });

        Assert.Equal(49.875m, await TestAccounts.AvailableAsync(factory, maker.Id, AssetCodes.USDT));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Cancel_and_expiry_return_escrow_to_the_ad()
    {
        var (seller, bank) = await TraderAsync(usdt: 600m);
        var (buyer, _) = await TraderAsync();
        var ad = await seller.Client.PostOk<MyAdDto>("/api/p2p/ads", SellAd(bank.Id, quantity: 300m));

        var cancelled = await buyer.Client.PostOk<P2POrderDto>("/api/p2p/orders", new { adId = ad.Id, quantity = 100m });
        await buyer.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{cancelled.Id}/cancel", new { reason = "Changed my mind" });

        var expiring = await buyer.Client.PostOk<P2POrderDto>("/api/p2p/orders", new { adId = ad.Id, quantity = 100m });
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        await TestAccounts.SignInAsync(factory, buyer); // access tokens live 15 minutes
        await TestAccounts.SignInAsync(factory, seller);
        var late = await buyer.Client.PostProblem($"/api/p2p/orders/{expiring.Id}/paid", new { });
        Assert.Equal("invalid_state", late.Code);
        await buyer.Client.PostNoContent("/api/dev/jobs/p2p-expiry");
        Assert.Equal(P2POrderStatus.Expired, (await buyer.Client.GetOk<P2POrderDto>($"/api/p2p/orders/{expiring.Id}")).Status);

        var mine = await seller.Client.GetOk<List<MyAdDto>>("/api/p2p/my-ads");
        var current = mine.Single(a => a.Id == ad.Id);
        Assert.Equal(300m, current.RemainingQuantity);
        Assert.Equal(300.75m, current.ReservedAmount);
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Partial_fills_consume_the_reserve_exactly()
    {
        var (seller, bank) = await TraderAsync(usdt: 1_000m);
        var (buyer, _) = await TraderAsync();
        var ad = await seller.Client.PostOk<MyAdDto>("/api/p2p/ads", SellAd(bank.Id, quantity: 100m, price: 1_550m));
        Assert.Equal(100.25m, ad.ReservedAmount);

        foreach (var qty in new[] { 33.333333m, 33.333333m, 33.333334m })
        {
            var order = await buyer.Client.PostOk<P2POrderDto>("/api/p2p/orders", new { adId = ad.Id, quantity = qty });
            await buyer.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{order.Id}/paid", new { });
            await seller.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{order.Id}/release", new { twoFactorCode = TestAccounts.NextCode(factory, seller) });
        }

        var current = (await seller.Client.GetOk<List<MyAdDto>>("/api/p2p/my-ads")).Single(a => a.Id == ad.Id);
        Assert.Equal(0m, current.RemainingQuantity);
        Assert.Equal(0m, current.ReservedAmount);
        Assert.Equal(100m, await TestAccounts.AvailableAsync(factory, buyer.Id, AssetCodes.USDT));
        Assert.Equal(899.75m, await TestAccounts.AvailableAsync(factory, seller.Id, AssetCodes.USDT));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Disputes_are_resolved_by_compliance()
    {
        var (seller, bank) = await TraderAsync(usdt: 500m);
        var (buyer, _) = await TraderAsync();
        var admin = await TestAccounts.AdminAsync(factory);
        var ad = await seller.Client.PostOk<MyAdDto>("/api/p2p/ads", SellAd(bank.Id, quantity: 200m));

        var first = await buyer.Client.PostOk<P2POrderDto>("/api/p2p/orders", new { adId = ad.Id, quantity = 50m });
        await buyer.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{first.Id}/paid", new { paymentReference = "UBA-123" });
        var tooSoon = await buyer.Client.PostProblem($"/api/p2p/orders/{first.Id}/dispute", new { reason = "Seller is not releasing after payment." });
        Assert.Equal("invalid_state", tooSoon.Code);

        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        await ReauthenticateAsync(buyer, seller, admin);
        var disputed = await buyer.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{first.Id}/dispute", new { reason = "Seller is not releasing after payment." });
        Assert.Equal(P2POrderStatus.Disputed, disputed.Status);

        using (var evidence = new MultipartFormDataContent { { new StringContent("Transfer receipt attached"), "text" } })
        {
            using var response = await buyer.Client.PostAsync($"/api/p2p/orders/{first.Id}/evidence", evidence);
            response.EnsureSuccessStatusCode();
        }

        var cannotRelease = await seller.Client.PostProblem($"/api/p2p/orders/{first.Id}/release", new { twoFactorCode = TestAccounts.NextCode(factory, seller) });
        Assert.Equal("invalid_state", cannotRelease.Code);

        var resolved = await admin.Client.PostOk<AdminDisputeDto>($"/api/admin/p2p/disputes/{disputed.Dispute!.Id}/resolve", new { releaseToBuyer = true, note = "Bank statement confirms payment." });
        Assert.Equal(P2PDisputeStatus.ResolvedToBuyer, resolved.Status);
        Assert.Single(resolved.Evidence);
        Assert.Equal(50m, await TestAccounts.AvailableAsync(factory, buyer.Id, AssetCodes.USDT));

        var second = await buyer.Client.PostOk<P2POrderDto>("/api/p2p/orders", new { adId = ad.Id, quantity = 40m });
        await buyer.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{second.Id}/paid", new { });
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        await ReauthenticateAsync(buyer, seller, admin);
        var disputed2 = await seller.Client.PostOk<P2POrderDto>($"/api/p2p/orders/{second.Id}/dispute", new { reason = "No payment arrived in my account." });
        await admin.Client.PostOk<AdminDisputeDto>($"/api/admin/p2p/disputes/{disputed2.Dispute!.Id}/resolve", new { releaseToBuyer = false, note = "No payment evidence provided." });
        Assert.Equal(P2POrderStatus.ResolvedToSeller, (await seller.Client.GetOk<P2POrderDto>($"/api/p2p/orders/{second.Id}")).Status);
        Assert.Equal(50m, await TestAccounts.AvailableAsync(factory, buyer.Id, AssetCodes.USDT));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Marketplace_rules_are_enforced()
    {
        var unverified = await TestAccounts.CreateAsync(factory, kycTier: 0, balances: new Dictionary<string, decimal> { [AssetCodes.USDT] = 100m });
        var noKyc = await unverified.Client.PostProblem("/api/p2p/ads", new { side = "Buy", asset = "USDT", priceType = "Floating", floatingMarginBps = 0, totalQuantity = 10m, minOrderFiat = 10_000m, maxOrderFiat = 20_000m, paymentWindowMinutes = 15 });
        Assert.Equal("kyc_required", noKyc.Code);

        var (poor, bank) = await TraderAsync(usdt: 10m);
        var tooBig = await poor.Client.PostProblem("/api/p2p/ads", SellAd(bank.Id, quantity: 50m));
        Assert.Equal("insufficient_funds", tooBig.Code);

        var wildPrice = await poor.Client.PostProblem("/api/p2p/ads", SellAd(bank.Id, quantity: 5m, price: 5_000m));
        Assert.Equal("validation_error", wildPrice.Code);

        var ad = await poor.Client.PostOk<MyAdDto>("/api/p2p/ads", SellAd(bank.Id, quantity: 5m));
        using (var delete = await poor.Client.DeleteAsync($"/api/fiat/bank-accounts/{bank.Id}"))
        {
            var problem = await Http.ReadProblem(delete);
            Assert.Equal("invalid_state", problem.Code);
        }

        var (buyer, _) = await TraderAsync();
        var belowMin = await buyer.Client.PostProblem("/api/p2p/orders", new { adId = ad.Id, fiatAmount = 5_000m });
        Assert.Equal("limit_exceeded", belowMin.Code);
    }
}
