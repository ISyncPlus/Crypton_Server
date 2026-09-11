namespace Crypton.Core.Domain;

/// <summary>Admin-editable platform settings stored as JSON documents by key.</summary>
public class PlatformSetting
{
    public string Key { get; set; } = "";

    public string Value { get; set; } = "{}";

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class JobRun
{
    public string Name { get; set; } = "";

    public DateTimeOffset? LastStartedAt { get; set; }

    public DateTimeOffset? LastSucceededAt { get; set; }

    public DateTimeOffset? LastFailedAt { get; set; }

    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }
}

public class PriceTick
{
    public long Id { get; set; }

    public string Asset { get; set; } = "";

    public decimal PriceNgn { get; set; }

    public decimal PriceUsd { get; set; }

    public string Source { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

public enum SimulatedPaymentKind
{
    Charge,
    Transfer,
}

/// <summary>State for the built-in simulated payment provider (development / demo mode only).</summary>
public class SimulatedPayment
{
    public string Reference { get; set; } = "";

    public SimulatedPaymentKind Kind { get; set; }

    public long AmountMinor { get; set; }

    /// <summary>pending | success | failed | abandoned</summary>
    public string Status { get; set; } = "pending";

    public string? RecipientCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
