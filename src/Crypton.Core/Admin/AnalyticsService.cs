using System.Data.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Crypton.Core.Admin;

public sealed record TimePoint(DateOnly Date, decimal Value);

public sealed record AnalyticsOverview(
    DateTimeOffset From,
    DateTimeOffset To,
    int TotalUsers,
    int NewUsers,
    int VerifiedUsers,
    int TradeCount,
    decimal TradeVolumeNgn,
    decimal P2PVolumeNgn,
    int P2PCompletedOrders,
    decimal FiatDepositsNgn,
    decimal FiatWithdrawalsNgn,
    decimal CryptoDepositsNgn,
    decimal CryptoWithdrawalsNgn,
    IReadOnlyDictionary<string, decimal> FeesByAsset,
    decimal FeesNgnEstimate);

public sealed record QueueCounts(int PendingKyc, int PendingWithdrawals, int WithdrawalsNeedingAttention, int OpenAmlAlerts, int OpenDisputes);

public sealed class ReportingOptions
{
    public const string Section = "Reporting";

    public string TimeZone { get; set; } = "Africa/Lagos";
}

public sealed class AnalyticsService(CryptonDbContext db, PriceService prices, IOptions<ReportingOptions> reporting)
{
    public static readonly string[] Metrics = ["trade_volume", "p2p_volume", "signups", "fiat_deposits", "fiat_withdrawals", "crypto_deposits", "fees_ngn"];

    public async Task<AnalyticsOverview> OverviewAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var totalUsers = await db.Users.CountAsync(ct);
        var newUsers = await db.Users.CountAsync(u => u.CreatedAt >= from && u.CreatedAt < to, ct);
        var verified = await db.Users.CountAsync(u => u.KycTier >= 1, ct);
        var trades = db.TradeOrders.AsNoTracking().Where(o => o.CreatedAt >= from && o.CreatedAt < to);
        var tradeCount = await trades.CountAsync(ct);
        var tradeVolume = await trades.SumAsync(o => (decimal?)o.NgnValue, ct) ?? 0m;
        var p2p = db.P2POrders.AsNoTracking().Where(o => o.Status == P2POrderStatus.Completed && o.CompletedAt >= from && o.CompletedAt < to);
        var p2pVolume = await p2p.SumAsync(o => (decimal?)o.FiatAmount, ct) ?? 0m;
        var p2pCount = await p2p.CountAsync(ct);
        var fiatIn = await db.FiatDeposits.AsNoTracking().Where(d => d.Status == FiatDepositStatus.Succeeded && d.CompletedAt >= from && d.CompletedAt < to).SumAsync(d => (decimal?)d.Amount, ct) ?? 0m;
        var fiatOut = await db.FiatWithdrawals.AsNoTracking().Where(w => w.Status == FiatWithdrawalStatus.Succeeded && w.CompletedAt >= from && w.CompletedAt < to).SumAsync(w => (decimal?)w.Amount, ct) ?? 0m;
        var cryptoIn = await db.CryptoDeposits.AsNoTracking().Where(d => d.Status == CryptoDepositStatus.Credited && d.CreditedAt >= from && d.CreditedAt < to).SumAsync(d => (decimal?)d.NgnValue, ct) ?? 0m;
        var cryptoOut = await db.CryptoWithdrawals.AsNoTracking()
            .Where(w => (w.Status == CryptoWithdrawalStatus.Broadcast || w.Status == CryptoWithdrawalStatus.Confirmed) && w.BroadcastAt >= from && w.BroadcastAt < to)
            .SumAsync(w => (decimal?)w.NgnValue, ct) ?? 0m;

        var fees = await db.Postings.AsNoTracking()
            .Where(p => p.SystemCode == SystemAccounts.Fees && p.CreatedAt >= from && p.CreatedAt < to)
            .GroupBy(p => p.Asset)
            .Select(g => new { Asset = g.Key, Total = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.Asset, x => x.Total, ct);

        decimal feesNgn = 0;
        foreach (var (asset, amount) in fees)
        {
            try
            {
                feesNgn += await prices.ToNgnAsync(asset, amount, ct);
            }
            catch (Common.AppException)
            {
            }
        }

        return new AnalyticsOverview(from, to, totalUsers, newUsers, verified, tradeCount, tradeVolume, p2pVolume, p2pCount, fiatIn, fiatOut, cryptoIn, cryptoOut, fees, feesNgn);
    }

