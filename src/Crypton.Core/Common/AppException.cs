namespace Crypton.Core.Common;

/// <summary>
/// An expected, user-facing failure. The API turns it into an RFC 7807 problem response
/// with a stable machine-readable <see cref="Code"/>.
/// </summary>
public class AppException : Exception
{
    public AppException(string code, string message, int statusCode = 400, IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Details = details;
    }

    public string Code { get; }

    public int StatusCode { get; }

    public IReadOnlyDictionary<string, object?>? Details { get; }

    public static AppException NotFound(string what) => new(ErrorCodes.NotFound, $"{what} was not found.", 404);

    public static AppException Forbidden(string message = "You are not allowed to do this.", string code = ErrorCodes.Forbidden) =>
        new(code, message, 403);

    public static AppException Conflict(string code, string message) => new(code, message, 409);

    public static AppException Validation(string message, string code = ErrorCodes.Validation) => new(code, message, 400);
}

public static class ErrorCodes
{
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string Validation = "validation_error";
    public const string InsufficientFunds = "insufficient_funds";
    public const string InsufficientLiquidity = "insufficient_liquidity";
    public const string QuoteExpired = "quote_expired";
    public const string QuoteUsed = "quote_already_used";
    public const string PriceUnavailable = "price_unavailable";
    public const string AssetDisabled = "asset_disabled";
    public const string AmountTooSmall = "amount_too_small";
    public const string AmountTooLarge = "amount_too_large";
    public const string InvalidAmount = "invalid_amount";
    public const string InvalidAddress = "invalid_address";
    public const string AddressBlocked = "address_blocked";
    public const string LimitExceeded = "limit_exceeded";
    public const string KycRequired = "kyc_required";
    public const string TwoFactorRequired = "two_factor_required";
    public const string InvalidTwoFactorCode = "invalid_two_factor_code";
    public const string AccountFrozen = "account_frozen";
    public const string WithdrawalsLocked = "withdrawals_locked";
    public const string EmailNotConfirmed = "email_not_confirmed";
    public const string InvalidCredentials = "invalid_credentials";
    public const string LockedOut = "locked_out";
    public const string InvalidToken = "invalid_token";
    public const string DuplicateRequest = "duplicate_request";
    public const string InvalidState = "invalid_state";
    public const string ProviderError = "provider_error";
    public const string NotConfigured = "not_configured";
    public const string RateLimited = "rate_limited";
    public const string AmlBlocked = "blocked_by_compliance";
}
