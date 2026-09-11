using System.Security.Cryptography;
using Crypton.Integrations.Blockchain;
using NBitcoin;
using Nethereum.Hex.HexConvertors.Extensions;

// Crypton key tool: generates the placeholder values Crypton needs for live blockchain mode and secrets.
// Usage:
//   dotnet run --project tools/Crypton.KeyTool -- generate --network testnet
//   dotnet run --project tools/Crypton.KeyTool -- derive --btc-xpub <tpub/vpub/xpub/zpub> --network testnet --count 5
//   dotnet run --project tools/Crypton.KeyTool -- derive --eth-xpub <xpub> --count 5
//   dotnet run --project tools/Crypton.KeyTool -- secrets

var command = args.FirstOrDefault()?.ToLowerInvariant();
string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

switch (command)
{
    case "generate":
        Generate(Option("--network") ?? "testnet");
        break;
    case "derive":
        Derive(Option("--btc-xpub"), Option("--eth-xpub"), Option("--network") ?? "testnet", int.TryParse(Option("--count"), out var count) ? count : 5);
        break;
    case "secrets":
        Secrets();
        break;
    default:
        Console.WriteLine("""
            Crypton key tool

              generate --network testnet|mainnet   Create deposit (cold) seed, account xpubs and hot wallet keys
              derive --btc-xpub KEY [--network N]  Show the first deposit addresses for a Bitcoin account xpub
              derive --eth-xpub KEY                Show the first deposit addresses for an Ethereum account xpub
              secrets                              Generate JWT signing key and KYC ID hash key
            """);
        break;
}

static void Generate(string networkName)
{
    var mainnet = networkName.Equals("mainnet", StringComparison.OrdinalIgnoreCase);
    var btcNetwork = mainnet ? Network.Main : Network.TestNet;
    var coinType = mainnet ? 0 : 1;

    var mnemonic = new Mnemonic(Wordlist.English, WordCount.TwentyFour);
    var root = mnemonic.DeriveExtKey();
    var btcAccount = root.Derive(new KeyPath($"m/84'/{coinType}'/0'")).Neuter();
    var ethAccount = root.Derive(new KeyPath("m/44'/60'/0'")).Neuter();

    var btcHot = new Key();
    var ethHot = new Key();

    Console.WriteLine($"""
        ============================================================================
         CRYPTON KEYS ({(mainnet ? "MAINNET" : "TESTNET")})
        ============================================================================

        1) DEPOSIT (COLD) SEED - write these 24 words on paper and store them OFFLINE.
           Never put them on the server. They control every customer deposit address.

           {mnemonic}

        2) ACCOUNT EXTENDED PUBLIC KEYS - safe to put on the server (watch-only).

           Blockchain__Bitcoin__AccountXpub  = {btcAccount.ToString(btcNetwork)}
           Blockchain__Ethereum__AccountXpub = {ethAccount.ToString(Network.Main)}

           First Bitcoin deposit address  : {HdKeys.BitcoinAddress(btcAccount, 0, btcNetwork)}
           First Ethereum deposit address : {HdKeys.EthereumAddress(ethAccount, 0)}

        3) HOT WALLETS - store in your secret manager. Fund them with only what you need for withdrawals.

           Blockchain__Bitcoin__HotWalletWif          = {btcHot.GetWif(btcNetwork)}
           Bitcoin hot wallet address (fund this)     : {btcHot.PubKey.GetAddress(ScriptPubKeyType.Segwit, btcNetwork)}

           Blockchain__Ethereum__HotWalletPrivateKey  = {ethHot.ToBytes().ToHex(true)}
           Ethereum hot wallet address (fund this)    : {HdKeys.EthereumAddress(ethHot.PubKey)}

        To sweep deposits later, import the 24-word seed into a wallet that supports
        BIP84 (Bitcoin, e.g. Sparrow/Electrum) and BIP44 m/44'/60'/0'/0/i (Ethereum, e.g. MetaMask).
        ============================================================================
        """);
}

static void Derive(string? btcXpub, string? ethXpub, string networkName, int count)
{
    count = Math.Clamp(count, 1, 100);
    if (btcXpub is not null)
    {
        var network = HdKeys.BitcoinNetwork(networkName);
        var account = HdKeys.ParseAccountXpub(btcXpub, network);
        for (var i = 0; i < count; i++)
        {
            Console.WriteLine($"bitcoin  {i,3}  {HdKeys.BitcoinAddress(account, i, network)}");
        }
    }

    if (ethXpub is not null)
    {
        var account = HdKeys.ParseEthereumAccountXpub(ethXpub);
        for (var i = 0; i < count; i++)
        {
            Console.WriteLine($"ethereum {i,3}  {HdKeys.EthereumAddress(account, i)}");
        }
    }

    if (btcXpub is null && ethXpub is null)
    {
        Console.WriteLine("Pass --btc-xpub and/or --eth-xpub.");
    }
}

static void Secrets()
{
    Console.WriteLine($"""
        Jwt__SigningKey = {Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))}
        Kyc__IdHashKey  = {Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}
        """);
}
