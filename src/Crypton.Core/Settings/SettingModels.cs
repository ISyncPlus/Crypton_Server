using Crypton.Core.Domain;

namespace Crypton.Core.Settings;

public interface IValidatableSetting
{
    IEnumerable<string> Validate();
}

public sealed record TradingSettings : IValidatableSetting
{
    public int BuyFeeBps { get; init; } = 50;

    public int SellFeeBps { get; init; } = 50;

    public int SwapFeeBps { get; init; } = 50;

    /// <summary>House spread applied to the market mid price (buy above, sell below).</summary>
    public int SpreadBps { get; init; } = 0;

    public int QuoteTtlSeconds { get; init; } = 20;

    public int MaxPriceAgeSeconds { get; init; } = 300;

    public decimal MinOrderNgn { get; init; } = 1_000m;

    public decimal MaxOrderNgn { get; init; } = 50_000_000m;

    public IEnumerable<string> Validate()
    {
        foreach (var (name, bps) in new[] { ("buyFeeBps", BuyFeeBps), ("sellFeeBps", SellFeeBps), ("swapFeeBps", SwapFeeBps), ("spreadBps", SpreadBps) })
        {
            if (bps is < 0 or > 2_000)
            {
                yield return $"{name} must be between 0 and 2000.";
            }
        }

        if (QuoteTtlSeconds is < 5 or > 300)
        {
            yield return "quoteTtlSeconds must be between 5 and 300.";
        }

        if (MaxPriceAgeSeconds is < 10 or > 3_600)
        {
            yield return "maxPriceAgeSeconds must be between 10 and 3600.";
        }

        if (MinOrderNgn < 0 || MaxOrderNgn <= MinOrderNgn)
        {
            yield return "maxOrderNgn must be greater than minOrderNgn, and both must be positive.";
        }
    }
}

public sealed record WithdrawalSettings : IValidatableSetting
{
    public bool RequireTwoFactor { get; init; } = true;

    /// <summary>Hours withdrawals stay locked after a password reset or 2FA change.</summary>
    public int SecurityLockHours { get; init; } = 24;

    /// <summary>Withdrawals above this NGN value always go to manual review.</summary>
    public decimal ManualReviewAboveNgn { get; init; } = 1_000_000m;

    public decimal FiatWithdrawalFeeNgn { get; init; } = 50m;

    public decimal MinFiatWithdrawalNgn { get; init; } = 1_000m;

    public decimal MaxFiatWithdrawalNgn { get; init; } = 20_000_000m;

    public IEnumerable<string> Validate()
    {
        if (SecurityLockHours is < 0 or > 720)
        {
            yield return "securityLockHours must be between 0 and 720.";
        }

        if (ManualReviewAboveNgn < 0)
        {
            yield return "manualReviewAboveNgn cannot be negative.";
        }

        if (FiatWithdrawalFeeNgn < 0 || MinFiatWithdrawalNgn <= FiatWithdrawalFeeNgn)
        {
            yield return "minFiatWithdrawalNgn must be greater than fiatWithdrawalFeeNgn.";
        }

        if (MaxFiatWithdrawalNgn <= MinFiatWithdrawalNgn)
        {
            yield return "maxFiatWithdrawalNgn must be greater than minFiatWithdrawalNgn.";
        }
    }
}

public sealed record FiatSettings : IValidatableSetting
{
    /// <summary>Deposit fee charged to the user, in basis points of the amount.</summary>
    public int DepositFeeBps { get; init; } = 0;

    public decimal DepositFeeCapNgn { get; init; } = 2_000m;

    public decimal MinDepositNgn { get; init; } = 1_000m;

    public decimal MaxDepositNgn { get; init; } = 10_000_000m;

    public IEnumerable<string> Validate()
    {
        if (DepositFeeBps is < 0 or > 1_000)
        {
            yield return "depositFeeBps must be between 0 and 1000.";
        }

        if (DepositFeeCapNgn < 0)
        {
            yield return "depositFeeCapNgn cannot be negative.";
        }

        if (MinDepositNgn < 100 || MaxDepositNgn <= MinDepositNgn)
        {
            yield return "minDepositNgn must be at least 100 and below maxDepositNgn.";
        }
    }
}

