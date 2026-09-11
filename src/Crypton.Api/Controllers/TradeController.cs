using Crypton.Api.Contracts;
using Crypton.Core.Data;
using Crypton.Core.Trading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers;

[Route("api/trade")]
[EnableRateLimiting(RateLimitPolicies.Sensitive)]
public sealed class TradeController(TradeService trades, CryptonDbContext db) : ApiControllerBase
{
    [HttpPost("quotes")]
    public async Task<QuoteDto> Quote(QuoteRequestDto request, CancellationToken ct) =>
        (await trades.CreateQuoteAsync(CurrentUserId, new QuoteRequest(request.Kind, request.FromAsset, request.ToAsset, request.Amount, request.Side), ct)).ToDto();

    [HttpPost("orders")]
    public async Task<TradeOrderDto> Execute(ExecuteQuoteRequest request, CancellationToken ct) =>
        (await trades.ExecuteAsync(CurrentUserId, request.QuoteId, request.ClientOrderId, ct)).ToDto();

    [HttpGet("orders")]
    public async Task<PageDto<TradeOrderDto>> Orders([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var userId = CurrentUserId;
        var request = Page(page, pageSize);
        var query = db.TradeOrders.AsNoTracking().Where(o => o.UserId == userId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(o => o.CreatedAt).Skip(request.Skip).Take(request.SafePageSize).ToListAsync(ct);
        return items.Select(o => o.ToDto()).ToList().ToPage(request, total);
    }
}
