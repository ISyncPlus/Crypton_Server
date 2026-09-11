using Crypton.Api.Auth;
using Crypton.Api.Contracts;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.P2P;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers.Admin;

[Route("api/admin/p2p")]
[Authorize(Policy = Policies.Compliance)]
public sealed class AdminP2PController(CryptonDbContext db, P2POrderService orders, P2PAdService ads, AuditService audit, Core.Storage.IFileStorage storage) : ApiControllerBase
{
    [HttpGet("orders")]
    public async Task<PageDto<AdminP2POrderDto>> Orders([FromQuery] P2POrderStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = Query();
        if (status is not null)
        {
            query = query.Where(x => x.Order.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.Order.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return rows.Select(ToDto).ToList().ToPage(request, total);
    }

    [HttpGet("disputes")]
    public async Task<PageDto<AdminDisputeDto>> Disputes([FromQuery] P2PDisputeStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = db.P2PDisputes.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(d => d.Status == status);
        }

        var total = await query.CountAsync(ct);
        var ids = await query.OrderBy(d => d.Status == P2PDisputeStatus.Open ? 0 : 1).ThenBy(d => d.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).Select(d => d.Id).ToListAsync(ct);
        var items = new List<AdminDisputeDto>();
        foreach (var id in ids)
        {
            items.Add(await DisputeDtoAsync(id, ct));
        }

        return items.ToPage(request, total);
    }

    [HttpGet("disputes/{id:guid}")]
    public Task<AdminDisputeDto> Dispute(Guid id, CancellationToken ct) => DisputeDtoAsync(id, ct);

    [HttpGet("disputes/{id:guid}/evidence/{evidenceId:guid}")]
    public async Task<IActionResult> EvidenceFile(Guid id, Guid evidenceId, CancellationToken ct)
    {
        var evidence = await db.P2PDisputeEvidence.AsNoTracking().FirstOrDefaultAsync(e => e.Id == evidenceId && e.DisputeId == id, ct);
        if (evidence?.StorageKey is null)
        {
            throw AppException.NotFound("File");
        }

        return File(await storage.OpenReadAsync(evidence.StorageKey, ct), evidence.ContentType ?? "application/octet-stream", evidence.FileName);
    }

    [HttpPost("disputes/{id:guid}/resolve")]
    public async Task<AdminDisputeDto> Resolve(Guid id, AdminResolveDisputeRequest request, CancellationToken ct)
    {
        await orders.ResolveDisputeAsync(CurrentUserId, id, request.ReleaseToBuyer, request.Note, ct);
        return await DisputeDtoAsync(id, ct);
    }

    [HttpGet("ads")]
    public async Task<PageDto<AdminAdDto>> Ads([FromQuery] P2PAdStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var request = Page(page, pageSize);
        var query = from a in db.P2PAds.AsNoTracking()
                    join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                    select new { a, u.Email };
        if (status is not null)
        {
            query = query.Where(x => x.a.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.a.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return rows.Select(x => new AdminAdDto(x.a.Id, x.a.UserId, x.Email ?? "", x.a.Side, x.a.Asset, x.a.PriceType, x.a.FixedPrice, x.a.FloatingMarginBps, x.a.TotalQuantity,
            x.a.RemainingQuantity, x.a.Status, x.a.SuspendedByAdmin, x.a.CreatedAt)).ToList().ToPage(request, total);
    }

    [HttpPost("ads/{id:guid}/suspend")]
    public async Task<IActionResult> Suspend(Guid id, AdminReasonRequest request, CancellationToken ct)
    {
        await db.InTransactionAsync(async token =>
        {
            var ad = await ads.LockAsync(id, token) ?? throw AppException.NotFound("Ad");
            ad.SuspendedByAdmin = true;
            if (ad.Status == P2PAdStatus.Active)
            {
                ad.Status = P2PAdStatus.Paused;
            }

            audit.Record(AuditActions.AdminAdSuspended, ad.UserId, new AuditContext(CurrentUserId, null), "p2p_ad", id.ToString(), new { request.Reason });
            await db.SaveChangesAsync(token);
        }, ct);
        return NoContent();
    }

    [HttpPost("ads/{id:guid}/unsuspend")]
    public async Task<IActionResult> Unsuspend(Guid id, CancellationToken ct)
    {
        var ad = await db.P2PAds.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw AppException.NotFound("Ad");
        ad.SuspendedByAdmin = false;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<OrderRow> Query() =>
        from o in db.P2POrders.AsNoTracking()
        join b in db.Users.AsNoTracking() on o.BuyerId equals b.Id
        join s in db.Users.AsNoTracking() on o.SellerId equals s.Id
        select new OrderRow { Order = o, BuyerEmail = b.Email, SellerEmail = s.Email };

    private async Task<AdminDisputeDto> DisputeDtoAsync(Guid id, CancellationToken ct)
    {
        var dispute = await db.P2PDisputes.AsNoTracking().Include(d => d.Evidence).FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw AppException.NotFound("Dispute");
        var row = await Query().FirstAsync(x => x.Order.Id == dispute.OrderId, ct);
        var o = row.Order;
        string Party(Guid who) => who == o.BuyerId ? "buyer" : who == o.SellerId ? "seller" : "support";
        var details = Json.Deserialize<List<PaymentDetail>>(o.PaymentDetails) ?? [];
        return new AdminDisputeDto(dispute.Id, dispute.Status, dispute.Reason, Party(dispute.OpenedBy), dispute.ResolutionNote, dispute.CreatedAt, dispute.ResolvedAt, ToDto(row),
            details.Select(d => new PaymentDetailDto(d.BankName, d.AccountNumber, d.AccountName)).ToList(), o.BuyerPaymentReference,
            dispute.Evidence.OrderBy(e => e.CreatedAt).Select(e => new DisputeEvidenceDto(e.Id, Party(e.UserId), e.Text, e.FileName, e.StorageKey is not null, e.CreatedAt)).ToList());
    }

    private static AdminP2POrderDto ToDto(OrderRow x) => new(x.Order.Id, x.Order.OrderNumber, x.Order.Status, x.Order.AdSide, x.Order.Asset, x.Order.Quantity, x.Order.Price,
        x.Order.FiatAmount, x.Order.Fee, x.Order.BuyerId, x.BuyerEmail ?? "", x.Order.SellerId, x.SellerEmail ?? "", x.Order.CreatedAt, x.Order.PaidAt, x.Order.CompletedAt);

    public sealed class OrderRow
    {
        public P2POrder Order { get; set; } = null!;

        public string? BuyerEmail { get; set; }

        public string? SellerEmail { get; set; }
    }
}
