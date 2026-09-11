namespace Crypton.Core.Domain;

public class BankAccount
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string BankCode { get; set; } = "";

    public string BankName { get; set; } = "";

    public string AccountNumber { get; set; } = "";

    public string AccountName { get; set; } = "";

    /// <summary>Paystack transfer recipient code, created lazily on first payout.</summary>
    public string? RecipientCode { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum FiatDepositStatus
{
    Initiated,
    Succeeded,
    Failed,
    Abandoned,
}

public class FiatDeposit
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Provider { get; set; } = "";

    public string Reference { get; set; } = "";

    public string Currency { get; set; } = AssetCodes.NGN;

    /// <summary>Amount the user asked to pay.</summary>
    public decimal Amount { get; set; }

    /// <summary>Fee charged to the user (deducted from the credit).</summary>
    public decimal Fee { get; set; }

    /// <summary>Fee the provider charged the platform.</summary>
    public decimal ProviderFee { get; set; }

    public FiatDepositStatus Status { get; set; }

    public string? AuthorizationUrl { get; set; }

    public string? ProviderTransactionId { get; set; }

    public string? GatewayResponse { get; set; }

    public Guid? JournalEntryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public enum FiatWithdrawalStatus
{
    PendingReview,
    Approved,

    /// <summary>Submitted to the provider (transfer queued / processing).</summary>
    Processing,

    Succeeded,
    Failed,
    Reversed,
    Rejected,
    Cancelled,
    NeedsAttention,
}

public class FiatWithdrawal
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid BankAccountId { get; set; }

    public string Provider { get; set; } = "";

    public string Currency { get; set; } = AssetCodes.NGN;

    /// <summary>Amount sent to the bank account.</summary>
    public decimal Amount { get; set; }

    public decimal Fee { get; set; }

    /// <summary>Transfer reference (lowercase a-z, 0-9, '-' and '_', 16-50 chars).</summary>
    public string Reference { get; set; } = "";

    public string? IdempotencyKey { get; set; }

    public string? TransferCode { get; set; }

    public FiatWithdrawalStatus Status { get; set; }

    public string? RiskSummary { get; set; }

    public string? FailureReason { get; set; }

    public int SubmitAttempts { get; set; }

    public Guid? ReviewedBy { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public string? ReviewNote { get; set; }

    /// <summary>Snapshot of the destination at request time.</summary>
    public string BankName { get; set; } = "";

    public string AccountNumber { get; set; } = "";

    public string AccountName { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
