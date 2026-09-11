using NBitcoin;
using NBitcoin.DataEncoders;
using Nethereum.Util;

namespace Crypton.Integrations.Blockchain;

/// <summary>Watch-only HD address derivation for Bitcoin (BIP84 native SegWit) and Ethereum (BIP44).</summary>
public static class HdKeys
{
    private static readonly byte[] XpubVersion = [0x04, 0x88, 0xB2, 0x1E];
    private static readonly byte[] TpubVersion = [0x04, 0x35, 0x87, 0xCF];

    // SLIP-0132 versions we accept and normalize: ypub/zpub (mainnet), upub/vpub (testnet).
    private static readonly byte[][] MainnetVersions = [XpubVersion, [0x04, 0x9D, 0x7C, 0xB2], [0x04, 0xB2, 0x47, 0x46]];
    private static readonly byte[][] TestnetVersions = [TpubVersion, [0x04, 0x4A, 0x52, 0x62], [0x04, 0x5F, 0x1C, 0xF6]];

    public static Network BitcoinNetwork(string name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "mainnet" or "main" => Network.Main,
        "regtest" => Network.RegTest,
        // testnet3, testnet4 and signet share address encoding.
        "testnet" or "testnet3" or "testnet4" or "signet" or "" => Network.TestNet,
        _ => throw new ArgumentException($"Unknown Bitcoin network '{name}'."),
    };

    public static bool IsMainnet(Network network) => network.ChainName == ChainName.Mainnet;

    /// <summary>Parses an account-level extended public key (xpub/ypub/zpub or tpub/upub/vpub).</summary>
    public static ExtPubKey ParseAccountXpub(string value, Network network)
    {
        byte[] data;
        try
        {
            data = Encoders.Base58Check.DecodeData((value ?? "").Trim());
        }
        catch (FormatException ex)
        {
            throw new FormatException("The extended public key is not valid base58.", ex);
        }

        if (data.Length != 78)
        {
            throw new FormatException("The extended public key has an unexpected length.");
        }

        var mainnet = IsMainnet(network);
        var allowed = mainnet ? MainnetVersions : TestnetVersions;
        var version = data.AsSpan(0, 4).ToArray();
        if (!allowed.Any(v => v.AsSpan().SequenceEqual(version)))
        {
            throw new FormatException(mainnet
                ? "Expected a mainnet key (xpub, ypub or zpub)."
                : "Expected a testnet key (tpub, upub or vpub).");
        }

        (mainnet ? XpubVersion : TpubVersion).CopyTo(data, 0);
        return ExtPubKey.Parse(Encoders.Base58Check.EncodeData(data), network);
    }

    public static string BitcoinAddress(ExtPubKey accountXpub, int index, Network network) =>
        accountXpub.Derive(0).Derive((uint)index).PubKey.GetAddress(ScriptPubKeyType.Segwit, network).ToString();

    /// <summary>Parses an Ethereum account xpub (m/44'/60'/0'); Ethereum keys always use mainnet xpub encoding.</summary>
    public static ExtPubKey ParseEthereumAccountXpub(string value) => ParseAccountXpub(value, Network.Main);

    public static string EthereumAddress(ExtPubKey accountXpub, int index) => EthereumAddress(accountXpub.Derive(0).Derive((uint)index).PubKey);

    public static string EthereumAddress(PubKey pubKey)
    {
        var uncompressed = pubKey.Decompress().ToBytes();
        var hash = Sha3Keccack.Current.CalculateHash(uncompressed.AsSpan(1).ToArray());
        var address = "0x" + Convert.ToHexStringLower(hash.AsSpan(hash.Length - 20).ToArray());
        return AddressUtil.Current.ConvertToChecksumAddress(address);
    }

    public static bool IsValidEthereumAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || !AddressUtil.Current.IsValidEthereumAddressHexFormat(address))
        {
            return false;
        }

        var body = address[2..];
        var singleCase = body == body.ToLowerInvariant() || body == body.ToUpperInvariant();
        return singleCase || AddressUtil.Current.IsChecksumAddress(address);
    }

    public static string NormalizeEthereumAddress(string address) =>
        AddressUtil.Current.ConvertToChecksumAddress(address.Trim().ToLowerInvariant());

    public static bool IsValidBitcoinAddress(string address, Network network)
    {
        try
        {
            NBitcoin.BitcoinAddress.Create((address ?? "").Trim(), network);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string NormalizeBitcoinAddress(string address, Network network) =>
        NBitcoin.BitcoinAddress.Create(address.Trim(), network).ToString();
}