public sealed record P2PSettings : IValidatableSetting
{
    public int MakerFeeBps { get; init; } = 25;

    public int[] PaymentWindowsMinutes { get; init; } = [15, 30, 45, 60];

    /// <summary>Minutes after "paid" before either side can open a dispute.</summary>
    public int DisputeAfterMinutes { get; init; } = 10;

    public int MaxOpenOrdersPerUser { get; init; } = 5;

    public int MinKycTier { get; init; } = 1;

    /// <summary>Allowed floating price margin range (+/-) in basis points.</summary>
    public int MaxFloatingMarginBps { get; init; } = 2_000;

    public decimal MinOrderFiat { get; init; } = 1_000m;

    public IEnumerable<string> Validate()
    {
        if (MakerFeeBps is < 0 or > 1_000)
        {
            yield return "makerFeeBps must be between 0 and 1000.";
        }

        if (PaymentWindowsMinutes.Length == 0 || PaymentWindowsMinutes.Any(m => m is < 5 or > 180))
        {
            yield return "paymentWindowsMinutes must contain values between 5 and 180.";
        }

        if (DisputeAfterMinutes is < 0 or > 1_440)
        {
            yield return "disputeAfterMinutes must be between 0 and 1440.";
        }

        if (MaxOpenOrdersPerUser is < 1 or > 100)
        {
            yield return "maxOpenOrdersPerUser must be between 1 and 100.";
        }

        if (MinKycTier is < 0 or > 2)
        {
            yield return "minKycTier must be 0, 1 or 2.";
        }

        if (MaxFloatingMarginBps is < 0 or > 5_000)
        {
            yield return "maxFloatingMarginBps must be between 0 and 5000.";
        }
    }
}

public sealed record KycTierLimits
{
    public int Tier { get; init; }

    public string Name { get; init; } = "";

    public bool FiatEnabled { get; init; }

    public bool P2PEnabled { get; init; }

    public decimal DailyFiatDepositNgn { get; init; }

    public decimal DailyFiatWithdrawalNgn { get; init; }

    public decimal DailyCryptoWithdrawalNgn { get; init; }

    public decimal DailyTradeNgn { get; init; }
}

public sealed record KycLimitSettings : IValidatableSetting
{
    public List<KycTierLimits> Tiers { get; init; } =
    [
        new() { Tier = 0, Name = "Unverified", FiatEnabled = false, P2PEnabled = false, DailyFiatDepositNgn = 0, DailyFiatWithdrawalNgn = 0, DailyCryptoWithdrawalNgn = 0, DailyTradeNgn = 250_000m },
        new() { Tier = 1, Name = "Verified", FiatEnabled = true, P2PEnabled = true, DailyFiatDepositNgn = 2_000_000m, DailyFiatWithdrawalNgn = 2_000_000m, DailyCryptoWithdrawalNgn = 5_000_000m, DailyTradeNgn = 10_000_000m },
        new() { Tier = 2, Name = "Advanced", FiatEnabled = true, P2PEnabled = true, DailyFiatDepositNgn = 50_000_000m, DailyFiatWithdrawalNgn = 50_000_000m, DailyCryptoWithdrawalNgn = 100_000_000m, DailyTradeNgn = 200_000_000m },
    ];

    public KycTierLimits ForTier(int tier) =>
        Tiers.FirstOrDefault(t => t.Tier == tier) ?? Tiers.OrderBy(t => t.Tier).First();

    public IEnumerable<string> Validate()
    {
        var tiers = Tiers.Select(t => t.Tier).OrderBy(t => t).ToArray();
        if (!tiers.SequenceEqual([0, 1, 2]))
        {
            yield return "tiers must define exactly tiers 0, 1 and 2.";
        }

        if (Tiers.Any(t => t.DailyFiatDepositNgn < 0 || t.DailyFiatWithdrawalNgn < 0 || t.DailyCryptoWithdrawalNgn < 0 || t.DailyTradeNgn < 0))
        {
            yield return "limits cannot be negative.";
        }
    }
}

