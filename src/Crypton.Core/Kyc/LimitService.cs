using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Kyc;

public enum LimitKind
{
    FiatDeposit,
    FiatWithdrawal,
    CryptoWithdrawal,
    Trade,
}

public sealed record LimitUsage(LimitKind Kind, decimal DailyLimitNgn, decimal UsedNgn)
{
    public decimal RemainingNgn => Math.Max(0, DailyLimitNgn - UsedNgn);
}

public sealed class LimitService(CryptonDbContext db, SettingsService settings, TimeProvider clock)
{
    public async Task<KycTierLimits> GetTierLimitsAsync(int tier, CancellationToken ct = default) =>
        (await settings.GetAsync<KycLimitSettings>(ct)).ForTier(tier);

    public async Task<IReadOnlyList<LimitUsage>> GetUsageAsync(Guid userId, int tier, CancellationToken ct = default)
    {
        var limits = await GetTierLimitsAsync(tier, ct);
        var result = new List<LimitUsage>();
        foreach (var kind in Enum.GetValues<LimitKind>())
        {
            result.Add(new LimitUsage(kind, LimitFor(limits, kind), await UsedAsync(userId, kind, ct)));
        }

        return result;
    }

    public async Task EnsureWithinLimitAsync(Guid userId, int tier, LimitKind kind, decimal ngnValue, CancellationToken ct = default)
    {
        var limits = await GetTierLimitsAsync(tier, ct);

        if ((kind is LimitKind.FiatDeposit or LimitKind.FiatWithdrawal) && !limits.FiatEnabled)
        {
            throw new AppException(ErrorCodes.KycRequired, "Verify your identity to use naira deposits and withdrawals.", 403,
                new Dictionary<string, object?> { ["requiredTier"] = 1 });
        }

        var limit = LimitFor(limits, kind);
        if (limit <= 0)
        {
            throw new AppException(ErrorCodes.KycRequired, $"Verify your identity to unlock {Describe(kind)}.", 403,
                new Dictionary<string, object?> { ["requiredTier"] = tier + 1 });
        }

        var used = await UsedAsync(userId, kind, ct);
        if (used + ngnValue > limit)
        {
            var remaining = Math.Max(0, limit - used);
            throw new AppException(
                ErrorCodes.LimitExceeded,
                $"This exceeds your daily {Describe(kind)} limit. Remaining today: NGN {remaining:N2}.",
                403,
                new Dictionary<string, object?> { ["limitNgn"] = limit, ["usedNgn"] = used, ["remainingNgn"] = remaining, ["tier"] = tier });
        }
    }

    private static decimal LimitFor(KycTierLimits limits, LimitKind kind) => kind switch
    {
        LimitKind.FiatDeposit => limits.DailyFiatDepositNgn,
        LimitKind.FiatWithdrawal => limits.DailyFiatWithdrawalNgn,
        LimitKind.CryptoWithdrawal => limits.DailyCryptoWithdrawalNgn,
        LimitKind.Trade => limits.DailyTradeNgn,
        _ => 0,
    };

    private static string Describe(LimitKind kind) => kind switch
    {
        LimitKind.FiatDeposit => "naira deposit",
        LimitKind.FiatWithdrawal => "naira withdrawal",
        LimitKind.CryptoWithdrawal => "crypto withdrawal",
        LimitKind.Trade => "trading",
        _ => "transaction",
    };

    private async Task<decimal> UsedAsync(Guid userId, LimitKind kind, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var since = now.AddHours(-24);
        switch (kind)
        {
            case LimitKind.Trade:
                return await db.TradeOrders.AsNoTracking()
                    .Where(o => o.UserId == userId && o.CreatedAt >= since)
                    .SumAsync(o => (decimal?)o.NgnValue, ct) ?? 0m;

            case LimitKind.CryptoWithdrawal:
                return await db.CryptoWithdrawals.AsNoTracking()
                    .Where(w => w.UserId == userId && w.CreatedAt >= since &&
                                w.Status != CryptoWithdrawalStatus.Rejected &&
                                w.Status != CryptoWithdrawalStatus.Cancelled &&
                                w.Status != CryptoWithdrawalStatus.Failed)
                    .SumAsync(w => (decimal?)w.NgnValue, ct) ?? 0m;

            case LimitKind.FiatWithdrawal:
                return await db.FiatWithdrawals.AsNoTracking()
                    .Where(w => w.UserId == userId && w.CreatedAt >= since &&
                                w.Status != FiatWithdrawalStatus.Rejected &&
                                w.Status != FiatWithdrawalStatus.Cancelled &&
                                w.Status != FiatWithdrawalStatus.Failed &&
                                w.Status != FiatWithdrawalStatus.Reversed)
                    .SumAsync(w => (decimal?)w.Amount, ct) ?? 0m;

            case LimitKind.FiatDeposit:
                var pendingSince = now.AddMinutes(-30);
                return await db.FiatDeposits.AsNoTracking()
                    .Where(d => d.UserId == userId &&
                                ((d.Status == FiatDepositStatus.Succeeded && d.CompletedAt >= since) ||
                                 (d.Status == FiatDepositStatus.Initiated && d.CreatedAt >= pendingSince)))
                    .SumAsync(d => (decimal?)d.Amount, ct) ?? 0m;

            default:
                return 0m;
        }
    }
}
