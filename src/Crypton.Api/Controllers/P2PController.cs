using Crypton.Api.Contracts;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.P2P;
using Crypton.Core.Settings;
using Crypton.Core.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers;

[Route("api/p2p")]
public sealed class P2PController(
    CryptonDbContext db,
    P2PAdService ads,
    P2POrderService orders,
    TraderStatsService traders,
    SettingsService settings,
    IFileStorage storage) : ApiControllerBase
{
    private static readonly P2POrderStatus[] OpenStatuses = [P2POrderStatus.PendingPayment, P2POrderStatus.Paid, P2POrderStatus.Disputed];

    [HttpGet("config")]
    [AllowAnonymous]
    public async Task<P2PSettings> Config(CancellationToken ct) => await settings.GetAsync<P2PSettings>(ct);

    /// <summary>Marketplace listing. <paramref name="side"/> is what the viewer wants to do: buy or sell crypto.</summary>
    [HttpGet("market")]
    [AllowAnonymous]
    public async Task<PageDto<MarketAdDto>> Market([FromQuery] string side, [FromQuery] string asset, [FromQuery] decimal? amount, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var adSide = side?.ToLowerInvariant() switch
        {
            "buy" => P2PAdSide.Sell,
            "sell" => P2PAdSide.Buy,
            _ => throw AppException.Validation("side must be 'buy' or 'sell'."),
        };
        Guid? viewer = User.Identity?.IsAuthenticated == true ? CurrentUserId : null;
        var result = await ads.ListMarketAsync(viewer, new MarketFilter(adSide, asset, amount, page, pageSize), ct);
        return result.Map(m => m.ToDto());
    }

    [HttpGet("ads/{id:guid}")]
    [AllowAnonymous]
    public async Task<MarketAdDto> Ad(Guid id, CancellationToken ct) => (await ads.GetPublicAsync(id, ct)).ToDto();

    [HttpGet("my-ads")]
    public async Task<IReadOnlyList<MyAdDto>> MyAds(CancellationToken ct) =>
        (await ads.ListMineAsync(CurrentUserId, ct)).Select(x => x.Ad.ToMyDto(x.EffectivePrice)).ToList();

    [HttpPost("ads")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<MyAdDto> CreateAd(CreateAdRequestDto request, CancellationToken ct)
    {
        var ad = await ads.CreateAsync(CurrentUserId, new CreateAdRequest(request.Side, request.Asset, request.PriceType, request.FixedPrice, request.FloatingMarginBps,
            request.TotalQuantity, request.MinOrderFiat, request.MaxOrderFiat, request.PaymentWindowMinutes, request.PaymentMethodIds ?? [], request.Terms), ct);
        return (await ads.ListMineAsync(CurrentUserId, ct)).Where(x => x.Ad.Id == ad.Id).Select(x => x.Ad.ToMyDto(x.EffectivePrice)).First();
    }

    [HttpPut("ads/{id:guid}")]
    public async Task<MyAdDto> UpdateAd(Guid id, UpdateAdRequestDto request, CancellationToken ct)
    {
        await ads.UpdateAsync(CurrentUserId, id, new UpdateAdRequest(request.PriceType, request.FixedPrice, request.FloatingMarginBps, request.MinOrderFiat,
            request.MaxOrderFiat, request.PaymentWindowMinutes, request.PaymentMethodIds ?? [], request.Terms, request.Status), ct);
        return (await ads.ListMineAsync(CurrentUserId, ct)).Where(x => x.Ad.Id == id).Select(x => x.Ad.ToMyDto(x.EffectivePrice)).First();
    }

    [HttpPost("ads/{id:guid}/close")]
    public async Task<MyAdDto> CloseAd(Guid id, CancellationToken ct)
    {
        var ad = await ads.CloseAsync(CurrentUserId, id, ct);
        return ad.ToMyDto(0);
    }

    [HttpPost("orders")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<P2POrderDto> CreateOrder(CreateP2POrderRequest request, CancellationToken ct)
    {
        var order = await orders.CreateAsync(CurrentUserId, new CreateOrderRequest(request.AdId, request.FiatAmount, request.Quantity, request.PaymentMethodId), ct);
        return await BuildOrderDtoAsync(order.Id, ct);
    }

    [HttpGet("orders")]
    public async Task<PageDto<P2POrderDto>> Orders([FromQuery] string? state, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var request = Page(page, pageSize);
        var query = db.P2POrders.AsNoTracking().Where(o => o.BuyerId == userId || o.SellerId == userId);
        query = state?.ToLowerInvariant() switch
        {
            "open" => query.Where(o => OpenStatuses.Contains(o.Status)),
            "closed" => query.Where(o => !OpenStatuses.Contains(o.Status)),
            _ => query,
        };

        var total = await query.CountAsync(ct);
        var ids = await query.OrderByDescending(o => o.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).Select(o => o.Id).ToListAsync(ct);
        var items = new List<P2POrderDto>();
        foreach (var id in ids)
        {
            items.Add(await BuildOrderDtoAsync(id, ct));
        }

        return items.ToPage(request, total);
    }

    [HttpGet("orders/{id:guid}")]
    public Task<P2POrderDto> Order(Guid id, CancellationToken ct) => BuildOrderDtoAsync(id, ct);

    [HttpPost("orders/{id:guid}/paid")]
    public async Task<P2POrderDto> MarkPaid(Guid id, MarkPaidRequest request, CancellationToken ct)
    {
        await orders.MarkPaidAsync(CurrentUserId, id, request.PaymentReference, ct);
        return await BuildOrderDtoAsync(id, ct);
    }

    [HttpPost("orders/{id:guid}/release")]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<P2POrderDto> Release(Guid id, ReleaseRequest request, CancellationToken ct)
    {
        await orders.ReleaseAsync(CurrentUserId, id, request.TwoFactorCode, ct);
        return await BuildOrderDtoAsync(id, ct);
    }

    [HttpPost("orders/{id:guid}/cancel")]
    public async Task<P2POrderDto> Cancel(Guid id, ReasonRequest request, CancellationToken ct)
    {
        await orders.CancelAsync(CurrentUserId, id, request.Reason, ct);
        return await BuildOrderDtoAsync(id, ct);
    }

    [HttpPost("orders/{id:guid}/dispute")]
    public async Task<P2POrderDto> Dispute(Guid id, ReasonRequest request, CancellationToken ct)
    {
        await orders.OpenDisputeAsync(CurrentUserId, id, request.Reason ?? "", ct);
        return await BuildOrderDtoAsync(id, ct);
    }

    [HttpPost("orders/{id:guid}/evidence")]
    [RequestSizeLimit(6_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<P2POrderDto> Evidence(Guid id, [FromForm] string? text, IFormFile? file, CancellationToken ct)
    {
        if (file is not null)
        {
            await using var stream = file.OpenReadStream();
            await orders.AddEvidenceAsync(CurrentUserId, id, text, (stream, file.FileName, file.Length), ct);
        }
        else
        {
            await orders.AddEvidenceAsync(CurrentUserId, id, text, null, ct);
        }

        return await BuildOrderDtoAsync(id, ct);
    }

    [HttpGet("orders/{id:guid}/evidence/{evidenceId:guid}/file")]
    public async Task<IActionResult> EvidenceFile(Guid id, Guid evidenceId, CancellationToken ct)
    {
        var userId = CurrentUserId;
        var order = await db.P2POrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || (order.BuyerId != userId && order.SellerId != userId))
        {
            throw AppException.NotFound("Order");
        }

        var evidence = await (from e in db.P2PDisputeEvidence.AsNoTracking()
                              join d in db.P2PDisputes.AsNoTracking() on e.DisputeId equals d.Id
                              where e.Id == evidenceId && d.OrderId == id
                              select e).FirstOrDefaultAsync(ct);
        if (evidence?.StorageKey is null)
        {
            throw AppException.NotFound("File");
        }

        var stream = await storage.OpenReadAsync(evidence.StorageKey, ct);
        return File(stream, evidence.ContentType ?? "application/octet-stream", evidence.FileName);
    }

    [HttpPost("orders/{id:guid}/feedback")]
    public async Task<P2POrderDto> Feedback(Guid id, FeedbackRequest request, CancellationToken ct)
    {
        await orders.LeaveFeedbackAsync(CurrentUserId, id, request.Positive, request.Comment, ct);
        return await BuildOrderDtoAsync(id, ct);
    }

    [HttpGet("traders/{userId:guid}")]
    [AllowAnonymous]
    public async Task<TraderProfileDto> Trader(Guid userId, CancellationToken ct)
    {
        var stats = await traders.GetAsync(userId, ct);
        var feedback = await (from f in db.P2PFeedback.AsNoTracking()
                              join u in db.Users.AsNoTracking() on f.FromUserId equals u.Id
                              where f.ToUserId == userId
                              orderby f.CreatedAt descending
                              select new FeedbackDto(f.Positive, f.Comment, u.DisplayName ?? "trader", f.CreatedAt))
            .Take(20)
            .ToListAsync(ct);
        return new TraderProfileDto(stats.ToDto(), feedback);
    }

    private async Task<P2POrderDto> BuildOrderDtoAsync(Guid id, CancellationToken ct)
    {
        var userId = CurrentUserId;
        var order = await db.P2POrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || (order.BuyerId != userId && order.SellerId != userId))
        {
            throw AppException.NotFound("Order");
        }

        var counterpartyId = order.BuyerId == userId ? order.SellerId : order.BuyerId;
        var counterparty = await traders.GetAsync(counterpartyId, ct);
        var cfg = await settings.GetAsync<P2PSettings>(ct);
        var dispute = await db.P2PDisputes.AsNoTracking().Include(d => d.Evidence).FirstOrDefaultAsync(d => d.OrderId == id, ct);
        var feedbackGiven = await db.P2PFeedback.AsNoTracking().AnyAsync(f => f.OrderId == id && f.FromUserId == userId, ct);
        var details = Json.Deserialize<List<PaymentDetail>>(order.PaymentDetails) ?? [];
        var receive = order.AdSide == P2PAdSide.Sell ? order.Quantity : order.Quantity - order.Fee;

        string Party(Guid who) => who == order.BuyerId ? "buyer" : who == order.SellerId ? "seller" : "support";

        return new P2POrderDto(
            order.Id,
            order.OrderNumber,
            order.AdId,
            order.BuyerId == userId ? "buyer" : "seller",
            order.AdSide,
            order.Asset,
            order.FiatCurrency,
            order.Quantity,
            order.Price,
            order.FiatAmount,
            order.MakerId == userId ? order.Fee : 0,
            receive,
            order.Status,
            order.PaymentDeadline,
            details.Select(d => new PaymentDetailDto(d.BankName, d.AccountNumber, d.AccountName)).ToList(),
            order.BuyerPaymentReference,
            order.CancelReason,
            counterparty.ToDto(),
            order.CreatedAt,
            order.PaidAt,
            order.CompletedAt,
            order.CancelledAt,
            order.PaidAt?.AddMinutes(cfg.DisputeAfterMinutes),
            dispute is null
                ? null
                : new DisputeDto(dispute.Id, dispute.Status, Party(dispute.OpenedBy), dispute.Reason, dispute.ResolutionNote, dispute.CreatedAt, dispute.ResolvedAt,
                    dispute.Evidence.OrderBy(e => e.CreatedAt).Select(e => new DisputeEvidenceDto(e.Id, Party(e.UserId), e.Text, e.FileName, e.StorageKey is not null, e.CreatedAt)).ToList()),
            feedbackGiven);
    }
}