public sealed record AmlRuleConfig
{
    public string Code { get; init; } = "";

    public bool Enabled { get; init; } = true;

    public AmlAction Action { get; init; } = AmlAction.Flag;

    public decimal? ThresholdNgn { get; init; }

    public int? Count { get; init; }

    public int? WindowMinutes { get; init; }

    public decimal? Ratio { get; init; }
}

public static class AmlRuleCodes
{
    public const string LargeWithdrawal = "large_withdrawal";
    public const string WithdrawalVelocity = "withdrawal_velocity";
    public const string NewDeviceWithdrawal = "new_device_withdrawal";
    public const string RapidInOut = "rapid_in_out";
    public const string Structuring = "structuring";
    public const string LargeDeposit = "large_deposit";
    public const string LargeP2POrder = "large_p2p_order";
    public const string P2PVelocity = "p2p_velocity";
    public const string FailedLoginBurst = "failed_login_burst";
    public const string BlockedAddress = "blocked_address";
    public const string SanctionedAddress = "sanctioned_address";
    public const string FirstWithdrawalToNewAddress = "new_withdrawal_address";
}

public sealed record AmlSettings : IValidatableSetting
{
    public List<AmlRuleConfig> Rules { get; init; } = Defaults();

    public static List<AmlRuleConfig> Defaults() =>
    [
        new() { Code = AmlRuleCodes.LargeWithdrawal, Action = AmlAction.Review, ThresholdNgn = 5_000_000m },
        new() { Code = AmlRuleCodes.WithdrawalVelocity, Action = AmlAction.Review, Count = 10, ThresholdNgn = 20_000_000m, WindowMinutes = 1_440 },
        new() { Code = AmlRuleCodes.NewDeviceWithdrawal, Action = AmlAction.Review, WindowMinutes = 1_440 },
        new() { Code = AmlRuleCodes.RapidInOut, Action = AmlAction.Review, WindowMinutes = 60, Ratio = 0.8m, ThresholdNgn = 200_000m },
        new() { Code = AmlRuleCodes.FirstWithdrawalToNewAddress, Action = AmlAction.Flag, ThresholdNgn = 2_000_000m },
        new() { Code = AmlRuleCodes.Structuring, Action = AmlAction.Flag, Count = 3, ThresholdNgn = 1_000_000m, WindowMinutes = 1_440, Ratio = 0.8m },
        new() { Code = AmlRuleCodes.LargeDeposit, Action = AmlAction.Flag, ThresholdNgn = 10_000_000m },
        new() { Code = AmlRuleCodes.LargeP2POrder, Action = AmlAction.Flag, ThresholdNgn = 5_000_000m },
        new() { Code = AmlRuleCodes.P2PVelocity, Action = AmlAction.Flag, Count = 20, WindowMinutes = 1_440 },
        new() { Code = AmlRuleCodes.FailedLoginBurst, Action = AmlAction.Flag, Count = 10, WindowMinutes = 60 },
        new() { Code = AmlRuleCodes.BlockedAddress, Action = AmlAction.Block },
        new() { Code = AmlRuleCodes.SanctionedAddress, Action = AmlAction.Block },
    ];

    public AmlRuleConfig? Rule(string code)
    {
        var rule = Rules.FirstOrDefault(r => r.Code == code) ?? Defaults().FirstOrDefault(r => r.Code == code);
        return rule is { Enabled: true } ? rule : null;
    }

    public IEnumerable<string> Validate()
    {
        var known = Defaults().Select(r => r.Code).ToHashSet();
        foreach (var rule in Rules)
        {
            if (!known.Contains(rule.Code))
            {
                yield return $"Unknown AML rule '{rule.Code}'.";
            }

            if (rule.ThresholdNgn is < 0 || rule.Count is < 0 || rule.WindowMinutes is < 0 || rule.Ratio is < 0 or > 1)
            {
                yield return $"Rule '{rule.Code}' has an invalid parameter.";
            }
        }
    }
}
