namespace Crypton.Core.Common;

/// <summary>
/// Directed rounding helpers. All ledger amounts are rounded to their asset precision with an
/// explicit direction so that no journal ever creates or destroys value through rounding.
/// </summary>
public static class MoneyMath
{
    public const int MaxPrecision = 18;

    public static decimal Floor(decimal value, int decimals) =>
        Math.Round(value, Clamp(decimals), MidpointRounding.ToNegativeInfinity);

    public static decimal Ceil(decimal value, int decimals) =>
        Math.Round(value, Clamp(decimals), MidpointRounding.ToPositiveInfinity);

    public static decimal RoundHalfUp(decimal value, int decimals) =>
        Math.Round(value, Clamp(decimals), MidpointRounding.AwayFromZero);

    /// <summary>True when <paramref name="value"/> has no more than <paramref name="decimals"/> decimal places.</summary>
    public static bool HasMaxDecimals(decimal value, int decimals) =>
        Math.Round(value, Clamp(decimals), MidpointRounding.ToZero) == value;

    /// <summary>Returns amount * bps / 10,000 (unrounded).</summary>
    public static decimal Bps(decimal amount, int basisPoints) => amount * basisPoints / 10_000m;

    /// <summary>Invariant string without trailing zeros, e.g. 1.2300 -> "1.23".</summary>
    public static string ToPlainString(decimal value) =>
        value.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Removes trailing zeros without changing the value.</summary>
    public static decimal Normalize(decimal value) =>
        decimal.Parse(ToPlainString(value), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture);

    private static int Clamp(int decimals) => Math.Clamp(decimals, 0, 28);
}
