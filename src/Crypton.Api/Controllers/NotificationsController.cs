using Crypton.Api.Contracts;
using Crypton.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers;

[Route("api/notifications")]
public sealed class NotificationsController(CryptonDbContext db, TimeProvider clock) : ApiControllerBase
{
    [HttpGet]
    public async Task<PageDto<NotificationDto>> List([FromQuery] bool unreadOnly = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var request = Page(page, pageSize);
        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(n => n.CreatedAt).Skip(request.Skip).Take(request.SafePageSize)
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.Link, n.ReadAt != null, n.CreatedAt))
            .ToListAsync(ct);
        return items.ToPage(request, total);
    }

    [HttpGet("unread-count")]
    public async Task<CountDto> UnreadCount(CancellationToken ct)
    {
        var userId = CurrentUserId;
        return new CountDto(await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct));
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId;
        var now = clock.GetUtcNow();
        await db.Notifications.Where(n => n.Id == id && n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        var userId = CurrentUserId;
        var now = clock.GetUtcNow();
        await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        return NoContent();
    }
}
