using Crypton.Api.Contracts;
using Crypton.Core.Domain;
using Crypton.Integrations.Blockchain;
using Crypton.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace Crypton.Tests.Integration;

[Collection(ApiCollection.Name)]
public class WalletTests(CryptonFactory factory)
{
    private static string ExternalBitcoinAddress() => new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet).ToString();

    private static string ExternalEthereumAddress() => Crypton.Integrations.Blockchain.HdKeys.EthereumAddress(new Key().PubKey);

    private async Task RunJobAsync(TestUser user, string job) =>
        await user.Client.PostNoContent($"/api/dev/jobs/{job}");

    [Fact]
    public async Task Network_info_is_public_and_marks_simulation()
    {
        using var anonymous = factory.CreateClient();
        var networks = await anonymous.GetOk<List<ChainNetworkInfo>>("/api/market/networks");
        Assert.Equal(new[] { "bitcoin", "ethereum" }, networks.Select(n => n.Network).ToArray());
        Assert.All(networks, n => Assert.True(n.Simulated));
        Assert.All(networks, n => Assert.Null(n.TxUrlTemplate));
    }

    [Fact]
    public async Task Bitcoin_deposit_is_credited_once_after_confirmations()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var address = await user.Client.GetOk<DepositAddressDto>("/api/wallets/BTC/address");
        Assert.StartsWith("tb1q", address.Address);
        Assert.True(address.Simulated);

        var again = await user.Client.GetOk<DepositAddressDto>("/api/wallets/BTC/address");
        Assert.Equal(address.Address, again.Address);

        await user.Client.PostOk<CryptoDepositDto>("/api/dev/simulate/crypto-deposit", new { asset = "BTC", amount = 0.25m });
        await RunJobAsync(user, "deposits-bitcoin");

        var pending = await user.Client.GetOk<PageDto<CryptoDepositDto>>("/api/wallets/deposits");
        var deposit = Assert.Single(pending.Items);
        Assert.Equal(CryptoDepositStatus.Pending, deposit.Status);
        Assert.Equal(0m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.BTC));

        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunJobAsync(user, "deposits-bitcoin");
        await RunJobAsync(user, "deposits-bitcoin");

        var credited = await user.Client.GetOk<PageDto<CryptoDepositDto>>("/api/wallets/deposits");
        Assert.Equal(CryptoDepositStatus.Credited, Assert.Single(credited.Items).Status);
        Assert.Equal(0.25m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.BTC));

        var wallets = await user.Client.GetOk<WalletsResponse>("/api/wallets");
        Assert.Equal(0.25m, wallets.Balances.Single(b => b.Asset == "BTC").Available);
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Deposits_below_minimum_are_not_credited()
    {
        var user = await TestAccounts.CreateAsync(factory);
        await user.Client.GetOk<DepositAddressDto>("/api/wallets/ETH/address");
        await user.Client.PostOk<CryptoDepositDto>("/api/dev/simulate/crypto-deposit", new { asset = "ETH", amount = 0.001m });
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunJobAsync(user, "deposits-ethereum");
        await RunJobAsync(user, "deposits-ethereum");

        var deposits = await user.Client.GetOk<PageDto<CryptoDepositDto>>("/api/wallets/deposits");
        var deposit = Assert.Single(deposits.Items);
        Assert.Equal(CryptoDepositStatus.Rejected, deposit.Status);
        Assert.Equal(0m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.ETH));
    }

    [Fact]
    public async Task Usdt_deposit_shares_the_ethereum_address()
    {
        var user = await TestAccounts.CreateAsync(factory);
        var eth = await user.Client.GetOk<DepositAddressDto>("/api/wallets/ETH/address");
        var usdt = await user.Client.GetOk<DepositAddressDto>("/api/wallets/USDT/address");
        Assert.Equal(eth.Address, usdt.Address);

        await user.Client.PostOk<CryptoDepositDto>("/api/dev/simulate/crypto-deposit", new { asset = "USDT", amount = 250.5m });
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobAsync(user, "deposits-ethereum");
        await RunJobAsync(user, "deposits-ethereum");
        Assert.Equal(250.5m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.USDT));
    }

    [Fact]
    public async Task Withdrawal_requires_two_factor_and_verification()
    {
        var unverified = await TestAccounts.CreateAsync(factory, kycTier: 0, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.BTC] = 0.1m });
        var noKyc = await unverified.Client.PostProblem("/api/wallets/withdrawals", new { asset = "BTC", address = ExternalBitcoinAddress(), amount = 0.001m, twoFactorCode = TestAccounts.NextCode(factory, unverified) });
        Assert.Equal("kyc_required", noKyc.Code);

        var no2fa = await TestAccounts.CreateAsync(factory, kycTier: 1, balances: new Dictionary<string, decimal> { [AssetCodes.BTC] = 0.1m });
        var refused = await no2fa.Client.PostProblem("/api/wallets/withdrawals", new { asset = "BTC", address = ExternalBitcoinAddress(), amount = 0.001m });
        Assert.Equal("two_factor_required", refused.Code);

        var withTwoFactor = await TestAccounts.CreateAsync(factory, kycTier: 1, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.BTC] = 0.1m });
        var badCode = await withTwoFactor.Client.PostProblem("/api/wallets/withdrawals", new { asset = "BTC", address = ExternalBitcoinAddress(), amount = 0.001m, twoFactorCode = "123456" });
        Assert.Equal("invalid_two_factor_code", badCode.Code);

        var badAddress = await withTwoFactor.Client.PostProblem("/api/wallets/withdrawals", new { asset = "BTC", address = "bc1qcr8te4kr609gcawutmrza0j4xv80jy8z306fyu", amount = 0.001m, twoFactorCode = TestAccounts.NextCode(factory, withTwoFactor) });
        Assert.Equal("invalid_address", badAddress.Code);
        Assert.Equal(0.1m, await TestAccounts.AvailableAsync(factory, withTwoFactor.Id, AssetCodes.BTC));
    }

    [Fact]
    public async Task Bitcoin_withdrawal_is_sent_settled_and_confirmed()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.BTC] = 0.1m });
        var feesBefore = await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Fees, AssetCodes.BTC);
        var networkFeesBefore = await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.NetworkFees, AssetCodes.BTC);

        var withdrawal = await user.Client.PostOk<CryptoWithdrawalDto>("/api/wallets/withdrawals", new
        {
            asset = "BTC",
            address = ExternalBitcoinAddress(),
            amount = 0.005m,
            twoFactorCode = TestAccounts.NextCode(factory, user),
            idempotencyKey = "wd-1",
        });
        Assert.Equal(CryptoWithdrawalStatus.Approved, withdrawal.Status);
        Assert.Equal(0.1m - 0.005m - 0.00005m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.BTC));
        Assert.Equal(0.00505m, await TestAccounts.BalanceAsync(factory, user.Id, AssetCodes.BTC, AccountKind.WithdrawalHold));

        var duplicate = await user.Client.PostOk<CryptoWithdrawalDto>("/api/wallets/withdrawals", new
        {
            asset = "BTC",
            address = ExternalBitcoinAddress(),
            amount = 0.005m,
            twoFactorCode = TestAccounts.NextCode(factory, user),
            idempotencyKey = "wd-1",
        });
        Assert.Equal(withdrawal.Id, duplicate.Id);

        await RunJobAsync(user, "withdrawals-bitcoin");
        var sent = await factory.WithDbAsync(db => db.CryptoWithdrawals.AsNoTracking().FirstAsync(w => w.Id == withdrawal.Id));
        Assert.Equal(CryptoWithdrawalStatus.Broadcast, sent.Status);
        Assert.NotNull(sent.TxHash);
        Assert.Equal(0m, await TestAccounts.BalanceAsync(factory, user.Id, AssetCodes.BTC, AccountKind.WithdrawalHold));
        Assert.Equal(feesBefore + 0.00005m, await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.Fees, AssetCodes.BTC));
        Assert.Equal(networkFeesBefore - 0.00002m, await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.NetworkFees, AssetCodes.BTC));

        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        await RunJobAsync(user, "withdrawals-bitcoin");
        var confirmed = await factory.WithDbAsync(db => db.CryptoWithdrawals.AsNoTracking().FirstAsync(w => w.Id == withdrawal.Id));
        Assert.Equal(CryptoWithdrawalStatus.Confirmed, confirmed.Status);
        Assert.True(confirmed.NetworkFeeBooked);
        Assert.Equal(1, await factory.WithDbAsync(db => db.JournalEntries.CountAsync(j => j.Type == JournalTypes.NetworkFee && j.ReferenceId == withdrawal.Id)));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Usdt_withdrawal_books_gas_in_eth()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.USDT] = 500m });
        var ethNetworkBefore = await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.NetworkFees, AssetCodes.ETH);
        var withdrawal = await user.Client.PostOk<CryptoWithdrawalDto>("/api/wallets/withdrawals", new
        {
            asset = "USDT",
            address = ExternalEthereumAddress(),
            amount = 100m,
            twoFactorCode = TestAccounts.NextCode(factory, user),
        });
        await RunJobAsync(user, "withdrawals-ethereum");
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobAsync(user, "withdrawals-ethereum");

        var done = await factory.WithDbAsync(db => db.CryptoWithdrawals.AsNoTracking().FirstAsync(w => w.Id == withdrawal.Id));
        Assert.Equal(CryptoWithdrawalStatus.Confirmed, done.Status);
        Assert.Equal(397m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.USDT));
        Assert.Equal(ethNetworkBefore - 0.0004m, await TestAccounts.SystemBalanceAsync(factory, SystemAccounts.NetworkFees, AssetCodes.ETH));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Large_withdrawal_goes_to_review_and_rejection_returns_funds()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 2, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.BTC] = 1m });
        var withdrawal = await user.Client.PostOk<CryptoWithdrawalDto>("/api/wallets/withdrawals", new
        {
            asset = "BTC",
            address = ExternalBitcoinAddress(),
            amount = 0.05m,
            twoFactorCode = TestAccounts.NextCode(factory, user),
        });
        Assert.Equal(CryptoWithdrawalStatus.PendingReview, withdrawal.Status);

        // The processor must not touch it.
        await RunJobAsync(user, "withdrawals-bitcoin");
        Assert.Equal(CryptoWithdrawalStatus.PendingReview, (await factory.WithDbAsync(db => db.CryptoWithdrawals.AsNoTracking().FirstAsync(w => w.Id == withdrawal.Id))).Status);

        var admin = await TestAccounts.AdminAsync(factory);
        var queue = await admin.Client.GetOk<PageDto<AdminCryptoWithdrawalDto>>("/api/admin/withdrawals/crypto?status=PendingReview&pageSize=200");
        Assert.Contains(queue.Items, w => w.Id == withdrawal.Id);

        var rejected = await admin.Client.PostOk<AdminCryptoWithdrawalDto>($"/api/admin/withdrawals/crypto/{withdrawal.Id}/reject", new { reason = "Unable to verify destination" });
        Assert.Equal(CryptoWithdrawalStatus.Rejected, rejected.Status);
        Assert.Equal(1m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.BTC));
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Approved_review_is_then_sent()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 2, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.ETH] = 5m });
        var destination = ExternalEthereumAddress();
        var withdrawal = await user.Client.PostOk<CryptoWithdrawalDto>("/api/wallets/withdrawals", new
        {
            asset = "ETH",
            address = destination.ToLowerInvariant(),
            amount = 1m,
            twoFactorCode = TestAccounts.NextCode(factory, user),
        });
        Assert.Equal(CryptoWithdrawalStatus.PendingReview, withdrawal.Status);
        Assert.Equal(destination, withdrawal.ToAddress);

        var admin = await TestAccounts.AdminAsync(factory);
        await admin.Client.PostOk<AdminCryptoWithdrawalDto>($"/api/admin/withdrawals/crypto/{withdrawal.Id}/approve", new { note = "Verified by phone" });
        await RunJobAsync(user, "withdrawals-ethereum");
        Assert.Equal(CryptoWithdrawalStatus.Broadcast, (await factory.WithDbAsync(db => db.CryptoWithdrawals.AsNoTracking().FirstAsync(w => w.Id == withdrawal.Id))).Status);
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Blocked_addresses_are_refused_and_alerted()
    {
        var admin = await TestAccounts.AdminAsync(factory);
        var destination = ExternalBitcoinAddress();
        await admin.Client.PostOk<BlockedAddress>("/api/admin/aml/blocked-addresses", new { network = "bitcoin", address = destination, reason = "Known scam wallet" });

        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.BTC] = 0.1m });
        var refused = await user.Client.PostProblem("/api/wallets/withdrawals", new { asset = "BTC", address = destination, amount = 0.001m, twoFactorCode = TestAccounts.NextCode(factory, user) });
        Assert.Equal("blocked_by_compliance", refused.Code);
        Assert.Equal(0.1m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.BTC));

        var alerts = await admin.Client.GetOk<PageDto<AdminAlertDto>>($"/api/admin/aml/alerts?userId={user.Id}");
        Assert.Contains(alerts.Items, a => a.RuleCode == "blocked_address" && a.Action == AmlAction.Block);
    }

    [Fact]
    public async Task User_can_cancel_before_sending()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.BTC] = 0.1m });
        var withdrawal = await user.Client.PostOk<CryptoWithdrawalDto>("/api/wallets/withdrawals", new { asset = "BTC", address = ExternalBitcoinAddress(), amount = 0.002m, twoFactorCode = TestAccounts.NextCode(factory, user) });
        var cancelled = await user.Client.PostOk<CryptoWithdrawalDto>($"/api/wallets/withdrawals/{withdrawal.Id}/cancel");
        Assert.Equal(CryptoWithdrawalStatus.Cancelled, cancelled.Status);
        Assert.Equal(0.1m, await TestAccounts.AvailableAsync(factory, user.Id, AssetCodes.BTC));

        var again = await user.Client.PostProblem($"/api/wallets/withdrawals/{withdrawal.Id}/cancel");
        Assert.Equal("invalid_state", again.Code);
        await LedgerAssert.InvariantsHoldAsync(factory);
    }

    [Fact]
    public async Task Security_changes_pause_withdrawals()
    {
        var user = await TestAccounts.CreateAsync(factory, kycTier: 1, twoFactor: true, balances: new Dictionary<string, decimal> { [AssetCodes.BTC] = 0.1m });
        await user.Client.PostNoContent("/api/me/password", new { currentPassword = user.Password, newPassword = "Changed-Password-42", twoFactorCode = TestAccounts.NextCode(factory, user) });
        var refused = await user.Client.PostProblem("/api/wallets/withdrawals", new { asset = "BTC", address = ExternalBitcoinAddress(), amount = 0.002m, twoFactorCode = TestAccounts.NextCode(factory, user) });
        Assert.Equal("withdrawals_locked", refused.Code);
    }

    [Fact]
    public async Task Transaction_history_reflects_ledger_movements()
    {
        var user = await TestAccounts.CreateAsync(factory, balances: new Dictionary<string, decimal> { [AssetCodes.USDT] = 42m });
        var history = await user.Client.GetOk<PageDto<TransactionDto>>("/api/wallets/transactions?asset=USDT");
        var entry = Assert.Single(history.Items);
        Assert.Equal(42m, entry.Amount);
        Assert.Equal(JournalTypes.AdminAdjustment, entry.Type);
    }
}
