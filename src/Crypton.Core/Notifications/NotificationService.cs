using System.Net;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crypton.Core.Notifications;

public sealed class AppOptions
{
    public const string Section = "App";

    public string Name { get; set; } = "Crypton";

    /// <summary>Public URL of the web app, used in emails and payment callbacks.</summary>
    public string FrontendBaseUrl { get; set; } = "http://localhost:4200";

    /// <summary>Public URL of this API (for webhooks shown in the admin portal).</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5080";

    public string SupportEmail { get; set; } = "support@example.com";
}

public static class NotificationTypes
{
    public const string Security = "security";
    public const string Deposit = "deposit";
    public const string Withdrawal = "withdrawal";
    public const string Trade = "trade";
    public const string Kyc = "kyc";
    public const string P2P = "p2p";
    public const string Compliance = "compliance";
    public const string System = "system";
}

/// <summary>In-app notifications plus transactional email through the outbox.</summary>
public sealed class NotificationService(CryptonDbContext db, IOptions<AppOptions> app, TimeProvider clock)
{
    /// <summary>Adds an in-app notification (and optional email) to the current unit of work. Caller saves.</summary>
    public async Task QueueAsync(Guid userId, string type, string title, string body, string? link = null, bool email = false, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        db.Notifications.Add(new Notification
        {
            Id = Ids.New(),
            UserId = userId,
            Type = type,
            Title = Truncate(title, 200),
            Body = Truncate(body, 2000),
            Link = link,
            CreatedAt = now,
        });

        if (email)
        {
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Email, u.FirstName })
                .FirstOrDefaultAsync(ct);
            if (user?.Email is not null)
            {
                QueueEmail(user.Email, title, EmailTemplates.Notification(app.Value, user.FirstName, title, body, link));
            }
        }
    }

    /// <summary>Adds a notification and saves immediately.</summary>
    public async Task NotifyAsync(Guid userId, string type, string title, string body, string? link = null, bool email = false, CancellationToken ct = default)
    {
        await QueueAsync(userId, type, title, body, link, email, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task NotifyStaffAsync(string[] roles, string title, string body, string? link, CancellationToken ct = default)
    {
        var normalized = roles.Select(r => r.ToUpperInvariant()).ToArray();
        var staffIds = await (from ur in db.UserRoles
                              join r in db.Roles on ur.RoleId equals r.Id
                              where normalized.Contains(r.NormalizedName!)
                              select ur.UserId).Distinct().ToListAsync(ct);
        foreach (var id in staffIds)
        {
            await QueueAsync(id, NotificationTypes.Compliance, title, body, link, email: false, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    public void QueueEmail(string to, string subject, EmailContent content)
    {
        var now = clock.GetUtcNow();
        db.EmailOutbox.Add(new EmailOutboxMessage
        {
            Id = Ids.New(),
            ToEmail = to,
            Subject = Truncate($"{subject}", 300),
            HtmlBody = content.Html,
            TextBody = content.Text,
            Status = EmailStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        });
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public sealed record EmailContent(string Html, string Text);

public static class EmailTemplates
{
    public static EmailContent Notification(AppOptions app, string? firstName, string title, string body, string? link)
    {
        var url = link is null ? null : Combine(app.FrontendBaseUrl, link);
        return Layout(app, title, firstName, body, url is null ? null : ("Open " + app.Name, url));
    }

    public static EmailContent ConfirmEmail(AppOptions app, string firstName, string url) =>
        Layout(app, "Confirm your email", firstName,
            $"Welcome to {app.Name}. Confirm your email address to finish creating your account. This link expires in 24 hours.",
            ("Confirm email", url));

    public static EmailContent ResetPassword(AppOptions app, string firstName, string url) =>
        Layout(app, "Reset your password", firstName,
            "We received a request to reset your password. If this wasn't you, ignore this email and consider enabling two-factor authentication. For your security, withdrawals are paused for 24 hours after a password reset.",
            ("Reset password", url));

    public static EmailContent NewDeviceLogin(AppOptions app, string firstName, string device, string ip, DateTimeOffset at) =>
        Layout(app, "New sign-in to your account", firstName,
            $"Your account was accessed from a new device.\n\nDevice: {device}\nIP address: {ip}\nTime: {at:yyyy-MM-dd HH:mm} UTC\n\nIf this wasn't you, reset your password immediately and contact support.",
            null);

    public static string Combine(string baseUrl, string path) => $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private static EmailContent Layout(AppOptions app, string title, string? firstName, string body, (string Label, string Url)? action)
    {
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hello," : $"Hi {firstName},";
        var e = (string s) => WebUtility.HtmlEncode(s);
        var paragraphs = string.Join("", body.Split("\n\n").Select(p => $"<p style=\"margin:0 0 16px;line-height:1.55\">{e(p).Replace("\n", "<br>")}</p>"));
        var button = action is null
            ? ""
            : $"<p style=\"margin:24px 0\"><a href=\"{e(action.Value.Url)}\" style=\"background:#111;color:#fff;padding:12px 20px;border-radius:6px;text-decoration:none;font-weight:600\">{e(action.Value.Label)}</a></p><p style=\"font-size:12px;color:#666;word-break:break-all\">{e(action.Value.Url)}</p>";

        var html = $"""
            <!doctype html>
            <html><body style="margin:0;background:#f4f1ea;font-family:-apple-system,Segoe UI,Helvetica,Arial,sans-serif;color:#161512">
            <div style="max-width:560px;margin:0 auto;padding:32px 20px">
              <div style="font-weight:700;letter-spacing:.08em;text-transform:uppercase;font-size:13px;margin-bottom:24px">{e(app.Name)}</div>
              <div style="background:#fff;border:1px solid #e4dfd3;border-radius:10px;padding:28px">
                <h1 style="font-size:20px;margin:0 0 16px">{e(title)}</h1>
                <p style="margin:0 0 16px">{e(greeting)}</p>
                {paragraphs}
                {button}
              </div>
              <p style="font-size:12px;color:#77726a;margin-top:20px">You received this because you have an account with {e(app.Name)}. Need help? {e(app.SupportEmail)}</p>
            </div>
            </body></html>
            """;

        var text = $"{title}\n\n{greeting}\n\n{body}\n\n{(action is null ? "" : $"{action.Value.Label}: {action.Value.Url}\n\n")}— {app.Name}";
        return new EmailContent(html, text);
    }
}
