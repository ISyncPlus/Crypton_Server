using System.Security.Claims;
using System.Security.Cryptography;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Crypton.Api.Auth;

public sealed record IssuedTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, Guid SessionId);

public enum RefreshOutcome
{
    Success,
    Invalid,
    ReuseDetected,
    RaceRetry,
}

public sealed record RefreshResult(RefreshOutcome Outcome, IssuedTokens? Tokens, AppUser? User);

/// <summary>Short-lived JWT access tokens plus rotating, single-use refresh tokens grouped into sessions.</summary>
public sealed class TokenService(CryptonDbContext db, UserManager<AppUser> users, IOptions<JwtOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(30);

    public async Task<IssuedTokens> CreateSessionAsync(AppUser user, bool twoFactorVerified, string? ip, string? userAgent, CancellationToken ct)
    {
        var familyId = Ids.New();
        return await IssueAsync(user, familyId, twoFactorVerified, ip, userAgent, null, ct);
    }

    public async Task<RefreshResult> RefreshAsync(string rawRefreshToken, string? ip, string? userAgent, CancellationToken ct)
    {
        var hash = Hash(rawRefreshToken);
        var now = clock.GetUtcNow();
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
        {
            return new RefreshResult(RefreshOutcome.Invalid, null, null);
        }

        if (token.RevokedAt is not null)
        {
            // A rotated token presented again within seconds is almost always two tabs refreshing at once.
            if (token.ReplacedByTokenId is not null && now - token.RevokedAt.Value <= RotationGrace)
            {
                return new RefreshResult(RefreshOutcome.RaceRetry, null, null);
            }

            if (token.ReplacedByTokenId is not null)
            {
                await RevokeFamilyAsync(token.FamilyId, "reuse_detected", ct);
                return new RefreshResult(RefreshOutcome.ReuseDetected, null, null);
            }

            return new RefreshResult(RefreshOutcome.Invalid, null, null);
        }

        if (token.ExpiresAt <= now)
        {
            return new RefreshResult(RefreshOutcome.Invalid, null, null);
        }

        var sessionStart = await db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId).MinAsync(t => t.CreatedAt, ct);
        if (now - sessionStart > TimeSpan.FromDays(options.Value.SessionMaxDays))
        {
            await RevokeFamilyAsync(token.FamilyId, "session_expired", ct);
            return new RefreshResult(RefreshOutcome.Invalid, null, null);
        }

        var user = await users.FindByIdAsync(token.UserId.ToString());
        if (user is null || user.Status == UserStatus.Closed)
        {
            return new RefreshResult(RefreshOutcome.Invalid, null, null);
        }

        // Rotate atomically: only one request may consume this token, and the replacement becomes
        // visible in the same transaction so the session never appears revoked mid-rotation.
        return await db.InTransactionAsync(async token2 =>
        {
            var consumed = await db.RefreshTokens
                .Where(t => t.Id == token.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, "rotated"), token2);
            if (consumed != 1)
            {
                return new RefreshResult(RefreshOutcome.RaceRetry, null, null);
            }

            var issued = await IssueAsync(user, token.FamilyId, token.TwoFactorVerified, ip, userAgent, token.Id, token2);
            return new RefreshResult(RefreshOutcome.Success, issued, user);
        }, ct);
    }

    public async Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, reason), ct);
    }

    public async Task RevokeAllAsync(Guid userId, string reason, Guid? exceptFamilyId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && (exceptFamilyId == null || t.FamilyId != exceptFamilyId))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, reason), ct);
    }

    public async Task<Guid?> FindFamilyAsync(string rawRefreshToken, CancellationToken ct)
    {
        var hash = Hash(rawRefreshToken);
        return await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(ct);
    }

    private async Task<IssuedTokens> IssueAsync(AppUser user, Guid familyId, bool twoFactorVerified, string? ip, string? userAgent, Guid? replacesId, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        var entity = new RefreshToken
        {
            Id = Ids.New(),
            UserId = user.Id,
            TokenHash = Hash(raw),
            FamilyId = familyId,
            TwoFactorVerified = twoFactorVerified,
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = now.AddDays(o.RefreshTokenDays),
            IpAddress = ip,
            UserAgent = userAgent,
        };
        db.RefreshTokens.Add(entity);

        if (replacesId is { } previous)
        {
            await db.RefreshTokens.Where(t => t.Id == previous).ExecuteUpdateAsync(s => s.SetProperty(t => t.ReplacedByTokenId, entity.Id), ct);
        }

        await db.SaveChangesAsync(ct);

        var roles = await users.GetRolesAsync(user);
        var accessExpires = now.AddMinutes(o.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("email", user.Email ?? ""),
            new("name", user.FullName),
            new("sid", familyId.ToString()),
            new("mfa", twoFactorVerified ? "true" : "false"),
            new("jti", Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(roles.Select(r => new Claim("role", r)));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = accessExpires.UtcDateTime,
            SigningCredentials = new SigningCredentials(o.GetSigningKey(), SecurityAlgorithms.HmacSha256),
        };

        var jwt = new JsonWebTokenHandler().CreateToken(descriptor);
        return new IssuedTokens(jwt, accessExpires, raw, entity.ExpiresAt, familyId);
    }

    private static string Hash(string raw) => Ids.Sha256Hex(raw);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
