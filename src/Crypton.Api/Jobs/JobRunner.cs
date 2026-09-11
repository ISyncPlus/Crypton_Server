using System.Diagnostics;
using Crypton.Core.Admin;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Fiat;
using Crypton.Core.Notifications;
using Crypton.Core.P2P;
using Crypton.Core.Pricing;
using Crypton.Core.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Crypton.Api.Jobs;

public sealed class JobsOptions
{
    public const string Section = "Jobs";

    public bool Enabled { get; set; } = true;
}

public sealed record JobDefinition(string Name, TimeSpan Interval, Func<IServiceProvider, CancellationToken, Task> Run);

public static class JobCatalog
{
    public static IReadOnlyList<JobDefinition> Build(IConfiguration configuration)
    {
        var simulated = (configuration.GetSection(BlockchainOptions.Section).Get<BlockchainOptions>() ?? new BlockchainOptions()).IsSimulated;
        var pricing = configuration.GetSection(PricingOptions.Section).Get<PricingOptions>() ?? new PricingOptions();
        var fast = simulated ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30);

        return
        [
            new("prices", TimeSpan.FromSeconds(Math.Max(10, pricing.RefreshSeconds)),
                (sp, ct) => sp.GetRequiredService<PriceService>().RefreshAsync(persistTicks: true, ct)),
            new("deposits:bitcoin", fast, (sp, ct) => sp.GetRequiredService<DepositScanner>().RunAsync(Networks.Bitcoin, ct)),
            new("deposits:ethereum", simulated ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(15), (sp, ct) => sp.GetRequiredService<DepositScanner>().RunAsync(Networks.Ethereum, ct)),
            new("withdrawals:bitcoin", simulated ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(20), (sp, ct) => sp.GetRequiredService<WithdrawalProcessor>().RunAsync(Networks.Bitcoin, ct)),
            new("withdrawals:ethereum", simulated ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(20), (sp, ct) => sp.GetRequiredService<WithdrawalProcessor>().RunAsync(Networks.Ethereum, ct)),
            new("fiat:payouts", TimeSpan.FromSeconds(simulated ? 3 : 10), (sp, ct) => sp.GetRequiredService<FiatService>().ProcessApprovedWithdrawalsAsync(ct)),
            new("fiat:reconcile", TimeSpan.FromSeconds(simulated ? 3 : 60), (sp, ct) => sp.GetRequiredService<FiatService>().ReconcileAsync(ct)),
            new("p2p:expiry", TimeSpan.FromSeconds(15), async (sp, ct) => await sp.GetRequiredService<P2POrderService>().ExpireDueAsync(ct)),
            new("email:outbox", TimeSpan.FromSeconds(5), async (sp, ct) => await sp.GetRequiredService<EmailOutboxProcessor>().ProcessBatchAsync(50, ct)),
            new("cleanup", TimeSpan.FromHours(1), CleanupAsync),
        ];
    }

    private static async Task CleanupAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<CryptonDbContext>();
        var now = sp.GetRequiredService<TimeProvider>().GetUtcNow();
        await db.Quotes.Where(q => q.ExpiresAt < now.AddDays(-2) && q.ConsumedAt == null).ExecuteDeleteAsync(ct);
        await db.RefreshTokens.Where(t => t.ExpiresAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        await db.EmailOutbox.Where(m => m.Status == EmailStatus.Sent && m.SentAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// Runs periodic jobs. Each tick takes a PostgreSQL advisory lock so that when several API instances run,
/// only one executes a given job at a time.
/// </summary>
public sealed class JobRunner(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    IOptions<JobsOptions> options,
    TimeProvider clock,
    ILogger<JobRunner> logger) : BackgroundService
{
    private readonly IReadOnlyList<JobDefinition> _jobs = JobCatalog.Build(configuration);

    public IReadOnlyList<JobDefinition> Jobs => _jobs;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Background jobs are disabled (Jobs:Enabled=false).");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        await Task.WhenAll(_jobs.Select(job => LoopAsync(job, stoppingToken)));
    }

    public async Task<bool> RunOnceAsync(string name, CancellationToken ct)
    {
        var job = _jobs.FirstOrDefault(j => j.Name == name);
        if (job is null)
        {
            return false;
        }

        await ExecuteJobAsync(job, ct);
        return true;
    }

    private async Task LoopAsync(JobDefinition job, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(job.Interval);
        do
        {
            try
            {
                await ExecuteJobAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ExecuteJobAsync(JobDefinition job, CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Default")!;
        await using var lockConnection = new NpgsqlConnection(connectionString);
        try
        {
            await lockConnection.OpenAsync(ct);
            await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", lockConnection);
            acquire.Parameters.AddWithValue("key", LockKey(job.Name));
            if (await acquire.ExecuteScalarAsync(ct) is not true)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Job {Job}: could not acquire lock", job.Name);
            return;
        }

        var started = clock.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        string? error = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await job.Run(scope.ServiceProvider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogError(ex, "Job {Job} failed after {Elapsed} ms", job.Name, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            try
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", lockConnection);
                release.Parameters.AddWithValue("key", LockKey(job.Name));
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Job {Job}: unlock failed (connection closing releases it)", job.Name);
            }
        }

        await RecordRunAsync(job.Name, started, error);
    }

    private async Task RecordRunAsync(string name, DateTimeOffset started, string? error)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CryptonDbContext>();
            var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Name == name);
            if (run is null)
            {
                run = new JobRun { Name = name };
                db.JobRuns.Add(run);
            }

            run.LastStartedAt = started;
            if (error is null)
            {
                run.LastSucceededAt = clock.GetUtcNow();
                run.ConsecutiveFailures = 0;
            }
            else
            {
                run.LastFailedAt = clock.GetUtcNow();
                run.LastError = error.Length > 2000 ? error[..2000] : error;
                run.ConsecutiveFailures++;
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Recording job run for {Job} failed", name);
        }
    }

    /// <summary>Stable 64-bit FNV-1a hash of the job name.</summary>
    internal static long LockKey(string name)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes("crypton-job:" + name))
        {
            hash ^= b;
            hash *= prime;
        }

        return unchecked((long)hash);
    }
}
