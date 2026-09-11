namespace Crypton.Core.Domain;

public enum AmlAction
{
    /// <summary>Record an alert, let the activity continue.</summary>
    Flag,

    /// <summary>Hold the activity until compliance approves it.</summary>
    Review,

    /// <summary>Refuse the activity.</summary>
    Block,
}

public enum AmlAlertStatus
{
    Open,
    Dismissed,
    Confirmed,
}

public enum AmlSubjectType
{
    CryptoWithdrawal,
    FiatWithdrawal,
    CryptoDeposit,
    FiatDeposit,
    Trade,
    P2POrder,
    Login,
    Account,
}

public class AmlAlert
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string RuleCode { get; set; } = "";

    public AmlAction Action { get; set; }

    public int Severity { get; set; }

    public AmlSubjectType SubjectType { get; set; }

    public Guid? SubjectId { get; set; }

    public string Summary { get; set; } = "";

    public string? Details { get; set; }

    public AmlAlertStatus Status { get; set; }

    public Guid? ResolvedBy { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public string? ResolutionNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public class BlockedAddress
{
    public Guid Id { get; set; }

    public string Network { get; set; } = "";

    /// <summary>Normalized address (lowercase for Ethereum).</summary>
    public string Address { get; set; } = "";

    public string Reason { get; set; } = "";

    public string Source { get; set; } = "manual";

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
