using Crypton.Core.Domain;

namespace Crypton.Core.P2P;

public sealed record CreateAdRequest(
    P2PAdSide Side,
    string Asset,
    P2PPriceType PriceType,
    decimal? FixedPrice,
    int FloatingMarginBps,
    decimal TotalQuantity,
    decimal MinOrderFiat,
    decimal MaxOrderFiat,
    int PaymentWindowMinutes,
    IReadOnlyList<Guid> PaymentMethodIds,
    string? Terms);

public sealed record UpdateAdRequest(
    P2PPriceType PriceType,
    decimal? FixedPrice,
    int FloatingMarginBps,
    decimal MinOrderFiat,
    decimal MaxOrderFiat,
    int PaymentWindowMinutes,
    IReadOnlyList<Guid> PaymentMethodIds,
    string? Terms,
    P2PAdStatus Status);

public sealed record CreateOrderRequest(Guid AdId, decimal? FiatAmount, decimal? Quantity, Guid? PaymentMethodId);

public sealed record MarketFilter(P2PAdSide AdSide, string Asset, decimal? FiatAmount, int Page = 1, int PageSize = 20);

public sealed record TraderStats(
    Guid UserId,
    string DisplayName,
    bool Verified,
    DateTimeOffset MemberSince,
    int CompletedOrders30d,
    decimal CompletionRate30d,
    double? AverageReleaseMinutes,
    int PositiveFeedback,
    int NegativeFeedback);

public sealed record MarketAd(P2PAd Ad, decimal EffectivePrice, decimal MaxFiatAvailable, TraderStats Maker, IReadOnlyList<string> PaymentBanks);

public sealed record PaymentDetail(string BankName, string AccountNumber, string AccountName);
