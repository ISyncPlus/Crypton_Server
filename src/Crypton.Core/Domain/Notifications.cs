namespace Crypton.Core.Domain;

public class Notification
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Type { get; set; } = "";

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    /// <summary>In-app route to open, e.g. /p2p/orders/{id}.</summary>
    public string? Link { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum EmailStatus
{
    Pending,
    Sent,
    Failed,
}

public class EmailOutboxMessage
{
    public Guid Id { get; set; }

    public string ToEmail { get; set; } = "";

    public string Subject { get; set; } = "";

    public string HtmlBody { get; set; } = "";

    public string TextBody { get; set; } = "";

    public EmailStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}
