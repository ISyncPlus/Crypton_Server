using System.ComponentModel.DataAnnotations;
using Crypton.Core.Kyc;

namespace Crypton.Api.Contracts;

public sealed record UpdateProfileRequest(
    [MaxLength(100)] string? FirstName,
    [MaxLength(100)] string? LastName,
    [RegularExpression("^[a-zA-Z0-9_]{3,24}$", ErrorMessage = "Display names are 3-24 letters, numbers or underscores.")] string? DisplayName);

public sealed record ChangePasswordRequest([Required] string CurrentPassword, [Required, MinLength(10), MaxLength(128)] string NewPassword, string? TwoFactorCode);

public sealed record TwoFactorSetupResponse(string SharedKey, string OtpAuthUri);

public sealed record TwoFactorCodeRequest([Required, MaxLength(10)] string Code);

public sealed record DisableTwoFactorRequest([Required] string Password, string? Code, string? RecoveryCode);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

public sealed record TwoFactorStatusResponse(bool Enabled, int RecoveryCodesLeft);

public sealed record SessionDto(Guid Id, string Device, string? IpAddress, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Current);

public sealed record ActivityDto(string Action, string? IpAddress, DateTimeOffset CreatedAt);

public sealed record LimitUsageDto(LimitKind Kind, decimal DailyLimitNgn, decimal UsedNgn, decimal RemainingNgn);

public sealed record MeResponse(UserDto User, IReadOnlyList<LimitUsageDto> Limits, int UnreadNotifications);
