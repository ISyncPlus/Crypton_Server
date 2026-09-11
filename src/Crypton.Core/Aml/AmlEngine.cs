using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Crypton.Core.Aml;

public sealed record AmlFinding(string RuleCode, AmlAction Action, int Severity, string Summary, object? Details = null);

public sealed record AmlDecision(IReadOnlyList<AmlFinding> Findings)
{
    public static readonly AmlDecision Clear = new([]);

    public bool Blocked => Findings.Any(f => f.Action == AmlAction.Block);

    public bool NeedsReview => Findings.Any(f => f.Action == AmlAction.Review);

    public string? Summary => Findings.Count == 0 ? null : Truncate(string.Join("; ", Findings.Select(f => f.Summary)), 1000);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed record WithdrawalRiskContext(
    Guid UserId,
    string Asset,
    bool IsFiat,
    string? Network,
    string? Address,
    decimal Amount,
    decimal NgnValue,
    string? DeviceHash);

/// <summary>Screens addresses against sanctions data. Return null when the result is unknown.</summary>
public interface IAddressScreeningProvider
{
    string Name { get; }

    Task<bool?> IsSanctionedAsync(string network, string address, CancellationToken ct);
}

public sealed class NoAddressScreening : IAddressScreeningProvider
{
    public string Name => "none";

