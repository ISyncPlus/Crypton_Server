namespace Crypton.Core.Domain;

public enum KycSubmissionStatus
{
    Pending,
    Approved,
    Rejected,
}

public enum KycIdType
{
    NIN,
    BVN,
}

public enum KycDocumentType
{
    IdFront,
    IdBack,
    Selfie,
    ProofOfAddress,
}

public class KycSubmission
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public int TargetTier { get; set; }

    public KycSubmissionStatus Status { get; set; }

    /// <summary>manual | dojah</summary>
    public string Provider { get; set; } = "manual";

    public string FirstName { get; set; } = "";

    public string LastName { get; set; } = "";

    public DateOnly? DateOfBirth { get; set; }

    public string? PhoneNumber { get; set; }

    public string? AddressLine { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? Country { get; set; }

    public KycIdType? IdType { get; set; }

    /// <summary>ID number encrypted with ASP.NET Core Data Protection.</summary>
    public string? IdNumberProtected { get; set; }

    /// <summary>Last digits only, for display.</summary>
    public string? IdNumberMasked { get; set; }

    /// <summary>Keyed hash of type + number, used to stop one identity verifying several accounts.</summary>
    public string? IdNumberHash { get; set; }

    /// <summary>For tier 2: the type of ID document uploaded (passport, driver's licence...).</summary>
    public string? DocumentKind { get; set; }

    /// <summary>JSON summary of an automated provider check (never raw provider PII).</summary>
    public string? ProviderResult { get; set; }

    public Guid? ReviewedBy { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public string? RejectionReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<KycDocument> Documents { get; set; } = [];
}

public class KycDocument
{
    public Guid Id { get; set; }

    public Guid SubmissionId { get; set; }

    public Guid UserId { get; set; }

    public KycDocumentType Type { get; set; }

    public string StorageKey { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
