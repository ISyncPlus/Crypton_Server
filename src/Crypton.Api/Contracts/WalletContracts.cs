using System.ComponentModel.DataAnnotations;
using Crypton.Core.Domain;

namespace Crypton.Api.Contracts;

public sealed record AssetDto(
    string Code,
    string Name,
    bool IsFiat,
    int Precision,
    string? Network,
    int RequiredConfirmations,
    decimal MinDeposit,
    decimal MinWithdrawal,
    decimal WithdrawalFee,
    bool DepositsEnabled,
    bool WithdrawalsEnabled,
    bool TradingEnabled);

public sealed record PriceDto(string Asset, decimal PriceNgn, decimal PriceUsd, decimal? Change24hPercent, DateTimeOffset UpdatedAt, string Source);

public sealed record PricePointDto(DateTimeOffset At, decimal PriceNgn);

public sealed record BalanceDto(string Asset, decimal Available, decimal Locked, decimal Total, decimal ValueNgn);

public sealed record WalletsResponse(IReadOnlyList<BalanceDto> Balances, decimal TotalValueNgn, bool PricesAvailable);

public sealed record TransactionDto(Guid JournalEntryId, string Type, string Asset, decimal Amount, string? Description, string? ReferenceType, Guid? ReferenceId, DateTimeOffset CreatedAt);

public sealed record DepositAddressDto(string Asset, string Network, string Address, int RequiredConfirmations, decimal MinDeposit, bool Simulated);

public sealed record CryptoDepositDto(Guid Id, string Asset, string Network, string TxHash, decimal Amount, int Confirmations, int RequiredConfirmations, CryptoDepositStatus Status, string? RejectionReason, DateTimeOffset DetectedAt, DateTimeOffset? CreditedAt);

public sealed record CryptoWithdrawalDto(Guid Id, string Asset, string Network, string ToAddress, decimal Amount, decimal Fee, CryptoWithdrawalStatus Status, string? TxHash, int Confirmations, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? BroadcastAt, DateTimeOffset? ConfirmedAt);

public sealed record CreateCryptoWithdrawalRequest(
    [Required, MaxLength(10)] string Asset,
    [Required, MaxLength(128)] string Address,
    [Required] decimal Amount,
    [MaxLength(10)] string? TwoFactorCode,
    [MaxLength(64)] string? IdempotencyKey);

public sealed record SimulateDepositRequest([Required, MaxLength(10)] string Asset, [Required] decimal Amount);

public static class WalletMappings
{
    public static AssetDto ToDto(this Asset a) => new(a.Code, a.Name, a.IsFiat, a.Precision, a.Network, a.RequiredConfirmations, a.MinDeposit, a.MinWithdrawal, a.WithdrawalFee, a.DepositsEnabled, a.WithdrawalsEnabled, a.TradingEnabled);

    public static CryptoDepositDto ToDto(this CryptoDeposit d) => new(d.Id, d.Asset, d.Network, d.TxHash, d.Amount, d.Confirmations, d.RequiredConfirmations, d.Status, d.RejectionReason, d.DetectedAt, d.CreditedAt);

    public static CryptoWithdrawalDto ToDto(this CryptoWithdrawal w) => new(w.Id, w.Asset, w.Network, w.ToAddress, w.Amount, w.Fee, w.Status, w.TxHash, w.Confirmations,
        w.Status is CryptoWithdrawalStatus.Failed or CryptoWithdrawalStatus.Rejected ? w.FailureReason ?? w.ReviewNote : null, w.CreatedAt, w.BroadcastAt, w.ConfirmedAt);
}
