using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Crypton.Core.Notifications;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string html, string text, CancellationToken ct);
}

public sealed class EmailOutboxProcessor(CryptonDbContext db, IEmailSender sender, TimeProvider clock, ILogger<EmailOutboxProcessor> logger)
{
    private const int MaxAttempts = 8;

    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var batch = await db.EmailOutbox
            .Where(m => m.Status == EmailStatus.Pending && m.NextAttemptAt <= now)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            message.Attempts++;
            try
            {
                await sender.SendAsync(message.ToEmail, message.Subject, message.HtmlBody, message.TextBody, ct);
                message.Status = EmailStatus.Sent;
                message.SentAt = clock.GetUtcNow();
                message.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Email to {To} failed (attempt {Attempt})", message.ToEmail, message.Attempts);
                message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                if (message.Attempts >= MaxAttempts)
                {
                    message.Status = EmailStatus.Failed;
                }
                else
                {
                    message.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(3600, 15 * Math.Pow(2, message.Attempts)));
                }
            }

            await db.SaveChangesAsync(ct);
        }

        return batch.Count;
    }
}
