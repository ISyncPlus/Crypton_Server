namespace Crypton.Core.Wallets;

public sealed record ObservedDeposit(
    string Asset,
    string TxHash,
    int OutputIndex,
    string Address,
    decimal Amount,
    long? BlockNumber,
    int Confirmations);

public sealed record DepositScanContext(
    long TipHeight,
    long? CursorBlock,

    /// <summary>All platform deposit addresses on this network, normalized.</summary>
    IReadOnlySet<string> WatchedAddresses,

    /// <summary>For address-polling networks: the addresses to check this round.</summary>
    IReadOnlyList<string> AddressesToPoll);

public sealed record DepositScanResult(IReadOnlyList<ObservedDeposit> Deposits, long? NewCursorBlock);

public sealed record WithdrawalSignRequest(Guid WithdrawalId, string Asset, string ToAddress, decimal Amount, int? PreviousNonce);

public sealed record SignedWithdrawal(string TxHash, string RawTransaction, decimal? NetworkFee, string NetworkFeeAsset, int? Nonce);

public enum BroadcastOutcome
{
    Accepted,
    AlreadyKnown,

    /// <summary>The node definitively refused the transaction.</summary>
    Rejected,

    /// <summary>Timeout / transport error: the transaction may or may not have been received.</summary>
    Unknown,
}

public sealed record BroadcastResult(BroadcastOutcome Outcome, string? Message = null);

public sealed record ChainTxStatus(bool Found, int Confirmations, long? BlockNumber, bool Failed, decimal? NetworkFee);

/// <summary>Raised when the hot wallet cannot cover a withdrawal. Nothing was signed.</summary>
public sealed class HotWalletInsufficientFundsException(string message) : Exception(message);

/// <summary>A blockchain integration for one network (Bitcoin or Ethereum incl. ERC-20 USDT).</summary>
public interface IChainGateway
{
    string Network { get; }

    bool IsSimulated { get; }

    /// <summary>False when required configuration (xpub, RPC URL...) is missing.</summary>
    bool IsConfigured { get; }

    /// <summary>True when deposits are found by polling individual addresses rather than scanning blocks.</summary>
    bool PollsAddresses { get; }

    IReadOnlyList<string> Assets { get; }

    string? HotWalletAddress { get; }

    string DeriveDepositAddress(int index);

    bool IsValidAddress(string address);

    string NormalizeAddress(string address);

    Task<long> GetTipHeightAsync(CancellationToken ct);

    Task<DepositScanResult> ScanAsync(DepositScanContext context, CancellationToken ct);

    Task<SignedWithdrawal> SignWithdrawalAsync(WithdrawalSignRequest request, CancellationToken ct);

    Task<BroadcastResult> BroadcastAsync(SignedWithdrawal transaction, CancellationToken ct);

    Task<ChainTxStatus> GetTransactionStatusAsync(string asset, string txHash, CancellationToken ct);

    Task<decimal> GetHotWalletBalanceAsync(string asset, CancellationToken ct);
}

public sealed class ChainGatewayRegistry(IEnumerable<IChainGateway> gateways)
{
    private readonly Dictionary<string, IChainGateway> _byNetwork = gateways.ToDictionary(g => g.Network, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IChainGateway> All => _byNetwork.Values;

    public IChainGateway? Find(string network) => _byNetwork.GetValueOrDefault(network);

    public IChainGateway Get(string network) =>
        Find(network) ?? throw new Common.AppException(Common.ErrorCodes.NotConfigured, $"The {network} network is not available.", 503);
}

public sealed class BlockchainOptions
{
    public const string Section = "Blockchain";

    /// <summary>Simulated | Live</summary>
    public string Mode { get; set; } = "Simulated";

    public int DepositPollBatchSize { get; set; } = 40;

    public int MaxBroadcastAttempts { get; set; } = 5;

    public bool IsSimulated => string.Equals(Mode, "Simulated", StringComparison.OrdinalIgnoreCase);
}
