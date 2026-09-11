using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Notifications;
using Crypton.Core.Settings;
using Crypton.Core.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crypton.Core.Kyc;

public enum KycVerificationOutcome
{
    Verified,
    NotVerified,
    ManualReview,
}

public sealed record KycIdentityCheck(KycIdType IdType, string IdNumber, string FirstName, string LastName, DateOnly DateOfBirth, string? PhoneNumber);

public sealed record KycVerificationResult(KycVerificationOutcome Outcome, string Summary, object? Details = null);

/// <summary>Automated identity check for tier 1 (e.g. Dojah NIN/BVN lookup).</summary>
public interface IKycVerifier
{
    string Name { get; }

    Task<KycVerificationResult> VerifyIdentityAsync(KycIdentityCheck check, CancellationToken ct);
}

/// <summary>Default: every submission goes to the compliance review queue.</summary>
public sealed class ManualKycVerifier : IKycVerifier
{
    public string Name => "manual";

    public Task<KycVerificationResult> VerifyIdentityAsync(KycIdentityCheck check, CancellationToken ct) =>
        Task.FromResult(new KycVerificationResult(KycVerificationOutcome.ManualReview, "Queued for manual review."));
}

public sealed class KycOptions
{
    public const string Section = "Kyc";

    /// <summary>Manual | Dojah</summary>
    public string Provider { get; set; } = "Manual";

    /// <summary>Secret used to hash ID numbers for duplicate detection. Set a long random value in production.</summary>
    public string IdHashKey { get; set; } = "";

    public int MinimumAge { get; set; } = 18;
}

public sealed record Tier1Request(
    string FirstName,
    string LastName,
    DateOnly DateOfBirth,
    string PhoneNumber,
    string AddressLine,
    string City,
    string State,
    KycIdType IdType,
    string IdNumber);

public sealed record KycUpload(KycDocumentType Type, string FileName, long Length, Func<Stream> OpenStream);

