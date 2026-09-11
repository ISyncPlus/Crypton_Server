using Crypton.Core.Common;
using Crypton.Core.Domain;

namespace Crypton.Core.P2P;

public static class P2PPricing
{
    public static decimal EffectivePrice(P2PAd ad, decimal marketPriceNgn) => ad.PriceType == P2PPriceType.Fixed
        ? ad.FixedPrice ?? 0m
        : MoneyMath.RoundHalfUp(marketPriceNgn * (1m + ad.FloatingMarginBps / 10_000m), 2);

    /// <summary>Crypto reserve needed for a sell ad: quantity plus the maker fee on the whole quantity.</summary>
    public static decimal SellAdReserve(decimal quantity, int makerFeeBps, int precision) =>
        quantity + MoneyMath.Ceil(MoneyMath.Bps(quantity, makerFeeBps), precision);

    /// <summary>
    /// Maker fee for one order on a sell ad. Fees are floored per order and the final fill absorbs whatever fee
    /// reserve is left, so the reserve is always sufficient and ends at exactly zero.
    /// </summary>
    public static decimal SellOrderFee(decimal quantity, decimal remainingQuantity, decimal reservedAmount, int makerFeeBps, int precision) =>
        quantity == remainingQuantity
            ? reservedAmount - quantity
            : MoneyMath.Floor(MoneyMath.Bps(quantity, makerFeeBps), precision);

    /// <summary>Maker fee for one order on a buy ad, taken from the crypto the maker receives.</summary>
    public static decimal BuyOrderFee(decimal quantity, int makerFeeBps, int precision) =>
        MoneyMath.Ceil(MoneyMath.Bps(quantity, makerFeeBps), precision);

    public static (decimal Quantity, decimal FiatAmount) Size(decimal price, decimal? fiatAmount, decimal? quantity, int precision)
    {
        if (price <= 0)
        {
            throw new AppException(ErrorCodes.PriceUnavailable, "This ad has no valid price right now.", 409);
        }

        if (quantity is { } q)
        {
            if (q <= 0 || !MoneyMath.HasMaxDecimals(q, precision))
            {
                throw new AppException(ErrorCodes.InvalidAmount, $"Quantity must be positive with at most {precision} decimals.");
            }

            return (q, MoneyMath.RoundHalfUp(q * price, 2));
        }

        if (fiatAmount is { } f)
        {
            if (f <= 0 || !MoneyMath.HasMaxDecimals(f, 2))
            {
                throw new AppException(ErrorCodes.InvalidAmount, "Enter a valid naira amount.");
            }

            var derived = MoneyMath.Floor(f / price, precision);
            return (derived, MoneyMath.RoundHalfUp(derived * price, 2));
        }

        throw AppException.Validation("Enter either a naira amount or a crypto quantity.");
    }
}
