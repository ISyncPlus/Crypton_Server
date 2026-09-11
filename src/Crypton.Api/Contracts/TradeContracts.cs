using System.ComponentModel.DataAnnotations;
using Crypton.Core.Domain;

namespace Crypton.Api.Contracts;

public sealed record QuoteRequestDto(
    [Required] TradeKind Kind,
    [Required, MaxLength(10)] string FromAsset,
    [Required, MaxLength(10)] string ToAsset,
    [Required] decimal Amount,
    AmountSide Side = AmountSide.From);

public sealed record QuoteDto(Guid Id, TradeKind Kind, string FromAsset, string ToAsset, decimal FromAmount, decimal ToAmount, decimal Fee, string FeeAsset, decimal Rate, decimal NgnValue, DateTimeOffset ExpiresAt);

public sealed record ExecuteQuoteRequest([Required] Guid QuoteId, [MaxLength(64)] string? ClientOrderId);

public sealed record TradeOrderDto(Guid Id, TradeKind Kind, string FromAsset, string ToAsset, decimal FromAmount, decimal ToAmount, decimal Fee, string FeeAsset, decimal Rate, decimal NgnValue, DateTimeOffset CreatedAt);

public static class TradeMappings
{
    public static QuoteDto ToDto(this Quote q) => new(q.Id, q.Kind, q.FromAsset, q.ToAsset, q.FromAmount, q.ToAmount, q.Fee, q.FeeAsset, q.Rate, q.NgnValue, q.ExpiresAt);

    public static TradeOrderDto ToDto(this TradeOrder o) => new(o.Id, o.Kind, o.FromAsset, o.ToAsset, o.FromAmount, o.ToAmount, o.Fee, o.FeeAsset, o.Rate, o.NgnValue, o.CreatedAt);
}
