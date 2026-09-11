using System.Text;
using Crypton.Core.Admin;
using Crypton.Core.Common;
using Crypton.Core.Kyc;
using Crypton.Core.Security;

namespace Crypton.Tests.Unit;

public class MoneyMathTests
{
    [Theory]
    [InlineData("1.234567891", 8, "1.23456789", "1.2345679")]
    [InlineData("0.000000001", 8, "0", "0.00000001")]
    [InlineData("100", 2, "100", "100")]
    [InlineData("-1.005", 2, "-1.01", "-1")]
    public void Directed_rounding(string value, int decimals, string floor, string ceil)
    {
        var v = decimal.Parse(value);
        Assert.Equal(decimal.Parse(floor), MoneyMath.Floor(v, decimals));
        Assert.Equal(decimal.Parse(ceil), MoneyMath.Ceil(v, decimals));
    }

    [Fact]
    public void Decimal_places_check()
    {
        Assert.True(MoneyMath.HasMaxDecimals(1.12345678m, 8));
        Assert.False(MoneyMath.HasMaxDecimals(1.123456789m, 8));
        Assert.True(MoneyMath.HasMaxDecimals(1.10000000000m, 2));
    }

    [Fact]
    public void Plain_string_drops_trailing_zeros()
    {
        Assert.Equal("1.23", MoneyMath.ToPlainString(1.2300m));
        Assert.Equal("0.00000001", MoneyMath.ToPlainString(0.00000001m));
        Assert.Equal("150000000", MoneyMath.ToPlainString(150000000.00m));
    }
}

public class TotpTests
{
    // RFC 6238 appendix B test vectors (SHA-1), truncated to 6 digits.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Matches_rfc6238_vectors(long unixTime, string expected)
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        var step = Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(unixTime));
        Assert.Equal(expected, Totp.Compute(key, step));
    }

    [Fact]
    public void Accepts_adjacent_window_and_rejects_garbage()
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var previous = Totp.Compute(key, Totp.TimeStep(now) - 1);
        Assert.Equal(Totp.TimeStep(now) - 1, Totp.Match(key, previous, now));
        Assert.Null(Totp.Match(key, "000000x", now));
        Assert.Null(Totp.Match(key, null, now));
        var old = Totp.Compute(key, Totp.TimeStep(now) - 5);
        Assert.Null(Totp.Match(key, old, now));
    }

    [Fact]
    public void Base32_round_trips()
    {
        var bytes = Encoding.ASCII.GetBytes("12345678901234567890");
        var encoded = Base32.Encode(bytes);
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", encoded);
        Assert.Equal(bytes, Base32.Decode(encoded.ToLowerInvariant()));
    }
}

public class KycAndReportTests
{
    [Theory]
    [InlineData("08031234567", "+2348031234567")]
    [InlineData("+234 803 123 4567", "+2348031234567")]
    [InlineData("2349031234567", "+2349031234567")]
    [InlineData("0803123456", null)]
    [InlineData("06031234567", null)]
    public void Normalizes_nigerian_phone_numbers(string input, string? expected) =>
        Assert.Equal(expected, KycService.NormalizeNigerianPhone(input));

    [Fact]
    public void Csv_format_escapes_and_neutralizes_formulas()
    {
        Assert.Equal("\"a,b\"", ReportService.Format("a,b"));
        Assert.Equal("'=1+1", ReportService.Format("=1+1"));
        Assert.Equal("-5", ReportService.Format(-5m));
        Assert.Equal("\"say \"\"hi\"\"\"", ReportService.Format("say \"hi\""));
    }

    [Fact]
    public void Bank_account_name_matching()
    {
        Assert.True(Crypton.Core.Fiat.FiatService.NamesMatch("OKAFOR ADAORA CHIDINMA", "Adaora", "Okafor"));
        Assert.False(Crypton.Core.Fiat.FiatService.NamesMatch("BALOGUN TUNDE", "Adaora", "Okafor"));
    }
}
