using System.Security.Cryptography;

namespace Crypton.Core.Security;

/// <summary>RFC 6238 time-based one-time passwords (HMAC-SHA1, 30 second steps, 6 digits).</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    public static long TimeStep(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    public static string Compute(byte[] key, long timeStep)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(timeStep & 0xff);
            timeStep >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                     | ((hash[offset + 1] & 0xff) << 16)
                     | ((hash[offset + 2] & 0xff) << 8)
                     | (hash[offset + 3] & 0xff);
        var otp = binary % 1_000_000;
        return otp.ToString("D6");
    }

    /// <summary>Returns the matching time step within +/- <paramref name="window"/> steps, or null.</summary>
    public static long? Match(byte[] key, string? code, DateTimeOffset now, int window = 1)
    {
        if (code is null)
        {
            return null;
        }

        code = code.Replace(" ", "").Replace("-", "");
        if (code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = TimeStep(now);
        for (var offset = -window; offset <= window; offset++)
        {
            var step = current + offset;
            var expected = Compute(key, step);
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(expected),
                    System.Text.Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }

    public static string OtpAuthUri(string issuer, string accountName, string base32Key) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}?secret={base32Key}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] Decode(string input)
    {
        var clean = input.Trim().TrimEnd('=').Replace(" ", "").Replace("-", "").ToUpperInvariant();
        var output = new byte[clean.Length * 5 / 8];
        int buffer = 0, bitsLeft = 0, index = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException($"Invalid base32 character '{c}'.");
            }

            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output[index++] = (byte)(buffer >> (bitsLeft - 8));
                bitsLeft -= 8;
            }
        }

        return output;
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return sb.ToString();
    }
}
