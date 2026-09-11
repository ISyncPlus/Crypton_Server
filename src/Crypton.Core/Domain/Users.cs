using Microsoft.AspNetCore.Identity;

namespace Crypton.Core.Domain;

public enum UserStatus
{
    Active,
    Frozen,
    Closed,
}

public class AppUser : IdentityUser<Guid>
{
    public string FirstName { get; set; } = "";

    public string LastName { get; set; } = "";

    /// <summary>Public nickname shown on the P2P marketplace.</summary>
    public string? DisplayName { get; set; }

    public string? Country { get; set; }

    public int KycTier { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Active;

    public string? FrozenReason { get; set; }

    /// <summary>Withdrawals are blocked until this time (set after password resets, 2FA changes, etc).</summary>
    public DateTimeOffset? WithdrawalsLockedUntil { get; set; }

    /// <summary>Last accepted TOTP time step, used to reject replayed codes.</summary>
    public long LastTotpTimeStep { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public class AppRole : IdentityRole<Guid>
{
    public AppRole()
    {
    }

    public AppRole(string name)
        : base(name)
    {
        NormalizedName = name.ToUpperInvariant();
    }
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Compliance = "Compliance";
    public const string Support = "Support";

    public static readonly string[] All = [Admin, Compliance, Support];
}

public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the opaque token. The raw token is only ever held by the browser cookie.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>All tokens rotated from one login share a family (= one session).</summary>
    public Guid FamilyId { get; set; }

    public bool TwoFactorVerified { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public Guid? ReplacedByTokenId { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }
}

public class KnownDevice
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string DeviceHash { get; set; } = "";

    public string? UserAgent { get; set; }

    public string? LastIpAddress { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }

    public Guid? UserId { get; set; }

    public Guid? ActorUserId { get; set; }

    public string Action { get; set; } = "";

    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public string? IpAddress { get; set; }

    public string? Data { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
