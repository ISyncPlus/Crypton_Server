namespace Crypton.Integrations.Blockchain;

public sealed class BitcoinOptions
{
    public const string Section = "Blockchain:Bitcoin";

    /// <summary>mainnet | testnet4 | testnet | signet | regtest</summary>
    public string Network { get; set; } = "testnet4";

    /// <summary>Esplora-compatible REST API (mempool.space or Blockstream). Must end with /api.</summary>
    public string ApiBaseUrl { get; set; } = "https://mempool.space/testnet4/api";

    /// <summary>Account-level extended PUBLIC key (BIP84 m/84'/coin'/0'). Deposit addresses are derived from it; keep the private key offline.</summary>
    public string AccountXpub { get; set; } = "";

    /// <summary>Hot wallet private key (WIF). Only this key's balance is exposed to withdrawals. Store in a secret manager.</summary>
    public string HotWalletWif { get; set; } = "";

    /// <summary>fastest | halfHour | hour | economy</summary>
    public string FeeTarget { get; set; } = "halfHour";

    public decimal MaxFeeRateSatPerVb { get; set; } = 150;

    public bool SpendUnconfirmedChange { get; set; } = true;
}

public sealed class EthereumOptions
{
    public const string Section = "Blockchain:Ethereum";

    /// <summary>JSON-RPC endpoint (Alchemy, Infura, QuickNode, or your own node).</summary>
    public string RpcUrl { get; set; } = "";

    /// <summary>1 = mainnet, 11155111 = Sepolia.</summary>
    public long ChainId { get; set; } = 11155111;

    /// <summary>Account-level extended PUBLIC key (BIP44 m/44'/60'/0').</summary>
    public string AccountXpub { get; set; } = "";

    /// <summary>Hot wallet private key (hex). Store in a secret manager.</summary>
    public string HotWalletPrivateKey { get; set; } = "";

    /// <summary>USDT (or test token) ERC-20 contract address on this chain.</summary>
    public string UsdtContract { get; set; } = "";

    public int UsdtDecimals { get; set; } = 6;

    public int MaxBlocksPerScan { get; set; } = 20;

    /// <summary>First block to scan when no cursor exists yet. Defaults to a few blocks behind the tip.</summary>
    public long? StartBlock { get; set; }

    public decimal PriorityFeeGwei { get; set; } = 1.5m;

    public decimal MaxFeeGwei { get; set; } = 200m;
}

public sealed class SanctionsOracleOptions
{
    public const string Section = "Compliance:SanctionsOracle";

    public bool Enabled { get; set; }

    /// <summary>Ethereum MAINNET RPC URL (the Chainalysis oracle only exists on mainnet).</summary>
    public string RpcUrl { get; set; } = "";

    public string OracleAddress { get; set; } = "0x40C57923924B5c5c5455c48D93317139ADDaC8fb";
}
