namespace Crypton.Core.Domain;

public enum AccountOwnerType
{
    User,
    System,
}

public enum AccountKind
{
    /// <summary>Spendable user balance.</summary>
    Available,

    /// <summary>User funds held for a pending withdrawal.</summary>
    WithdrawalHold,

    /// <summary>User funds reserved for an active P2P sell ad.</summary>
    P2PReserve,

    /// <summary>Platform-owned account (see <see cref="SystemAccounts"/>).</summary>
    System,
}

/// <summary>
/// Platform accounts. The ledger is zero-sum per asset: every journal's postings sum to zero,
/// so the sum of all balances of an asset is always exactly zero.
/// </summary>
public static class SystemAccounts
{
    /// <summary>Funds held on-chain / at the payment provider. Negative balance = amount held.</summary>
    public const string Custody = "custody";

    /// <summary>House inventory used as the counterparty for instant buy / sell / swap.</summary>
    public const string Treasury = "treasury";

    /// <summary>Fee revenue.</summary>
    public const string Fees = "fees";

    /// <summary>Crypto locked in open P2P orders.</summary>
    public const string P2PEscrow = "p2p_escrow";

    /// <summary>Counterparty for manual admin corrections.</summary>
    public const string Adjustments = "adjustments";

    /// <summary>On-chain miner / gas fees paid by the hot wallet (expense, negative).</summary>
    public const string NetworkFees = "network_fees";

    /// <summary>Payment provider processing fees (expense, negative).</summary>
    public const string PaymentCosts = "payment_costs";

    /// <summary>Accounts whose balance may go negative.</summary>
    public static readonly string[] NegativeAllowed = [Custody, Adjustments, NetworkFees, PaymentCosts];

    public static readonly string[] All = [Custody, Treasury, Fees, P2PEscrow, Adjustments, NetworkFees, PaymentCosts];
}

public static class JournalTypes
{
    public const string CryptoDeposit = "crypto_deposit";
    public const string CryptoWithdrawalHold = "crypto_withdrawal_hold";
    public const string CryptoWithdrawalSettle = "crypto_withdrawal_settle";
    public const string CryptoWithdrawalRelease = "crypto_withdrawal_release";
    public const string NetworkFee = "network_fee";
    public const string FiatDeposit = "fiat_deposit";
    public const string FiatWithdrawalHold = "fiat_withdrawal_hold";
    public const string FiatWithdrawalSettle = "fiat_withdrawal_settle";
    public const string FiatWithdrawalRelease = "fiat_withdrawal_release";
    public const string FiatWithdrawalReversal = "fiat_withdrawal_reversal";
    public const string TradeBuy = "trade_buy";
    public const string TradeSell = "trade_sell";
    public const string TradeSwap = "trade_swap";
    public const string P2PAdReserve = "p2p_ad_reserve";
    public const string P2PAdRelease = "p2p_ad_release";
    public const string P2PEscrowLock = "p2p_escrow_lock";
    public const string P2PEscrowRelease = "p2p_escrow_release";
    public const string P2PEscrowRefund = "p2p_escrow_refund";
    public const string AdminAdjustment = "admin_adjustment";
    public const string TreasuryFunding = "treasury_funding";
    public const string TreasuryDefunding = "treasury_defunding";
}

public class LedgerAccount
{
    public Guid Id { get; set; }

    /// <summary>u:{userId}:{asset}:{kind} or s:{code}:{asset}. Unique.</summary>
    public string AccountKey { get; set; } = "";

    public AccountOwnerType OwnerType { get; set; }

    public Guid? UserId { get; set; }

    public string? SystemCode { get; set; }

    public string Asset { get; set; } = "";

    public AccountKind Kind { get; set; }

    public decimal Balance { get; set; }

    public bool AllowNegative { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public static string UserKey(Guid userId, string asset, AccountKind kind) => $"u:{userId:N}:{asset}:{kind}";

    public static string SystemKey(string code, string asset) => $"s:{code}:{asset}";
}

public class JournalEntry
{
    public Guid Id { get; set; }

    public string Type { get; set; } = "";

    /// <summary>Optional unique key that makes a posting idempotent (e.g. deposit tx + output).</summary>
    public string? IdempotencyKey { get; set; }

    public string? ReferenceType { get; set; }

    public Guid? ReferenceId { get; set; }

    /// <summary>The main user this entry concerns, for history queries.</summary>
    public Guid? UserId { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<Posting> Postings { get; set; } = [];
}

public class Posting
{
    public long Id { get; set; }

    public Guid JournalEntryId { get; set; }

    public Guid AccountId { get; set; }

    public Guid? UserId { get; set; }

    public AccountKind Kind { get; set; }

    public string? SystemCode { get; set; }

    public string Asset { get; set; } = "";

    /// <summary>Signed amount. Positive increases the account balance.</summary>
    public decimal Amount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
