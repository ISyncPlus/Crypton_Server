using System.Net;
using System.Net.Http.Json;
using Crypton.Api.Contracts;
using Crypton.Core.Admin;
using Crypton.Core.Domain;
using Crypton.Core.Settings;
using Crypton.Tests.Infrastructure;
using NBitcoin;

namespace Crypton.Tests.Integration;

[Collection(ApiCollection.Name)]
public class AdminTests(CryptonFactory factory)
{
    [Fact]
    public async Task Treasury_funding_is_recorded_once_per_reference()
    {
        var admin = await TestAccounts.AdminAsync(factory);
        var before = await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Treasury, AssetCodes.ETH);
        var reference = $"0x{Guid.NewGuid():N}";
        await admin.Client.PostNoContent("/api/admin/treasury/fund", new { asset = "ETH", amount = 12.5m, reference, note = "Top-up from cold storage" });
        Assert.Equal(before + 12.5m, await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Treasury, AssetCodes.ETH));

        var duplicate = await admin.Client.PostProblem("/api/admin/treasury/fund", new { asset = "ETH", amount = 12.5m, reference, note = "Again" });
        Assert.Equal("duplicate_request", duplicate.Code);

        var overview = await admin.Client.GetOk<List<TreasuryAssetView>>("/api/admin/treasury");
        Assert.Equal(4, overview.Count);
        Assert.True(overview.Single(a => a.Asset == "ETH").TreasuryInventory >= 12.5m);
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Balance_adjustments_are_audited()
    {
        var admin = await TestAccounts.AdminAsync(factory);
        var user = await TestAccounts.CreateAsync(factory);
        await admin.Client.PostNoContent($"/api/admin/users/{user.Id}/adjust-balance", new { asset = "NGN", amount = 2_500m, reason = "Goodwill credit for delayed payout" });
        Assert.Equal(2_500m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));

        var tooMuch = await admin.Client.PostProblem($"/api/admin/users/{user.Id}/adjust-balance", new { asset = "NGN", amount = -5_000m, reason = "Reverse an over-credit by mistake" });
        Assert.Equal("insufficient_funds", tooMuch.Code);

        var audit = await admin.Client.GetOk<PageDto<AdminAuditDto>>($"/api/admin/audit?userId={user.Id}&action=admin.");
        Assert.Contains(audit.Items, a => a.Action == "admin.balance_adjusted");

        var detail = await admin.Client.GetOk<AdminUserDetailDto>($"/api/admin/users/{user.Id}");
        Assert.Equal(2_500m, detail.Balances.Single(b => b.Asset == "NGN").Available);
    }

    [Fact]
    public async Task Settings_are_validated_and_applied()
    {
        var admin = await TestAccounts.AdminAsync(factory);
        var settings = await admin.Client.GetOk<AdminSettingsDto>("/api/admin/settings");

        var invalid = await admin.Client.PutProblemAsync("/api/admin/settings/trading", settings.Trading with { BuyFeeBps = 9_000 });
        Assert.Equal("validation_error", invalid);

        try
        {
            var updated = await admin.Client.PutOk<TradingSettings>("/api/admin/settings/trading", settings.Trading with { QuoteTtlSeconds = 45 });
            Assert.Equal(45, updated.QuoteTtlSeconds);
            var reread = await admin.Client.GetOk<AdminSettingsDto>("/api/admin/settings");
            Assert.Equal(45, reread.Trading.QuoteTtlSeconds);
        }
        finally
        {
            await admin.Client.PutOk<TradingSettings>("/api/admin/settings/trading", settings.Trading);
        }
    }

    [Fact]
    public async Task Asset_toggles_pause_activity()
    {
        var admin = await TestAccounts.AdminAsync(factory);
        var assets = await admin.Client.GetOk<List<AssetDto>>("/api/admin/assets");
        var eth = assets.Single(a => a.Code == "ETH");
        await admin.Client.PutOk<AssetDto>("/api/admin/assets/ETH", new { eth.MinDeposit, eth.MinWithdrawal, eth.WithdrawalFee, eth.RequiredConfirmations, eth.DepositsEnabled, eth.WithdrawalsEnabled, tradingEnabled = false });
        try
        {
            var user = await TestAccounts.CreateAsync(factory, kycTier: 1, balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 100_000m });
            var paused = await user.Client.PostProblem("/api/trade/quotes", new { kind = "Buy", fromAsset = "NGN", toAsset = "ETH", amount = 50_000m });
            Assert.Equal("asset_disabled", paused.Code);
        }
        finally
        {
            await admin.Client.PutOk<AssetDto>("/api/admin/assets/ETH", new { eth.MinDeposit, eth.MinWithdrawal, eth.WithdrawalFee, eth.RequiredConfirmations, eth.DepositsEnabled, eth.WithdrawalsEnabled, eth.TradingEnabled });
        }
    }

    [Fact]
    public async Task Staff_roles_require_two_factor()
    {
        var admin = await TestAccounts.AdminAsync(factory);
        var plain = await TestAccounts.CreateAsync(factory);
        var problem = await admin.Client.PutProblemAsync($"/api/admin/users/{plain.Id}/roles", new { roles = new[] { "Support" } });
        Assert.Equal("validation_error", problem);

        var secured = await TestAccounts.CreateAsync(factory, twoFactor: true);
        using (var response = await admin.Client.PutAsync($"/api/admin/users/{secured.Id}/roles", System.Net.Http.Json.JsonContent.Create(new { roles = new[] { "Support" } })))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        await TestAccounts.SignInAsync(factory, secured);
        await secured.Client.GetOk<AdminDashboardDto>("/api/admin/dashboard");
        using var forbidden = await secured.Client.PostAsync("/api/admin/treasury/fund", System.Net.Http.Json.JsonContent.Create(new { asset = "NGN", amount = 1m, reference = "x", note = "y" }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Reports_and_analytics_work()
    {
        var admin = await TestAccounts.AdminAsync(factory);
        using (var csv = await admin.Client.GetAsync("/api/admin/reports/ledger.csv"))
        {
            csv.EnsureSuccessStatusCode();
            var text = await csv.Content.ReadAsStringAsync();
            Assert.StartsWith("posting_id,created_at,journal_entry_id", text);
        }

        var series = await admin.Client.GetOk<List<TimePoint>>("/api/admin/analytics/timeseries?metric=trade_volume");
        Assert.InRange(series.Count, 30, 32);

        var ledger = await admin.Client.GetOk<LedgerCheckReport>("/api/admin/system/ledger-check");
        Assert.True(ledger.Ok, string.Join("; ", ledger.Checks.Where(c => !c.Ok).Select(c => c.Name)));
    }

    [Fact]
    public async Task Aml_rules_can_be_tuned_and_flag_rapid_in_out()
    {
        var admin = await TestAccounts.AdminAsync(factory);
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, twoFactor: true);
        await user.Client.GetOk<DepositAddressDto>("/api/wallets/BTC/address");
        await user.Client.PostOk<CryptoDepositDto>("/api/dev/simulate/crypto-deposit", new { asset = "BTC", amount = 0.004m });
        await user.Client.PostNoContent("/api/dev/jobs/deposits-bitcoin");
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await user.Client.PostNoContent("/api/dev/jobs/deposits-bitcoin");
        Assert.Equal(0.004m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.BTC));

        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet).ToString();
        var withdrawal = await user.Client.PostOk<CryptoWithdrawalDto>("/api/wallets/withdrawals", new { asset = "BTC", address = destination, amount = 0.0035m, twoFactorCode = TestAccounts.NextCode(factory, user) });
        Assert.Equal(CryptoWithdrawalStatus.PendingReview, withdrawal.Status);

        var alerts = await admin.Client.GetOk<PageDto<AdminAlertDto>>($"/api/admin/aml/alerts?userId={user.Id}");
        var alert = Assert.Single(alerts.Items, a => a.RuleCode == "rapid_in_out");
        await admin.Client.PostNoContent($"/api/admin/aml/alerts/{alert.Id}/resolve", new { status = "Dismissed", note = "Customer moving to own wallet" });

        var rules = await admin.Client.GetOk<AmlSettings>("/api/admin/aml/rules");
        try
        {
            var tuned = rules with { Rules = rules.Rules.Select(r => r.Code == "rapid_in_out" ? r with { Enabled = false } : r).ToList() };
            await admin.Client.PutOk<AmlSettings>("/api/admin/aml/rules", tuned);
            var reread = await admin.Client.GetOk<AmlSettings>("/api/admin/aml/rules");
            Assert.False(reread.Rules.Single(r => r.Code == "rapid_in_out").Enabled);
        }
        finally
        {
            await admin.Client.PutOk<AmlSettings>("/api/admin/aml/rules", rules);
        }
    }
}

internal static class AdminHttpExtensions
{
    public static async Task<string?> PutProblemAsync(this HttpClient client, string url, object body)
    {
        using var response = await client.PutAsJsonAsync(url, body, Http.Json);
        return (await Http.ReadProblem(response)).Code;
    }
}