    public Task<bool?> IsSanctionedAsync(string network, string address, CancellationToken ct) => Task.FromResult<bool?>(null);
}

/// <summary>Configurable transaction-monitoring rules. Produces findings; callers decide how to act and persist alerts.</summary>
public sealed class AmlEngine(
    CryptonDbContext db,
    SettingsService settings,
    IEnumerable<IAddressScreeningProvider> screeners,
    TimeProvider clock,
    ILogger<AmlEngine> logger)
{
    public async Task<AmlDecision> EvaluateWithdrawalAsync(WithdrawalRiskContext ctx, CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync<AmlSettings>(ct);
        var now = clock.GetUtcNow();
        var findings = new List<AmlFinding>();

        if (!ctx.IsFiat && ctx.Network is not null && ctx.Address is not null)
        {
            if (cfg.Rule(AmlRuleCodes.BlockedAddress) is { } blockedRule)
            {
                var normalized = NormalizeForLookup(ctx.Network, ctx.Address);
                var blocked = await db.BlockedAddresses.AsNoTracking()
                    .FirstOrDefaultAsync(b => b.Network == ctx.Network && b.Address == normalized, ct);
                if (blocked is not null)
                {
                    findings.Add(new AmlFinding(blockedRule.Code, blockedRule.Action, 100, $"Destination address is on the block list ({blocked.Reason}).",
                        new { ctx.Address, blocked.Reason, blocked.Source }));
                }
            }

            if (cfg.Rule(AmlRuleCodes.SanctionedAddress) is { } sanctionRule)
            {
                foreach (var screener in screeners)
                {
                    try
                    {
                        if (await screener.IsSanctionedAsync(ctx.Network, ctx.Address, ct) == true)
                        {
                            findings.Add(new AmlFinding(sanctionRule.Code, sanctionRule.Action, 100, $"Destination address is sanctioned ({screener.Name}).",
                                new { ctx.Address, provider = screener.Name }));
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Address screening via {Provider} failed", screener.Name);
                    }
                }
            }

            if (cfg.Rule(AmlRuleCodes.FirstWithdrawalToNewAddress) is { } newAddressRule && ctx.NgnValue >= (newAddressRule.ThresholdNgn ?? 0))
            {
                var usedBefore = await db.CryptoWithdrawals.AsNoTracking().AnyAsync(w =>
                    w.UserId == ctx.UserId && w.ToAddress == ctx.Address &&
                    (w.Status == CryptoWithdrawalStatus.Broadcast || w.Status == CryptoWithdrawalStatus.Confirmed), ct);
                if (!usedBefore)
                {
                    findings.Add(new AmlFinding(newAddressRule.Code, newAddressRule.Action, 30, $"First withdrawal of NGN {ctx.NgnValue:N0} to a new address.",
                        new { ctx.Address, ctx.NgnValue }));
                }
            }
        }

        if (cfg.Rule(AmlRuleCodes.LargeWithdrawal) is { ThresholdNgn: { } largeThreshold } largeRule && ctx.NgnValue >= largeThreshold)
        {
            findings.Add(new AmlFinding(largeRule.Code, largeRule.Action, 60, $"Large withdrawal of NGN {ctx.NgnValue:N0} (threshold NGN {largeThreshold:N0}).",
                new { ctx.NgnValue, threshold = largeThreshold }));
        }

        if (cfg.Rule(AmlRuleCodes.WithdrawalVelocity) is { } velocityRule)
        {
            var since = now.AddMinutes(-(velocityRule.WindowMinutes ?? 1_440));
            var crypto = await db.CryptoWithdrawals.AsNoTracking()
                .Where(w => w.UserId == ctx.UserId && w.CreatedAt >= since &&
                            w.Status != CryptoWithdrawalStatus.Rejected && w.Status != CryptoWithdrawalStatus.Cancelled && w.Status != CryptoWithdrawalStatus.Failed)
                .Select(w => w.NgnValue).ToListAsync(ct);
            var fiat = await db.FiatWithdrawals.AsNoTracking()
                .Where(w => w.UserId == ctx.UserId && w.CreatedAt >= since &&
                            w.Status != FiatWithdrawalStatus.Rejected && w.Status != FiatWithdrawalStatus.Cancelled && w.Status != FiatWithdrawalStatus.Failed)
                .Select(w => w.Amount).ToListAsync(ct);
            var count = crypto.Count + fiat.Count + 1;
            var total = crypto.Sum() + fiat.Sum() + ctx.NgnValue;
            if ((velocityRule.Count is { } maxCount && count > maxCount) || (velocityRule.ThresholdNgn is { } maxTotal && total > maxTotal))
            {
                findings.Add(new AmlFinding(velocityRule.Code, velocityRule.Action, 50, $"High withdrawal velocity: {count} withdrawals totalling NGN {total:N0} in the window.",
                    new { count, totalNgn = total, windowMinutes = velocityRule.WindowMinutes }));
            }
        }

        if (cfg.Rule(AmlRuleCodes.NewDeviceWithdrawal) is { } deviceRule && ctx.DeviceHash is not null)
        {
            var since = now.AddMinutes(-(deviceRule.WindowMinutes ?? 1_440));
            var device = await db.KnownDevices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.UserId == ctx.UserId && d.DeviceHash == ctx.DeviceHash, ct);
            var deviceCount = await db.KnownDevices.AsNoTracking().CountAsync(d => d.UserId == ctx.UserId, ct);
            if (deviceCount > 1 && (device is null || device.FirstSeenAt >= since))
            {
                findings.Add(new AmlFinding(deviceRule.Code, deviceRule.Action, 40, "Withdrawal from a recently added device.",
                    new { firstSeenAt = device?.FirstSeenAt }));
            }
        }

        if (cfg.Rule(AmlRuleCodes.RapidInOut) is { } rapidRule)
        {
            var since = now.AddMinutes(-(rapidRule.WindowMinutes ?? 60));
            var cryptoIn = await db.CryptoDeposits.AsNoTracking()
                .Where(d => d.UserId == ctx.UserId && d.Status == CryptoDepositStatus.Credited && d.CreditedAt >= since)
                .SumAsync(d => (decimal?)d.NgnValue, ct) ?? 0m;
            var fiatIn = await db.FiatDeposits.AsNoTracking()
                .Where(d => d.UserId == ctx.UserId && d.Status == FiatDepositStatus.Succeeded && d.CompletedAt >= since)
                .SumAsync(d => (decimal?)d.Amount, ct) ?? 0m;
            var deposited = cryptoIn + fiatIn;
            if (deposited >= (rapidRule.ThresholdNgn ?? 0) && deposited > 0 && ctx.NgnValue >= deposited * (rapidRule.Ratio ?? 0.8m))
            {
                findings.Add(new AmlFinding(rapidRule.Code, rapidRule.Action, 55, $"Funds withdrawn shortly after deposit (NGN {deposited:N0} in, NGN {ctx.NgnValue:N0} out).",
                    new { depositedNgn = deposited, withdrawalNgn = ctx.NgnValue, windowMinutes = rapidRule.WindowMinutes }));
            }
        }

        return new AmlDecision(findings);
    }

    public async Task<AmlDecision> EvaluateDepositAsync(Guid userId, bool isFiat, decimal ngnValue, CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync<AmlSettings>(ct);
        var findings = new List<AmlFinding>();

        if (cfg.Rule(AmlRuleCodes.LargeDeposit) is { ThresholdNgn: { } threshold } rule && ngnValue >= threshold)
        {
            findings.Add(new AmlFinding(rule.Code, rule.Action, 40, $"Large deposit of NGN {ngnValue:N0}.", new { ngnValue, isFiat }));
        }

        if (isFiat && cfg.Rule(AmlRuleCodes.Structuring) is { ThresholdNgn: { } reportThreshold } structuring)
        {
            var since = clock.GetUtcNow().AddMinutes(-(structuring.WindowMinutes ?? 1_440));
            var lower = reportThreshold * (structuring.Ratio ?? 0.8m);
            var nearThreshold = await db.FiatDeposits.AsNoTracking()
                .CountAsync(d => d.UserId == userId && d.Status == FiatDepositStatus.Succeeded && d.CompletedAt >= since &&
                                 d.Amount >= lower && d.Amount < reportThreshold, ct);
            if (nearThreshold >= (structuring.Count ?? 3) && !await HasRecentAlertAsync(userId, structuring.Code, since, ct))
            {
                findings.Add(new AmlFinding(structuring.Code, structuring.Action, 50, $"{nearThreshold} deposits just below NGN {reportThreshold:N0} in the window.",
                    new { count = nearThreshold, threshold = reportThreshold }));
            }
        }

        return new AmlDecision(findings);
    }

    public async Task<AmlDecision> EvaluateP2POrderAsync(Guid userId, decimal ngnValue, CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync<AmlSettings>(ct);
        var findings = new List<AmlFinding>();

        if (cfg.Rule(AmlRuleCodes.LargeP2POrder) is { ThresholdNgn: { } threshold } rule && ngnValue >= threshold)
        {
            findings.Add(new AmlFinding(rule.Code, rule.Action, 40, $"Large P2P order of NGN {ngnValue:N0}.", new { ngnValue }));
        }

        if (cfg.Rule(AmlRuleCodes.P2PVelocity) is { } velocity)
        {
            var since = clock.GetUtcNow().AddMinutes(-(velocity.WindowMinutes ?? 1_440));
            var count = await db.P2POrders.AsNoTracking().CountAsync(o => (o.BuyerId == userId || o.SellerId == userId) && o.CreatedAt >= since, ct);
            if (count + 1 > (velocity.Count ?? 20) && !await HasRecentAlertAsync(userId, velocity.Code, since, ct))
            {
                findings.Add(new AmlFinding(velocity.Code, velocity.Action, 35, $"{count + 1} P2P orders in the window.", new { count = count + 1 }));
            }
        }

        return new AmlDecision(findings);
    }

    public async Task<AmlDecision> EvaluateFailedLoginsAsync(Guid userId, CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync<AmlSettings>(ct);
        if (cfg.Rule(AmlRuleCodes.FailedLoginBurst) is not { } rule)
        {
            return AmlDecision.Clear;
        }

        var since = clock.GetUtcNow().AddMinutes(-(rule.WindowMinutes ?? 60));
        var failures = await db.AuditLogs.AsNoTracking()
            .CountAsync(a => a.UserId == userId && a.Action == Audit.AuditActions.LoginFailed && a.CreatedAt >= since, ct);
        if (failures >= (rule.Count ?? 10) && !await HasRecentAlertAsync(userId, rule.Code, since, ct))
        {
            return new AmlDecision([new AmlFinding(rule.Code, rule.Action, 30, $"{failures} failed sign-in attempts in {rule.WindowMinutes} minutes.", new { failures })]);
        }

        return AmlDecision.Clear;
    }

    /// <summary>Adds alerts for the decision's findings to the unit of work. Caller saves.</summary>
    public void QueueAlerts(AmlDecision decision, Guid userId, AmlSubjectType subjectType, Guid? subjectId)
    {
        var now = clock.GetUtcNow();
        foreach (var finding in decision.Findings)
        {
            db.AmlAlerts.Add(new AmlAlert
            {
                Id = Ids.New(),
                UserId = userId,
                RuleCode = finding.RuleCode,
                Action = finding.Action,
                Severity = finding.Severity,
                SubjectType = subjectType,
                SubjectId = subjectId,
                Summary = finding.Summary.Length > 500 ? finding.Summary[..500] : finding.Summary,
                Details = finding.Details is null ? null : Json.Serialize(finding.Details),
                Status = AmlAlertStatus.Open,
                CreatedAt = now,
            });
        }
    }

    public static string NormalizeForLookup(string network, string address) =>
        network == Networks.Ethereum ? address.Trim().ToLowerInvariant() : address.Trim();

    private Task<bool> HasRecentAlertAsync(Guid userId, string ruleCode, DateTimeOffset since, CancellationToken ct) =>
        db.AmlAlerts.AsNoTracking().AnyAsync(a => a.UserId == userId && a.RuleCode == ruleCode && a.CreatedAt >= since, ct);
}
