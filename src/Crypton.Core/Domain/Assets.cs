namespace Crypton.Core.Domain;

public static class AssetCodes
{
    public const string BTC = "BTC";
    public const string ETH = "ETH";
    public const string USDT = "USDT";
    public const string NGN = "NGN";

    public static readonly string[] Crypto = [BTC, ETH, USDT];
    public static readonly string[] All = [BTC, ETH, USDT, NGN];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);

    public static bool IsCrypto(string? code) => code is not null && Crypto.Contains(code);
}

public static class Networks
{
    public const string Bitcoin = "bitcoin";
    public const string Ethereum = "ethereum";

    public static string ForAsset(string asset) => asset switch
    {
        AssetCodes.BTC => Bitcoin,
        AssetCodes.ETH or AssetCodes.USDT => Ethereum,
        _ => throw new ArgumentOutOfRangeException(nameof(asset), asset, "Asset has no blockchain network."),
    };
}

public class Asset
{
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public bool IsFiat { get; set; }

    /// <summary>Decimal places used on the ledger and in the UI.</summary>
    public int Precision { get; set; }

    /// <summary>bitcoin | ethereum; null for fiat.</summary>
    public string? Network { get; set; }

    /// <summary>Decimals of the on-chain base unit (8 for BTC, 18 for ETH, 6 for USDT).</summary>
    public int ChainDecimals { get; set; }

    public int RequiredConfirmations { get; set; }

    public decimal MinDeposit { get; set; }

    public decimal MinWithdrawal { get; set; }

    /// <summary>Flat platform fee charged per withdrawal, in the asset itself.</summary>
    public decimal WithdrawalFee { get; set; }

    public bool DepositsEnabled { get; set; } = true;

    public bool WithdrawalsEnabled { get; set; } = true;

    public bool TradingEnabled { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
