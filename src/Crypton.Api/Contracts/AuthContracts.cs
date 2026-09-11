using System.ComponentModel.DataAnnotations;
using Crypton.Core.Domain;

namespace Crypton.Api.Contracts;

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(10), MaxLength(128)] string Password,
    [Required, MinLength(2), MaxLength(100)] string FirstName,
    [Required, MinLength(2), MaxLength(100)] string LastName);

public sealed record EmailRequest([Required, EmailAddress, MaxLength(256)] string Email);

public sealed record ConfirmEmailRequest([Required] Guid UserId, [Required] string Token);

public sealed record LoginRequest([Required, EmailAddress] string Email, [Required, MaxLength(128)] string Password);

public sealed record TwoFactorLoginRequest([Required] string ChallengeToken, [MaxLength(10)] string? Code, [MaxLength(20)] string? RecoveryCode);

public sealed record ResetPasswordRequest([Required] Guid UserId, [Required] string Token, [Required, MinLength(10), MaxLength(128)] string NewPassword);

public sealed record UserDto(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string? DisplayName,
    int KycTier,
    UserStatus Status,
    bool TwoFactorEnabled,
    IReadOnlyList<string> Roles,
    DateTimeOffset? WithdrawalsLockedUntil,
    DateTimeOffset CreatedAt);

public sealed record AuthResponse(bool RequiresTwoFactor, string? ChallengeToken, string? AccessToken, DateTimeOffset? AccessTokenExpiresAt, UserDto? User);
