using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Crypton.Api.Auth;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "crypton";

    public string Audience { get; set; } = "crypton-web";

    /// <summary>At least 32 random bytes, base64 encoded. Generate with: openssl rand -base64 48</summary>
    public string SigningKey { get; set; } = "";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>Absolute lifetime of a session (refresh token family), regardless of activity.</summary>
    public int SessionMaxDays { get; set; } = 30;

    public bool RefreshCookieSecure { get; set; } = true;

    /// <summary>Strict (same-site deployments) or None (API and web app on different sites; requires HTTPS).</summary>
    public string RefreshCookieSameSite { get; set; } = "Strict";

    public SymmetricSecurityKey GetSigningKey()
    {
        if (string.IsNullOrWhiteSpace(SigningKey))
        {
            throw new InvalidOperationException("Jwt:SigningKey is not configured. Generate one with `openssl rand -base64 48`.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(SigningKey);
        }
        catch (FormatException)
        {
            bytes = Encoding.UTF8.GetBytes(SigningKey);
        }

        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        }

        return new SymmetricSecurityKey(bytes);
    }
}
