namespace Crypton.Core.Domain;

public class DepositAddress
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Network { get; set; } = "";

    public string Address { get; set; } = "";

    public int DerivationIndex { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>For polling-based networks: when this address was last checked for new transactions.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Set when the user opens the deposit screen so the watcher checks the address sooner.</summary>
    public DateTimeOffset? WatchUntil { get; set; }
}

public enum CryptoDepositStatus
{
    Pending,
    Credited,
    Rejected,
}

public class CryptoDeposit
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Asset { get; set; } = "";

    public string Network { get; set; } = "";

    public string TxHash { get; set; } = "";

    /// <summary>vout for Bitcoin, log index for ERC-20 transfers, 0 for native ETH.</summary>
    public int OutputIndex { get; set; }

    public string Address { get; set; } = "";

    public decimal Amount { get; set; }

    public long? BlockNumber { get; set; }

    public int Confirmations { get; set; }

    public int RequiredConfirmations { get; set; }

    public CryptoDepositStatus Status { get; set; }

    public string? RejectionReason { get; set; }

    public decimal NgnValue { get; set; }

    public Guid? JournalEntryId { get; set; }

    public DateTimeOffset DetectedAt { get; set; }

    public DateTimeOffset? CreditedAt { get; set; }
}

public enum CryptoWithdrawalStatus
{
    /// <summary>Held, waiting for a compliance decision.</summary>
    PendingReview,

    /// <summary>Held and approved; waiting for the withdrawal processor.</summary>
    Approved,

    /// <summary>Signed and being broadcast. Never auto-refunded from this state.</summary>
    Broadcasting,

    /// <summary>Accepted by the network; waiting for confirmations.</summary>
    Broadcast,

    Confirmed,

    Rejected,

    Cancelled,

    /// <summary>Failed before anything was signed; funds were returned.</summary>
    Failed,

    /// <summary>Broadcast outcome is unknown after retries; needs an operator.</summary>
    NeedsAttention,
}

public class CryptoWithdrawal
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Asset { get; set; } = "";

    public string Network { get; set; } = "";

    public string ToAddress { get; set; } = "";

    /// <summary>Amount the recipient receives.</summary>
    public decimal Amount { get; set; }

    /// <summary>Platform withdrawal fee (asset).</summary>
    public decimal Fee { get; set; }

    public decimal NgnValue { get; set; }

    public CryptoWithdrawalStatus Status { get; set; }

    public string? IdempotencyKey { get; set; }

    public string? RiskSummary { get; set; }

    public string? TxHash { get; set; }

    /// <summary>Signed raw transaction, kept so a broadcast can be retried safely.</summary>
    public string? RawTransaction { get; set; }

    public int? Nonce { get; set; }

    public decimal? NetworkFee { get; set; }

    public string? NetworkFeeAsset { get; set; }

    public bool NetworkFeeBooked { get; set; }

    public int Confirmations { get; set; }

    public int BroadcastAttempts { get; set; }

    public string? FailureReason { get; set; }

    public Guid? ReviewedBy { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public string? ReviewNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? BroadcastAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }
}

public class ChainCursor
{
    public string Network { get; set; } = "";

    public long LastProcessedBlock { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum SimTxDirection
{
    Inbound,
    Outbound,
}

/// <summary>Transactions on the built-in simulated blockchain (development / demo mode only).</summary>
public class SimulatedChainTransaction
{
    public Guid Id { get; set; }

    public string Network { get; set; } = "";

    public string Asset { get; set; } = "";

    public string TxHash { get; set; } = "";

    public int OutputIndex { get; set; }

    public string ToAddress { get; set; } = "";

    public decimal Amount { get; set; }

    public SimTxDirection Direction { get; set; }

    public int Confirmations { get; set; }

    public long BlockNumber { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
