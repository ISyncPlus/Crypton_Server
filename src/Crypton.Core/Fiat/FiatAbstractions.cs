namespace Crypton.Core.Fiat;

public sealed record BankInfo(string Code, string Name, string? Slug);

public sealed record ResolvedBankAccount(string AccountNumber, string AccountName, string BankCode);

public sealed record PaymentInitRequest(string Email, long AmountMinor, string Currency, string Reference, string CallbackUrl, IReadOnlyDictionary<string, string> Metadata);

public sealed record PaymentInitResult(string AuthorizationUrl, string Reference, string? AccessCode);

public enum ProviderPaymentStatus
{
    Pending,
    Success,
    Failed,
    Abandoned,
}

public sealed record PaymentVerification(
    string Reference,
    ProviderPaymentStatus Status,
    long AmountMinor,
    string Currency,
    long FeesMinor,
    string? ProviderTransactionId,
    string? GatewayResponse,
    DateTimeOffset? PaidAt);

public enum ProviderTransferStatus
{
    Pending,
    Success,
    Failed,
    Reversed,

    /// <summary>The provider needs an OTP to release the transfer (disable OTP for automated payouts).</summary>
    OtpRequired,

    /// <summary>The provider has no transfer with this reference.</summary>
    NotFound,
}

public sealed record TransferRequest(long AmountMinor, string Currency, string RecipientCode, string Reference, string Reason);

public sealed record TransferResult(ProviderTransferStatus Status, string? TransferCode, string? Message);

/// <summary>A naira payment provider (Paystack or the built-in simulator).</summary>
public interface IFiatGateway
{
    string Name { get; }

    bool IsSimulated { get; }

    bool IsConfigured { get; }

    Task<IReadOnlyList<BankInfo>> ListBanksAsync(CancellationToken ct);

    Task<ResolvedBankAccount> ResolveAccountAsync(string accountNumber, string bankCode, CancellationToken ct);

    Task<PaymentInitResult> InitializePaymentAsync(PaymentInitRequest request, CancellationToken ct);

    Task<PaymentVerification> VerifyPaymentAsync(string reference, CancellationToken ct);

    Task<string> CreateTransferRecipientAsync(string accountName, string accountNumber, string bankCode, string currency, CancellationToken ct);

    Task<TransferResult> InitiateTransferAsync(TransferRequest request, CancellationToken ct);

    Task<TransferResult> VerifyTransferAsync(string reference, CancellationToken ct);

    bool VerifyWebhookSignature(ReadOnlySpan<byte> body, string? signature);
}

public sealed class FiatProviderException(string message, bool isDefinitive, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>True when the provider clearly refused the request (e.g. validation error), false for timeouts/5xx.</summary>
    public bool IsDefinitive { get; } = isDefinitive;
}

public static class MinorUnits
{
    public static long ToMinor(decimal amount) => (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal FromMinor(long minor) => minor / 100m;
}
