using System.Net;
using System.Text;
using Crypton.Api.Contracts;
using Crypton.Core.Domain;
using Crypton.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Tests.Integration;

[Collection(ApiCollection.Name)]
public class FiatTests(CryptonFactory factory)
{
    [Fact]
    public async Task Unverified_users_cannot_use_naira_rails()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 0);
        var problem = await user.Client.PostProblem("/api/fiat/deposits", new { amount = 10_000m });
        Assert.Equal("kyc_required", problem.Code);
    }

    [Fact]
    public async Task Deposit_via_checkout_is_credited_exactly_once()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1);
        var paymentCostsBefore = await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.PaymentCosts, AssetCodes.NGN);

        var deposit = await user.Client.PostOk<FiatDepositDto>("/api/fiat/deposits", new { amount = 50_000m });
        Assert.Equal(FiatDepositStatus.Initiated, deposit.Status);
        Assert.Contains("simulated-checkout", deposit.AuthorizationUrl);

        var checkout = await user.Client.GetOk<SimulatedCheckoutDto>($"/api/fiat/simulated-checkout/{deposit.Reference}");
        Assert.Equal("pending", checkout.Status);
        Assert.Equal(50_000m, checkout.Amount);

        var completed = await user.Client.PostOk<FiatDepositDto>($"/api/fiat/simulated-checkout/{deposit.Reference}", new { success = true });
        Assert.Equal(FiatDepositStatus.Succeeded, completed.Status);
        Assert.Equal(50_000m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));

        var verifiedAgain = await user.Client.PostOk<FiatDepositDto>($"/api/fiat/deposits/{deposit.Reference}/verify");
        Assert.Equal(FiatDepositStatus.Succeeded, verifiedAgain.Status);
        Assert.Equal(50_000m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));

        // Simulated provider charges 1.5%: booked as a platform cost, not taken from the user.
        Assert.Equal(paymentCostsBefore - 750m, await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.PaymentCosts, AssetCodes.NGN));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Declined_checkout_is_not_credited()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1);
        var deposit = await user.Client.PostOk<FiatDepositDto>("/api/fiat/deposits", new { amount = 20_000m });
        var failed = await user.Client.PostOk<FiatDepositDto>($"/api/fiat/simulated-checkout/{deposit.Reference}", new { success = false });
        Assert.Equal(FiatDepositStatus.Failed, failed.Status);
        Assert.Equal(0m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));
    }

    [Fact]
    public async Task Bank_account_resolution()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1);
        var banks = await user.Client.GetOk<List<BankDto>>("/api/fiat/banks");
        Assert.Contains(banks, b => b.Code == "058");

        var resolved = await user.Client.PostOk<ResolvedAccountDto>("/api/fiat/bank-accounts/resolve", new { bankCode = "058", accountNumber = "0123456789" });
        Assert.Equal("DEMO ACCOUNT 6789", resolved.AccountName);

        var unknown = await user.Client.PostProblem("/api/fiat/bank-accounts", new { bankCode = "058", accountNumber = "0003456789" });
        Assert.Equal("account_not_resolved", unknown.Code);

        var invalid = await user.Client.PostProblem("/api/fiat/bank-accounts", new { bankCode = "058", accountNumber = "12345" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.Status);
    }

    [Fact]
    public async Task Withdrawal_to_bank_settles_after_provider_confirms()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 100_000m });
        var account = await user.Client.PostOk<BankAccountDto>("/api/fiat/bank-accounts", new { bankCode = "057", accountNumber = "2123456789" });
        var feesBefore = await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Fees, AssetCodes.NGN);

        var withdrawal = await user.Client.PostOk<FiatWithdrawalDto>("/api/fiat/withdrawals", new
        {
            bankAccountId = account.Id,
            amount = 20_000m,
            twoFactorCode = TestAccounts.NextCode(factory, user),
        });
        Assert.Equal(FiatWithdrawalStatus.Approved, withdrawal.Status);
        Assert.Equal(79_950m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.NGN));

        await user.Client.PostNoContent("/api/dev/jobs/fiat-payouts");
        var processing = await factory.WithDbAsync(db => db.FiatWithdrawals.AsNoTracking().FirstAsync(w => w.Id == withdrawal.Id));
        Assert.Equal(FiatWithdrawalStatus.Processing, processing.Status);

        factory.Clock.Advance(TimeSpan.FromSeconds(5));
        await user.Client.PostNoContent("/api/dev/jobs/fiat-reconcile");
        var done = await user.Client.GetOk<PageDto<FiatWithdrawalDto>>("/api/fiat/withdrawals");
        Assert.Equal(FiatWithdrawalStatus.Succeeded, done.Items.Single(w => w.Id == withdrawal.Id).Status);
        Assert.Equal(0m, await TestAccounts.BalanceAsync(factory, user.Id, AssetCodes.NGN, AccountKind.WithdrawalHold));
        Assert.Equal(feesBefore + 50m, await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Fees, AssetCodes.NGN));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Webhook_rejects_bad_signatures()
    {
        var client = factory.CreateApiClient();
        var content = new StringContent("{\"event\":\"charge.success\",\"data\":{\"reference\":\"x\"}}", Encoding.UTF8, "application/json");
        content.Headers.Add("x-paystack-signature", "deadbeef");
        using var response = await client.PostAsync("/api/webhooks/paystack", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
