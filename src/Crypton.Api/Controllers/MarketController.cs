using Crypton.Api.Contracts;
using Crypton.Core.Assets;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Pricing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Api.Controllers;

[Route("api/market")]
[AllowAnonymous]
public sealed class MarketController(AssetCatalog assets, PriceService prices, CryptonDbContext db, TimeProvider clock) : ApiControllerBase
{
    [HttpGet("assets")]
    public async Task<IReadOnlyList<AssetDto>> Assets(CancellationToken ct) =>
        (await assets.GetAllAsync(ct)).Select(a => a.ToDto()).ToList();

    [HttpGet("prices")]
    public async Task<IReadOnlyList<PriceDto>> Prices(CancellationToken ct) =>
        (await prices.GetPricesAsync(ct)).Values
            .OrderBy(p => p.Asset)
            .Select(p => new PriceDto(p.Asset, p.PriceNgn, p.PriceUsd, p.Change24hPercent, p.UpdatedAt, p.Source))
            .ToList();

    [HttpGet("prices/{asset}/history")]
    public async Task<IReadOnlyList<PricePointDto>> History(string asset, [FromQuery] int hours = 24, CancellationToken ct = default)
    {
        var code = (await assets.GetAsync(asset, ct)).Code;
        var since = clock.GetUtcNow().AddHours(-Math.Clamp(hours, 1, 24 * 30));
        var ticks = await db.PriceTicks.AsNoTracking()
            .Where(t => t.Asset == code && t.CreatedAt >= since)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new PricePointDto(t.CreatedAt, t.PriceNgn))
            .ToListAsync(ct);

        // Thin to at most ~200 points for charts.
        if (ticks.Count <= 200)
        {
            return ticks;
        }

        var step = (int)Math.Ceiling(ticks.Count / 200.0);
        return ticks.Where((_, i) => i % step == 0 || i == ticks.Count - 1).ToList();
    }
}
