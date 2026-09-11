using System.ComponentModel.DataAnnotations;
using Crypton.Core.Domain;
using Crypton.Core.P2P;

namespace Crypton.Api.Contracts;

public sealed record TraderDto(Guid UserId, string DisplayName, bool Verified, DateTimeOffset MemberSince, int CompletedOrders30d, decimal CompletionRate30d, double? AverageReleaseMinutes, int PositiveFeedback, int NegativeFeedback);

public sealed record MarketAdDto(
    Guid Id,
    P2PAdSide Side,
    string Asset,
    string FiatCurrency,
    P2PPriceType PriceType,
    int FloatingMarginBps,
    decimal Price,
    decimal RemainingQuantity,
    decimal MinOrderFiat,
    decimal MaxOrderFiat,
    int PaymentWindowMinutes,
    IReadOnlyList<string> PaymentBanks,
    string? Terms,
    TraderDto Maker);

public sealed record MyAdDto(
    Guid Id,
    P2PAdSide Side,
    string Asset,
    P2PPriceType PriceType,
    decimal? FixedPrice,
    int FloatingMarginBps,
    decimal EffectivePrice,
    decimal TotalQuantity,
    decimal RemainingQuantity,
    decimal ReservedAmount,
    decimal MinOrderFiat,
    decimal MaxOrderFiat,
    int PaymentWindowMinutes,
    IReadOnlyList<Guid> PaymentMethodIds,
    string? Terms,
    P2PAdStatus Status,
    bool SuspendedByAdmin,
    DateTimeOffset CreatedAt);

public sealed record CreateAdRequestDto(
    [Required] P2PAdSide Side,
    [Required, MaxLength(10)] string Asset,
    [Required] P2PPriceType PriceType,
    decimal? FixedPrice,
    int FloatingMarginBps,
    [Required] decimal TotalQuantity,
    [Required] decimal MinOrderFiat,
    [Required] decimal MaxOrderFiat,
    [Required] int PaymentWindowMinutes,
    List<Guid>? PaymentMethodIds,
    [MaxLength(1000)] string? Terms);

public sealed record UpdateAdRequestDto(
    [Required] P2PPriceType PriceType,
    decimal? FixedPrice,
    int FloatingMarginBps,
    [Required] decimal MinOrderFiat,
    [Required] decimal MaxOrderFiat,
    [Required] int PaymentWindowMinutes,
    List<Guid>? PaymentMethodIds,
    [MaxLength(1000)] string? Terms,
    [Required] P2PAdStatus Status);

public sealed record CreateP2POrderRequest([Required] Guid AdId, decimal? FiatAmount, decimal? Quantity, Guid? PaymentMethodId);

public sealed record PaymentDetailDto(string BankName, string AccountNumber, string AccountName);

public sealed record DisputeEvidenceDto(Guid Id, string Party, string? Text, string? FileName, bool HasFile, DateTimeOffset CreatedAt);

public sealed record DisputeDto(Guid Id, P2PDisputeStatus Status, string OpenedBy, string Reason, string? ResolutionNote, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, IReadOnlyList<DisputeEvidenceDto> Evidence);

public sealed record P2POrderDto(
    Guid Id,
    string OrderNumber,
    Guid AdId,
    string MyRole,
    P2PAdSide AdSide,
    string Asset,
    string FiatCurrency,
    decimal Quantity,
    decimal Price,
    decimal FiatAmount,
    decimal Fee,
    decimal ReceiveQuantity,
    P2POrderStatus Status,
    DateTimeOffset PaymentDeadline,
    IReadOnlyList<PaymentDetailDto> PaymentDetails,
    string? BuyerPaymentReference,
    string? CancelReason,
    TraderDto Counterparty,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    DateTimeOffset? DisputeAvailableAt,
    DisputeDto? Dispute,
    bool FeedbackGiven);

public sealed record MarkPaidRequest([MaxLength(120)] string? PaymentReference);

public sealed record ReleaseRequest([MaxLength(10)] string? TwoFactorCode);

public sealed record ReasonRequest([MaxLength(1000)] string? Reason);

public sealed record FeedbackRequest(bool Positive, [MaxLength(500)] string? Comment);

public sealed record FeedbackDto(bool Positive, string? Comment, string FromDisplayName, DateTimeOffset CreatedAt);

public sealed record TraderProfileDto(TraderDto Trader, IReadOnlyList<FeedbackDto> RecentFeedback);

public static class P2PMappings
{
    public static TraderDto ToDto(this TraderStats t) => new(t.UserId, t.DisplayName, t.Verified, t.MemberSince, t.CompletedOrders30d, t.CompletionRate30d, t.AverageReleaseMinutes, t.PositiveFeedback, t.NegativeFeedback);

    public static MarketAdDto ToDto(this MarketAd m) => new(m.Ad.Id, m.Ad.Side, m.Ad.Asset, m.Ad.FiatCurrency, m.Ad.PriceType, m.Ad.FloatingMarginBps, m.EffectivePrice,
        m.Ad.RemainingQuantity, m.Ad.MinOrderFiat, m.MaxFiatAvailable, m.Ad.PaymentWindowMinutes, m.PaymentBanks, m.Ad.Terms, m.Maker.ToDto());

    public static MyAdDto ToMyDto(this P2PAd a, decimal effectivePrice) => new(a.Id, a.Side, a.Asset, a.PriceType, a.FixedPrice, a.FloatingMarginBps, effectivePrice, a.TotalQuantity,
        a.RemainingQuantity, a.ReservedAmount, a.MinOrderFiat, a.MaxOrderFiat, a.PaymentWindowMinutes, a.PaymentMethodIds, a.Terms, a.Status, a.SuspendedByAdmin, a.CreatedAt);
}
