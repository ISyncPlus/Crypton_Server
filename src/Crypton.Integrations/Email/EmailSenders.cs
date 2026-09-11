using Crypton.Core.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Crypton.Integrations.Email;

public sealed class SmtpOptions
{
    public const string Section = "Email:Smtp";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 1025;

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>None | Auto | StartTls | SslOnConnect</summary>
    public string Security { get; set; } = "None";

    public string FromAddress { get; set; } = "no-reply@crypton.local";

    public string FromName { get; set; } = "Crypton";
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string html, string text, CancellationToken ct)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();

        var security = o.Security.ToLowerInvariant() switch
        {
            "starttls" => SecureSocketOptions.StartTls,
            "sslonconnect" or "ssl" => SecureSocketOptions.SslOnConnect,
            "auto" => SecureSocketOptions.Auto,
            _ => SecureSocketOptions.None,
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(o.Host, o.Port, security, ct);
        if (!string.IsNullOrEmpty(o.Username))
        {
            await client.AuthenticateAsync(o.Username, o.Password, ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Writes emails to the log instead of sending them. Used when no SMTP server is configured.</summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string html, string text, CancellationToken ct)
    {
        logger.LogInformation("Email (not sent, logging only) to {To}: {Subject}\n{Text}", to, subject, text);
        return Task.CompletedTask;
    }
}
