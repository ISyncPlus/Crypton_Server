using System.ComponentModel.DataAnnotations;
using Crypton.Core.Domain;
using Crypton.Core.Settings;

namespace Crypton.Api.Contracts;

public sealed record Tier1RequestDto(
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    [Required] DateOnly DateOfBirth,
    [Required, MaxLength(20)] string PhoneNumber,
    [Required, MaxLength(200)] string AddressLine,
    [Required, MaxLength(80)] string City,
    [Required, MaxLength(80)] string State,
    [Required] KycIdType IdType,
    [Required, MaxLength(20)] string IdNumber);

public sealed record KycSubmissionDto(Guid Id, int TargetTier, KycSubmissionStatus Status, string Provider, string? RejectionReason, string? DocumentKind, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt);

public sealed record KycStatusResponse(int CurrentTier, IReadOnlyList<KycTierLimits> Tiers, IReadOnlyList<KycSubmissionDto> Submissions, bool CanSubmitTier1, bool CanSubmitTier2);

public static class KycMappings
{
    public static KycSubmissionDto ToDto(this KycSubmission s) => new(s.Id, s.TargetTier, s.Status, s.Provider, s.RejectionReason, s.DocumentKind, s.CreatedAt, s.ReviewedAt);
}
