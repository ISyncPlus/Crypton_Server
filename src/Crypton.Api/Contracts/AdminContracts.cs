using System.ComponentModel.DataAnnotations;
using Crypton.Core.Admin;
using Crypton.Core.Domain;

namespace Crypton.Api.Contracts;

public sealed record AdminNoteRequest([MaxLength(1000)] string? Note);

public sealed record AdminReasonRequest([Required, MaxLength(1000)] string Reason);

public sealed record AdminMarkSentRequest([Required, MaxLength(128)] string TxHash, [Required, MaxLength(1000)] string Note);

public sealed record AdminRolesRequest(List<string> Roles);

public sealed record AdminAdjustBalanceRequest([Required, MaxLength(10)] string Asset, [Required] decimal Amount, [Required, MaxLength(500)] string Reason);

public sealed record AdminTreasuryRequest([Required, MaxLength(10)] string Asset, [Required] decimal Amount, [Required, MaxLength(100)] string Reference, [Required, MaxLength(500)] string Note);

public sealed record AdminResolveAlertRequest([Required] AmlAlertStatus Status, [MaxLength(1000)] string? Note);

public sealed record AdminBlockAddressRequest([Required, MaxLength(20)] string Network, [Required, MaxLength(128)] string Address, [Required, MaxLength(300)] string Reason);

public sealed record AdminImportBlockListRequest([Required, MaxLength(20)] string Network, [Required] string Addresses, [Required, MaxLength(300)] string Reason, [MaxLength(40)] string? Source);

public sealed record AdminResolveDisputeRequest([Required] bool ReleaseToBuyer, [Required, MaxLength(1000)] string Note);

public sealed record AdminAssetUpdateRequest(
    decimal MinDeposit,
    decimal MinWithdrawal,
    decimal WithdrawalFee,
    int RequiredConfirmations,
    bool DepositsEnabled,
    bool WithdrawalsEnabled,
    bool TradingEnabled);

public sealed record AdminUserDetailDto(
    UserDto User,
    string? FrozenReason,
    bool EmailConfirmed,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? LockoutEnd,
    IReadOnlyList<BalanceDto> Balances,
    IReadOnlyList<KycSubmissionDto> Kyc,
    int OpenAlerts,
    int ActiveSessions,
    IReadOnlyList<BankAccountDto> BankAccounts);

public sealed record AdminCryptoWithdrawalDto(
    Guid Id,
    Guid UserId,
    string UserEmail,
    string Asset,
    string Network,
    string ToAddress,
    decimal Amount,
    decimal Fee,
    decimal NgnValue,
    CryptoWithdrawalStatus Status,
    string? RiskSummary,
    string? TxHash,
    int Confirmations,
    decimal? NetworkFee,
    int BroadcastAttempts,
    string? FailureReason,
    string? ReviewNote,
    bool Settled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? BroadcastAt,
    DateTimeOffset? ConfirmedAt);

public sealed record AdminFiatWithdrawalDto(
    Guid Id,
    Guid UserId,
    string UserEmail,
    decimal Amount,
    decimal Fee,
    FiatWithdrawalStatus Status,
    string Reference,
    string? TransferCode,
    string BankName,
    string AccountNumber,
    string AccountName,
    string? RiskSummary,
    string? FailureReason,
    string? ReviewNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record AdminKycSubmissionDto(
    Guid Id,
    Guid UserId,
    string UserEmail,
    int TargetTier,
    KycSubmissionStatus Status,
    string Provider,
    string FirstName,
    string LastName,
    DateOnly? DateOfBirth,
    string? PhoneNumber,
    string? Address,
    KycIdType? IdType,
    string? IdNumberMasked,
    string? DocumentKind,
    string? ProviderResult,
    string? RejectionReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt,
    IReadOnlyList<AdminKycDocumentDto> Documents);

public sealed record AdminKycDocumentDto(Guid Id, KycDocumentType Type, string ContentType, long SizeBytes);

public sealed record AdminAlertDto(Guid Id, Guid UserId, string UserEmail, string RuleCode, AmlAction Action, int Severity, AmlSubjectType SubjectType, Guid? SubjectId, string Summary, string? Details, AmlAlertStatus Status, string? ResolutionNote, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt);

public sealed record AdminP2POrderDto(Guid Id, string OrderNumber, P2POrderStatus Status, P2PAdSide AdSide, string Asset, decimal Quantity, decimal Price, decimal FiatAmount, decimal Fee, Guid BuyerId, string BuyerEmail, Guid SellerId, string SellerEmail, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, DateTimeOffset? CompletedAt);

public sealed record AdminDisputeDto(Guid Id, P2PDisputeStatus Status, string Reason, string OpenedByRole, string? ResolutionNote, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, AdminP2POrderDto Order, IReadOnlyList<PaymentDetailDto> PaymentDetails, string? BuyerPaymentReference, IReadOnlyList<DisputeEvidenceDto> Evidence);

public sealed record AdminAdDto(Guid Id, Guid UserId, string UserEmail, P2PAdSide Side, string Asset, P2PPriceType PriceType, decimal? FixedPrice, int FloatingMarginBps, decimal TotalQuantity, decimal RemainingQuantity, P2PAdStatus Status, bool SuspendedByAdmin, DateTimeOffset CreatedAt);

public sealed record AdminAuditDto(long Id, Guid? UserId, Guid? ActorUserId, string Action, string? EntityType, string? EntityId, string? IpAddress, string? Data, DateTimeOffset CreatedAt);

public sealed record AdminDashboardDto(AnalyticsOverview Last24h, AnalyticsOverview Last7d, QueueCounts Queues, IReadOnlyList<JobStatusDto> Jobs, PriceFeedStatusDto PriceFeed, string BlockchainMode, string PaymentsProvider);

public sealed record JobStatusDto(string Name, DateTimeOffset? LastSucceededAt, DateTimeOffset? LastFailedAt, string? LastError, int ConsecutiveFailures);

public sealed record PriceFeedStatusDto(string Provider, DateTimeOffset? LastRefreshAt, string? LastError);

public sealed record AdminSettingsDto(Core.Settings.TradingSettings Trading, Core.Settings.WithdrawalSettings Withdrawals, Core.Settings.FiatSettings Fiat, Core.Settings.P2PSettings P2P, Core.Settings.KycLimitSettings KycLimits);
