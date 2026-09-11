namespace Crypton.Integrations.Blockchain;

/// <summary>What the apps need to label a network and link to a block explorer.</summary>
public sealed record ChainNetworkInfo(string Network, string Name, bool Simulated, string? TxUrlTemplate, string? AddressUrlTemplate);

public static class ChainNetworks
{
    public static IReadOnlyList<ChainNetworkInfo> Describe(bool simulated, BitcoinOptions bitcoin, EthereumOptions ethereum)
    {
        if (simulated)
        {
            return
            [
                new ChainNetworkInfo("bitcoin", "Simulated Bitcoin", true, null, null),
                new ChainNetworkInfo("ethereum", "Simulated Ethereum", true, null, null),
            ];
        }

        var btcNetwork = (bitcoin.Network ?? "").Trim().ToLowerInvariant();
        var btcBase = Base(bitcoin.ExplorerUrl) ?? btcNetwork switch
        {
            "mainnet" or "main" => "https://mempool.space",
            "testnet4" => "https://mempool.space/testnet4",
            "testnet" or "testnet3" => "https://mempool.space/testnet",
            "signet" => "https://mempool.space/signet",
            _ => null,
        };
        var btcName = btcNetwork is "mainnet" or "main" ? "Bitcoin" : $"Bitcoin {btcNetwork}";

        var ethBase = Base(ethereum.ExplorerUrl) ?? ethereum.ChainId switch
        {
            1 => "https://etherscan.io",
            11155111 => "https://sepolia.etherscan.io",
            17000 => "https://holesky.etherscan.io",
            560048 => "https://hoodi.etherscan.io",
            _ => null,
        };
        var ethName = ethereum.ChainId switch
        {
            1 => "Ethereum",
            11155111 => "Ethereum Sepolia",
            17000 => "Ethereum Holesky",
            560048 => "Ethereum Hoodi",
            _ => $"Ethereum chain {ethereum.ChainId}",
        };

        return
        [
            new ChainNetworkInfo("bitcoin", btcName, false, Tx(btcBase), Address(btcBase)),
            new ChainNetworkInfo("ethereum", ethName, false, Tx(ethBase), Address(ethBase)),
        ];
    }

    private static string? Base(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? null : configured.Trim().TrimEnd('/');

    private static string? Tx(string? root) => root is null ? null : root + "/tx/{txid}";

    private static string? Address(string? root) => root is null ? null : root + "/address/{address}";
}
