using System.Security.Cryptography;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Wallets;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace Crypton.Integrations.Blockchain;

/// <summary>
/// A deterministic fake blockchain backed by the database, for local development, demos and automated tests.
/// Addresses are real-format (Bitcoin testnet / Ethereum) but derived from a publicly known test seed:
/// never send real funds to them.
/// </summary>
public sealed class SimulatedChainGateway : IChainGateway
{
    public const string DevMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    private static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Lazy<(ExtPubKey Btc, ExtPubKey Eth)> Keys = new(() =>
    {
        var root = new Mnemonic(DevMnemonic, Wordlist.English).DeriveExtKey();
        return (root.Derive(new KeyPath("m/84'/1'/0'")).Neuter(), root.Derive(new KeyPath("m/44'/60'/0'")).Neuter());
    });

    private readonly CryptonDbContext _db;
    private readonly TimeProvider _clock;

    public SimulatedChainGateway(string network, CryptonDbContext db, TimeProvider clock)
    {
        if (network is not (Networks.Bitcoin or Networks.Ethereum))
        {
            throw new ArgumentOutOfRangeException(nameof(network));
        }

        Network = network;
        _db = db;
        _clock = clock;
    }

    public string Network { get; }

    public bool IsSimulated => true;

    public bool IsConfigured => true;

    public bool PollsAddresses => Network == Networks.Bitcoin;

    public IReadOnlyList<string> Assets => Network == Networks.Bitcoin ? [AssetCodes.BTC] : [AssetCodes.ETH, AssetCodes.USDT];

    public string? HotWalletAddress => DeriveDepositAddress(999_999);

    public TimeSpan BlockTime => Network == Networks.Bitcoin ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(5);

    public string DeriveDepositAddress(int index) => Network == Networks.Bitcoin
        ? HdKeys.BitcoinAddress(Keys.Value.Btc, index, NBitcoin.Network.TestNet)
        : HdKeys.EthereumAddress(Keys.Value.Eth, index);

    public bool IsValidAddress(string address) => Network == Networks.Bitcoin
        ? HdKeys.IsValidBitcoinAddress(address, NBitcoin.Network.TestNet)
        : HdKeys.IsValidEthereumAddress(address);

    public string NormalizeAddress(string address) => Network == Networks.Bitcoin
        ? HdKeys.NormalizeBitcoinAddress(address, NBitcoin.Network.TestNet)
        : HdKeys.NormalizeEthereumAddress(address);

    public Task<long> GetTipHeightAsync(CancellationToken ct) => Task.FromResult(CurrentHeight());

    public async Task<DepositScanResult> ScanAsync(DepositScanContext context, CancellationToken ct)
    {
        var fromBlock = (context.CursorBlock ?? 0) - 500;
        var candidates = PollsAddresses ? context.AddressesToPoll.ToList() : null;
        var query = _db.SimulatedChainTransactions.AsNoTracking()
            .Where(t => t.Network == Network && t.Direction == SimTxDirection.Inbound && t.BlockNumber > fromBlock);
        if (candidates is not null)
        {
            query = query.Where(t => candidates.Contains(t.ToAddress));
        }

        var txs = await query.ToListAsync(ct);
        var deposits = txs
            .Where(t => context.WatchedAddresses.Contains(t.ToAddress))
            .Select(t => new ObservedDeposit(t.Asset, t.TxHash, t.OutputIndex, t.ToAddress, t.Amount, t.BlockNumber, Confirmations(t.BlockNumber, context.TipHeight)))
            .ToList();

        return new DepositScanResult(deposits, context.TipHeight);
    }

    public async Task<ChainTxStatus> GetTransactionStatusAsync(string asset, string txHash, CancellationToken ct)
    {
        var tx = await _db.SimulatedChainTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Network == Network && t.TxHash == txHash, ct);
        if (tx is null)
        {
            return new ChainTxStatus(false, 0, null, false, null);
        }

        var tip = CurrentHeight();
        var confirmations = Confirmations(tx.BlockNumber, tip);
        decimal? fee = tx.Direction == SimTxDirection.Outbound ? SimulatedNetworkFee() : null;
        return new ChainTxStatus(true, confirmations, confirmations > 0 ? tx.BlockNumber : null, false, fee);
    }

    public Task<SignedWithdrawal> SignWithdrawalAsync(WithdrawalSignRequest request, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var txHash = Network == Networks.Ethereum ? "0x" + hash : hash;
        var raw = Json.Serialize(new { simulated = true, request.Asset, request.ToAddress, request.Amount });
        return Task.FromResult(new SignedWithdrawal(txHash, raw, SimulatedNetworkFee(), FeeAsset, null));
    }

    public async Task<BroadcastResult> BroadcastAsync(SignedWithdrawal transaction, CancellationToken ct)
    {
        if (await _db.SimulatedChainTransactions.AnyAsync(t => t.Network == Network && t.TxHash == transaction.TxHash, ct))
        {
            return new BroadcastResult(BroadcastOutcome.AlreadyKnown);
        }

        var payload = Json.Deserialize<SimRaw>(transaction.RawTransaction) ?? throw new InvalidOperationException("Invalid simulated transaction.");
        _db.SimulatedChainTransactions.Add(new SimulatedChainTransaction
        {
            Id = Ids.New(),
            Network = Network,
            Asset = payload.Asset,
            TxHash = transaction.TxHash,
            OutputIndex = 0,
            ToAddress = payload.ToAddress,
            Amount = payload.Amount,
            Direction = SimTxDirection.Outbound,
            BlockNumber = CurrentHeight() + 1,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return new BroadcastResult(BroadcastOutcome.Accepted, transaction.TxHash);
    }

    public Task<decimal> GetHotWalletBalanceAsync(string asset, CancellationToken ct) => Task.FromResult(1_000_000m);

    /// <summary>Creates an incoming transaction to <paramref name="address"/> that is mined in the next simulated block.</summary>
    public async Task<SimulatedChainTransaction> CreateInboundAsync(string asset, string address, decimal amount, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var tx = new SimulatedChainTransaction
        {
            Id = Ids.New(),
            Network = Network,
            Asset = asset,
            TxHash = Network == Networks.Ethereum ? "0x" + hash : hash,
            OutputIndex = 0,
            ToAddress = NormalizeAddress(address),
            Amount = amount,
            Direction = SimTxDirection.Inbound,
            BlockNumber = CurrentHeight() + 1,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.SimulatedChainTransactions.Add(tx);
        await _db.SaveChangesAsync(ct);
        return tx;
    }

    private string FeeAsset => Network == Networks.Bitcoin ? AssetCodes.BTC : AssetCodes.ETH;

    private decimal SimulatedNetworkFee() => Network == Networks.Bitcoin ? 0.00002m : 0.0004m;

    private long CurrentHeight() => (long)((_clock.GetUtcNow() - Epoch).Ticks / BlockTime.Ticks);

    private static int Confirmations(long block, long tip) => (int)Math.Max(0, tip - block + 1);

    private sealed record SimRaw(bool Simulated, string Asset, string ToAddress, decimal Amount);
}
