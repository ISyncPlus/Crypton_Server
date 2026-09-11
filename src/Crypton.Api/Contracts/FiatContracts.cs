using System.ComponentModel.DataAnnotations;
using Crypton.Core.Domain;

namespace Crypton.Api.Contracts;

public sealed record FiatConfigDto(string Provider, bool Simulated, int DepositFeeBps, decimal DepositFeeCapNgn, decimal MinDepositNgn, decimal MaxDepositNgn, decimal WithdrawalFeeNgn, decimal MinWithdrawalNgn, decimal MaxWithdrawalNgn);

public sealed record BankDto(string Code, string Name);

public sealed record ResolveAccountRequest([Required, MaxLength(20)] string BankCode, [Required, RegularExpression("^[0-9]{10}$")] string AccountNumber);

public sealed record ResolvedAccountDto(string AccountNumber, string AccountName, string BankCode);

public sealed record BankAccountDto(Guid Id, string BankCode, string BankName, string AccountNumber, string AccountName, DateTimeOffset CreatedAt);

public sealed record CreateFiatDepositRequest([Required] decimal Amount);

public sealed record FiatDepositDto(Guid Id, string Reference, decimal Amount, decimal Fee, FiatDepositStatus Status, string? AuthorizationUrl, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record CreateFiatWithdrawalRequest([Required] Guid BankAccountId, [Required] decimal Amount, [MaxLength(10)] string? TwoFactorCode, [MaxLength(64)] string? IdempotencyKey);

public sealed record FiatWithdrawalDto(Guid Id, string Reference, decimal Amount, decimal Fee, FiatWithdrawalStatus Status, string BankName, string AccountNumber, string AccountName, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record SimulatedCheckoutDto(string Reference, decimal Amount, string Status, string Email);

public sealed record CompleteSimulatedCheckoutRequest(bool Success);

public static class FiatMappings
{
    public static BankAccountDto ToDto(this BankAccount b) => new(b.Id, b.BankCode, b.BankName, b.AccountNumber, b.AccountName, b.CreatedAt);

    public static FiatDepositDto ToDto(this FiatDeposit d) => new(d.Id, d.Reference, d.Amount, d.Fee, d.Status, d.Status == FiatDepositStatus.Initiated ? d.AuthorizationUrl : null, d.CreatedAt, d.CompletedAt);

    public static FiatWithdrawalDto ToDto(this FiatWithdrawal w) => new(w.Id, w.Reference, w.Amount, w.Fee, w.Status, w.BankName, w.AccountNumber, w.AccountName,
        w.Status is FiatWithdrawalStatus.Failed or FiatWithdrawalStatus.Rejected or FiatWithdrawalStatus.Reversed ? w.FailureReason ?? w.ReviewNote : null, w.CreatedAt, w.CompletedAt);
}