    public async Task<QueueCounts> QueuesAsync(CancellationToken ct = default) => new(
        await db.KycSubmissions.CountAsync(s => s.Status == KycSubmissionStatus.Pending, ct),
        await db.CryptoWithdrawals.CountAsync(w => w.Status == CryptoWithdrawalStatus.PendingReview, ct) +
        await db.FiatWithdrawals.CountAsync(w => w.Status == FiatWithdrawalStatus.PendingReview, ct),
        await db.CryptoWithdrawals.CountAsync(w => w.Status == CryptoWithdrawalStatus.NeedsAttention, ct) +
        await db.FiatWithdrawals.CountAsync(w => w.Status == FiatWithdrawalStatus.NeedsAttention, ct),
        await db.AmlAlerts.CountAsync(a => a.Status == AmlAlertStatus.Open, ct),
        await db.P2PDisputes.CountAsync(d => d.Status == P2PDisputeStatus.Open, ct));

    public async Task<IReadOnlyList<TimePoint>> TimeSeriesAsync(string metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        const string Bucket = "(date_trunc('day', {0} AT TIME ZONE @tz))::date";
        var sql = metric switch
        {
            "trade_volume" => $"SELECT {string.Format(Bucket, "created_at")} AS d, SUM(ngn_value) FROM trade_orders WHERE created_at >= @from AND created_at < @to GROUP BY 1",
            "p2p_volume" => $"SELECT {string.Format(Bucket, "completed_at")} AS d, SUM(fiat_amount) FROM p2p_orders WHERE status = 'Completed' AND completed_at >= @from AND completed_at < @to GROUP BY 1",
            "signups" => $"SELECT {string.Format(Bucket, "created_at")} AS d, COUNT(*) FROM users WHERE created_at >= @from AND created_at < @to GROUP BY 1",
            "fiat_deposits" => $"SELECT {string.Format(Bucket, "completed_at")} AS d, SUM(amount) FROM fiat_deposits WHERE status = 'Succeeded' AND completed_at >= @from AND completed_at < @to GROUP BY 1",
            "fiat_withdrawals" => $"SELECT {string.Format(Bucket, "completed_at")} AS d, SUM(amount) FROM fiat_withdrawals WHERE status = 'Succeeded' AND completed_at >= @from AND completed_at < @to GROUP BY 1",
            "crypto_deposits" => $"SELECT {string.Format(Bucket, "credited_at")} AS d, SUM(ngn_value) FROM crypto_deposits WHERE status = 'Credited' AND credited_at >= @from AND credited_at < @to GROUP BY 1",
            "fees_ngn" => $"SELECT {string.Format(Bucket, "created_at")} AS d, SUM(amount) FROM postings WHERE system_code = 'fees' AND asset = 'NGN' AND created_at >= @from AND created_at < @to GROUP BY 1",
            _ => throw Common.AppException.Validation($"Unknown metric. Use one of: {string.Join(", ", Metrics)}."),
        };

        var tz = reporting.Value.TimeZone;
        var rows = new Dictionary<DateOnly, decimal>();
        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            opened = true;
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("tz", tz));
            command.Parameters.Add(new NpgsqlParameter("from", from));
            command.Parameters.Add(new NpgsqlParameter("to", to));
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var date = DateOnly.FromDateTime(reader.GetDateTime(0));
                rows[date] = reader.IsDBNull(1) ? 0 : Convert.ToDecimal(reader.GetValue(1));
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        var end = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to.AddTicks(-1), zone).DateTime);
        var series = new List<TimePoint>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            series.Add(new TimePoint(day, rows.GetValueOrDefault(day)));
        }

        return series;
    }
}