public sealed partial class KycService(
    CryptonDbContext db,
    IKycVerifier verifier,
    IFileStorage storage,
    IDataProtectionProvider dataProtection,
    NotificationService notifications,
    AuditService audit,
    IOptions<KycOptions> options,
    TimeProvider clock,
    ILogger<KycService> logger)
{
    private IDataProtector Protector => dataProtection.CreateProtector("Crypton.Kyc.IdNumber.v1");

    public async Task<KycSubmission> SubmitTier1Async(Guid userId, Tier1Request request, string? ipAddress, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound("User");
        if (user.KycTier >= 1)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "Your identity is already verified.");
        }

        if (await db.KycSubmissions.AnyAsync(s => s.UserId == userId && s.TargetTier == 1 && s.Status == KycSubmissionStatus.Pending, ct))
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "You already have a verification under review.");
        }

        var firstName = CleanName(request.FirstName, "First name");
        var lastName = CleanName(request.LastName, "Last name");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (request.DateOfBirth > today.AddYears(-options.Value.MinimumAge) || request.DateOfBirth < today.AddYears(-120))
        {
            throw AppException.Validation($"You must be at least {options.Value.MinimumAge} years old.");
        }

        var phone = NormalizeNigerianPhone(request.PhoneNumber) ?? throw AppException.Validation("Enter a valid Nigerian phone number.");
        var idNumber = (request.IdNumber ?? "").Trim();
        if (!ElevenDigits().IsMatch(idNumber))
        {
            throw AppException.Validation($"{request.IdType} numbers are 11 digits.");
        }

        if (string.IsNullOrWhiteSpace(request.AddressLine) || request.AddressLine.Length > 200 ||
            string.IsNullOrWhiteSpace(request.City) || request.City.Length > 80 ||
            string.IsNullOrWhiteSpace(request.State) || request.State.Length > 80)
        {
            throw AppException.Validation("Enter your full residential address.");
        }

        var idHash = HashIdNumber(request.IdType, idNumber);
        var usedElsewhere = await db.KycSubmissions.AnyAsync(s =>
            s.IdNumberHash == idHash && s.UserId != userId && s.Status != KycSubmissionStatus.Rejected, ct);
        if (usedElsewhere)
        {
            throw AppException.Conflict("identity_in_use", "This ID is already linked to another account. Contact support if you believe this is a mistake.");
        }

        var now = clock.GetUtcNow();
        var submission = new KycSubmission
        {
            Id = Ids.New(),
            UserId = userId,
            TargetTier = 1,
            Status = KycSubmissionStatus.Pending,
            Provider = verifier.Name,
            FirstName = firstName,
            LastName = lastName,
            DateOfBirth = request.DateOfBirth,
            PhoneNumber = phone,
            AddressLine = request.AddressLine.Trim(),
            City = request.City.Trim(),
            State = request.State.Trim(),
            Country = "NG",
            IdType = request.IdType,
            IdNumberProtected = Protector.Protect(idNumber),
            IdNumberMasked = new string('•', 7) + idNumber[^4..],
            IdNumberHash = idHash,
            CreatedAt = now,
        };
        db.KycSubmissions.Add(submission);
        audit.Record(AuditActions.KycSubmitted, userId, new AuditContext(userId, ipAddress), "kyc_submission", submission.Id.ToString(), new { tier = 1, request.IdType });
        await db.SaveChangesAsync(ct);

        KycVerificationResult result;
        try
        {
            result = await verifier.VerifyIdentityAsync(new KycIdentityCheck(request.IdType, idNumber, firstName, lastName, request.DateOfBirth, phone), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Automated KYC check via {Provider} failed; sending to manual review", verifier.Name);
            result = new KycVerificationResult(KycVerificationOutcome.ManualReview, "Automated check unavailable; queued for manual review.");
        }

        submission.ProviderResult = Json.Serialize(new { outcome = result.Outcome, summary = result.Summary, details = result.Details });
        switch (result.Outcome)
        {
            case KycVerificationOutcome.Verified:
                ApplyApproval(user, submission, reviewerId: null, now);
                await notifications.QueueAsync(userId, NotificationTypes.Kyc, "Identity verified",
                    "Your identity has been verified. Naira deposits, withdrawals and P2P trading are now unlocked.", "/account/verification", email: true, ct);
                break;

            case KycVerificationOutcome.NotVerified:
                submission.Status = KycSubmissionStatus.Rejected;
                submission.RejectionReason = result.Summary;
                submission.ReviewedAt = now;
                await notifications.QueueAsync(userId, NotificationTypes.Kyc, "Identity verification unsuccessful",
                    $"We couldn't verify your details: {result.Summary} Please check them and try again.", "/account/verification", email: true, ct);
                break;

            default:
                await notifications.QueueAsync(userId, NotificationTypes.Kyc, "Verification submitted",
                    "Thanks. Your details are being reviewed, usually within one business day.", "/account/verification", email: false, ct);
                break;
        }

        await db.SaveChangesAsync(ct);

        if (submission.Status == KycSubmissionStatus.Pending)
        {
            await notifications.NotifyStaffAsync([Roles.Admin, Roles.Compliance], "KYC tier 1 submission", $"{firstName} {lastName} submitted identity details.", "/admin/kyc", ct);
        }

        return submission;
    }

    public async Task<KycSubmission> SubmitTier2Async(Guid userId, string documentKind, IReadOnlyList<KycUpload> uploads, string? ipAddress, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw AppException.NotFound("User");
        if (user.KycTier < 1)
        {
            throw new AppException(ErrorCodes.KycRequired, "Complete identity verification (tier 1) first.", 403);
        }

        if (user.KycTier >= 2)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "You already have advanced verification.");
        }

        if (await db.KycSubmissions.AnyAsync(s => s.UserId == userId && s.TargetTier == 2 && s.Status == KycSubmissionStatus.Pending, ct))
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "You already have documents under review.");
        }

        var allowedKinds = new[] { "passport", "drivers_licence", "national_id", "voters_card" };
        if (!allowedKinds.Contains(documentKind))
        {
            throw AppException.Validation($"Document type must be one of: {string.Join(", ", allowedKinds)}.");
        }

        if (!uploads.Any(u => u.Type == KycDocumentType.IdFront) || !uploads.Any(u => u.Type == KycDocumentType.Selfie) || !uploads.Any(u => u.Type == KycDocumentType.ProofOfAddress))
        {
            throw AppException.Validation("Upload the front of your ID, a selfie holding the ID, and a proof of address.");
        }

        if (uploads.GroupBy(u => u.Type).Any(g => g.Count() > 1))
        {
            throw AppException.Validation("Upload one file per document type.");
        }

        var tier1 = await db.KycSubmissions.AsNoTracking()
            .Where(s => s.UserId == userId && s.TargetTier == 1 && s.Status == KycSubmissionStatus.Approved)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var now = clock.GetUtcNow();
        var submission = new KycSubmission
        {
            Id = Ids.New(),
            UserId = userId,
            TargetTier = 2,
            Status = KycSubmissionStatus.Pending,
            Provider = "manual",
            FirstName = tier1?.FirstName ?? user.FirstName,
            LastName = tier1?.LastName ?? user.LastName,
            DateOfBirth = tier1?.DateOfBirth,
            Country = "NG",
            DocumentKind = documentKind,
            CreatedAt = now,
        };

        foreach (var upload in uploads)
        {
            if (upload.Length <= 0 || upload.Length > UploadValidation.MaxBytes)
            {
                throw AppException.Validation($"{upload.Type} must be a file up to 5 MB.");
            }

            await using var input = upload.OpenStream();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);
            if (buffer.Length > UploadValidation.MaxBytes)
            {
                throw AppException.Validation($"{upload.Type} must be a file up to 5 MB.");
            }

            var bytes = buffer.ToArray();
            var contentType = UploadValidation.DetectContentType(bytes.AsSpan(0, Math.Min(bytes.Length, 16)));
            if (contentType is null || (upload.Type == KycDocumentType.Selfie && contentType == "application/pdf"))
            {
                throw AppException.Validation(upload.Type == KycDocumentType.Selfie
                    ? "The selfie must be a JPEG or PNG image."
                    : $"{upload.Type} must be a JPEG, PNG or PDF file.");
            }

            buffer.Position = 0;
            var key = await storage.SaveAsync(buffer, UploadValidation.Extensions[contentType], ct);
            submission.Documents.Add(new KycDocument
            {
                Id = Ids.New(),
                SubmissionId = submission.Id,
                UserId = userId,
                Type = upload.Type,
                StorageKey = key,
                ContentType = contentType,
                SizeBytes = bytes.LongLength,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                CreatedAt = now,
            });
        }

        db.KycSubmissions.Add(submission);
        audit.Record(AuditActions.KycSubmitted, userId, new AuditContext(userId, ipAddress), "kyc_submission", submission.Id.ToString(), new { tier = 2, documentKind });
        await notifications.QueueAsync(userId, NotificationTypes.Kyc, "Documents submitted", "Your documents are being reviewed, usually within one business day.", "/account/verification", email: false, ct);
        await db.SaveChangesAsync(ct);
        await notifications.NotifyStaffAsync([Roles.Admin, Roles.Compliance], "KYC tier 2 submission", $"{submission.FirstName} {submission.LastName} uploaded documents.", "/admin/kyc", ct);
        return submission;
    }

    public async Task<KycSubmission> ApproveAsync(Guid adminId, Guid submissionId, string? note, CancellationToken ct = default)
    {
        var submission = await db.KycSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, ct) ?? throw AppException.NotFound("Submission");
        if (submission.Status != KycSubmissionStatus.Pending)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "Only pending submissions can be approved.");
        }

        var user = await db.Users.FirstAsync(u => u.Id == submission.UserId, ct);
        if (submission.TargetTier == 2 && user.KycTier < 1)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "The user must hold tier 1 before tier 2 can be approved.");
        }

        var now = clock.GetUtcNow();
        ApplyApproval(user, submission, adminId, now);
        submission.RejectionReason = note;
        audit.Record(AuditActions.AdminKycApproved, user.Id, new AuditContext(adminId, null), "kyc_submission", submission.Id.ToString(), new { submission.TargetTier, note });
        await notifications.QueueAsync(user.Id, NotificationTypes.Kyc, submission.TargetTier == 1 ? "Identity verified" : "Advanced verification approved",
            submission.TargetTier == 1
                ? "Your identity has been verified. Naira deposits, withdrawals and P2P trading are now unlocked."
                : "Your documents were approved and your limits have been raised.",
            "/account/verification", email: true, ct);
        await db.SaveChangesAsync(ct);
        return submission;
    }

    public async Task<KycSubmission> RejectAsync(Guid adminId, Guid submissionId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw AppException.Validation("A reason is required.");
        }

        var submission = await db.KycSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, ct) ?? throw AppException.NotFound("Submission");
        if (submission.Status != KycSubmissionStatus.Pending)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "Only pending submissions can be rejected.");
        }

        submission.Status = KycSubmissionStatus.Rejected;
        submission.RejectionReason = reason.Trim();
        submission.ReviewedBy = adminId;
        submission.ReviewedAt = clock.GetUtcNow();
        audit.Record(AuditActions.AdminKycRejected, submission.UserId, new AuditContext(adminId, null), "kyc_submission", submission.Id.ToString(), new { reason });
        await notifications.QueueAsync(submission.UserId, NotificationTypes.Kyc, "Verification needs attention",
            $"We couldn't approve your verification: {reason} You can submit again.", "/account/verification", email: true, ct);
        await db.SaveChangesAsync(ct);
        return submission;
    }

    public string? RevealIdNumber(KycSubmission submission) =>
        submission.IdNumberProtected is null ? null : Protector.Unprotect(submission.IdNumberProtected);

    public async Task<(Stream Content, string ContentType, string FileName)> OpenDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.KycDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw AppException.NotFound("Document");
        var stream = await storage.OpenReadAsync(document.StorageKey, ct);
        return (stream, document.ContentType, $"{document.Type}{UploadValidation.Extensions.GetValueOrDefault(document.ContentType, "")}");
    }

    internal static string? NormalizeNigerianPhone(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("234") && digits.Length == 13)
        {
            digits = digits[3..];
        }
        else if (digits.StartsWith('0') && digits.Length == 11)
        {
            digits = digits[1..];
        }

        return digits.Length == 10 && (digits[0] is '7' or '8' or '9') ? "+234" + digits : null;
    }

    private void ApplyApproval(AppUser user, KycSubmission submission, Guid? reviewerId, DateTimeOffset now)
    {
        submission.Status = KycSubmissionStatus.Approved;
        submission.ReviewedBy = reviewerId;
        submission.ReviewedAt = now;
        user.KycTier = Math.Max(user.KycTier, submission.TargetTier);
        if (submission.TargetTier == 1)
        {
            user.FirstName = submission.FirstName;
            user.LastName = submission.LastName;
            user.PhoneNumber = submission.PhoneNumber;
        }
    }

    private string HashIdNumber(KycIdType type, string idNumber)
    {
        var key = options.Value.IdHashKey;
        if (string.IsNullOrEmpty(key))
        {
            key = "crypton-development-id-hash-key";
        }

        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"{type}:{idNumber}")));
    }

    private static string CleanName(string? value, string field)
    {
        var name = Regex.Replace((value ?? "").Trim(), @"\s+", " ");
        if (name.Length is < 2 or > 100 || !NameChars().IsMatch(name))
        {
            throw AppException.Validation($"{field} should contain letters only.");
        }

        return name;
    }

    [GeneratedRegex("^[0-9]{11}$")]
    private static partial Regex ElevenDigits();

    [GeneratedRegex(@"^[\p{L}][\p{L}' .-]*$")]
    private static partial Regex NameChars();
}
