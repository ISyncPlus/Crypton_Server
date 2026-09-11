using Crypton.Api.Auth;
using Crypton.Api.Contracts;
using Crypton.Api.Infrastructure;
using Crypton.Core.Admin;
using Crypton.Core.Data;
using Crypton.Core.Fiat;
using Crypton.Core.Pricing;
using Crypton.Core.Wallets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crypton.Api.Controllers.Admin;

[Route("api/admin")]
[Authorize(Policy = Policies.Staff)]
public sealed class AdminDashboardController(
    CryptonDbContext db,
    AnalyticsService analytics,
    ReportService reports,
    LedgerCheckService ledgerCheck,
    PriceCache priceCache,
    IPriceProvider priceProvider,
    FiatService fiat,
    IOptions<BlockchainOptions> blockchain,
    TimeProvider clock) : ApiControllerBase
{
    [HttpGet("dashboard")]
    public async Task<AdminDashboardDto> Dashboard(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var jobs = await db.JobRuns.AsNoTracking().OrderBy(j => j.Name)
            .Select(j => new JobStatusDto(j.Name, j.LastSucceededAt, j.LastFailedAt, j.LastError, j.ConsecutiveFailures))
            .ToListAsync(ct);
        return new AdminDashboardDto(
            await analytics.OverviewAsync(now.AddDays(-1), now, ct),
            await analytics.OverviewAsync(now.AddDays(-7), now, ct),
            await analytics.QueuesAsync(ct),
            jobs,
            new PriceFeedStatusDto(priceProvider.Name, priceCache.LastRefreshAt, priceCache.LastError),
            blockchain.Value.Mode,
            fiat.ProviderName);
    }

    [HttpGet("analytics/overview")]
    public Task<AnalyticsOverview> Overview([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        var (start, end) = Range(from, to);
        return analytics.OverviewAsync(start, end, ct);
    }

    [HttpGet("analytics/timeseries")]
    public Task<IReadOnlyList<TimePoint>> TimeSeries([FromQuery] string metric, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        var (start, end) = Range(from, to);
        return analytics.TimeSeriesAsync(metric, start, end, ct);
    }

    [HttpGet("reports/{type}.csv")]
    [Authorize(Policy = Policies.Compliance)]
    public async Task Report(string type, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        if (!ReportService.Types.Contains(type))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var (start, end) = Range(from, to);
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"crypton-{type}-{start:yyyyMMdd}-{end:yyyyMMdd}.csv\"";
        await reports.WriteCsvAsync(type, start, end, Response.Body, ct);
    }

    [HttpGet("system/ledger-check")]
    [Authorize(Policy = Policies.Compliance)]
    public Task<LedgerCheckReport> LedgerCheck(CancellationToken ct) => ledgerCheck.RunAsync(ct);

    private (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset? from, DateTimeOffset? to)
    {
        var end = to ?? clock.GetUtcNow();
        var start = from ?? end.AddDays(-30);
        if (start >= end || end - start > TimeSpan.FromDays(366))
        {
            throw Core.Common.AppException.Validation("Choose a date range of up to one year.");
        }

        return (start, end);
    }
}
