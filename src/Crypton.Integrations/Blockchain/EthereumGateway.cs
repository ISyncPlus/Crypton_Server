using System.Numerics;
using Crypton.Core.Common;
using Crypton.Core.Domain;
using Crypton.Core.Wallets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using Nethereum.Contracts;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Hex.HexTypes;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace Crypton.Integrations.Blockchain;

/// <summary>Ethereum (native ETH) and ERC-20 USDT via JSON-RPC, with offline EIP-1559 signing.</summary>
public sealed class EthereumGateway : IChainGateway
{
    public const string TransferEventTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
    private const long NativeTransferGas = 21_000;

    private readonly EthereumOptions _options;
    private readonly ILogger<EthereumGateway> _logger;
    private readonly ExtPubKey? _accountXpub;
    private readonly Account? _hotAccount;

    public EthereumGateway(IOptions<EthereumOptions> options, ILogger<EthereumGateway> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.AccountXpub))
        {
            _accountXpub = HdKeys.ParseEthereumAccountXpub(_options.AccountXpub);
        }

        if (!string.IsNullOrWhiteSpace(_options.HotWalletPrivateKey))
        {
            _hotAccount = new Account(_options.HotWalletPrivateKey.Trim(), new BigInteger(_options.ChainId));
        }
    }

    public string Network => Networks.Ethereum;

    public bool IsSimulated => false;

    public bool IsConfigured => _accountXpub is not null && _hotAccount is not null && !string.IsNullOrWhiteSpace(_options.RpcUrl);

    public bool PollsAddresses => false;

    public IReadOnlyList<string> Assets => string.IsNullOrWhiteSpace(_options.UsdtContract) ? [AssetCodes.ETH] : [AssetCodes.ETH, AssetCodes.USDT];

    public string? HotWalletAddress => _hotAccount?.Address is { } a ? HdKeys.NormalizeEthereumAddress(a) : null;

    public string DeriveDepositAddress(int index) =>
        HdKeys.EthereumAddress(_accountXpub ?? throw new InvalidOperationException("Ethereum AccountXpub is not configured."), index);

    public bool IsValidAddress(string address) => HdKeys.IsValidEthereumAddress(address);

    public string NormalizeAddress(string address) => HdKeys.NormalizeEthereumAddress(address);

    public async Task<long> GetTipHeightAsync(CancellationToken ct)
    {
        var number = await ReadOnlyWeb3().Eth.Blocks.GetBlockNumber.SendRequestAsync();
        return (long)number.Value;
    }

    public async Task<DepositScanResult> ScanAsync(DepositScanContext context, CancellationToken ct)
    {
        var web3 = ReadOnlyWeb3();
        var from = context.CursorBlock is { } cursor ? cursor + 1 : _options.StartBlock ?? Math.Max(0, context.TipHeight - 12);
        if (from > context.TipHeight)
        {
            return new DepositScanResult([], context.CursorBlock);
        }

        var to = Math.Min(context.TipHeight, from + Math.Max(1, _options.MaxBlocksPerScan) - 1);
        var found = new List<ObservedDeposit>();

        for (var n = from; n <= to; n++)
        {
            ct.ThrowIfCancellationRequested();
            var block = await web3.Eth.Blocks.GetBlockWithTransactionsByNumber.SendRequestAsync(new HexBigInteger(n));
            if (block?.Transactions is null)
            {
                continue;
            }

            foreach (var tx in block.Transactions)
            {
                if (string.IsNullOrEmpty(tx.To) || tx.Value is null || tx.Value.Value <= 0)
                {
                    continue;
                }

                var toAddress = NormalizeAddress(tx.To);
                if (!context.WatchedAddresses.Contains(toAddress))
                {
                    continue;
                }

                if (TryFromBaseUnits(tx.Value.Value, 18, out var amount))
                {
                    found.Add(new ObservedDeposit(AssetCodes.ETH, tx.TransactionHash, 0, toAddress, amount, n, (int)(context.TipHeight - n + 1)));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.UsdtContract))
        {
            var filter = new NewFilterInput
            {
                FromBlock = new BlockParameter(new HexBigInteger(from)),
                ToBlock = new BlockParameter(new HexBigInteger(to)),
                Address = [_options.UsdtContract],
                Topics = [TransferEventTopic],
            };

            var logs = await web3.Eth.Filters.GetLogs.SendRequestAsync(filter);
            foreach (var log in logs ?? [])
            {
                if (log.Removed || log.Topics is null || log.Topics.Length < 3 || log.Data is null)
                {
                    continue;
                }

                var topic = log.Topics[2]?.ToString();
                if (topic is null || topic.Length < 42)
                {
                    continue;
                }

                var toAddress = NormalizeAddress("0x" + topic[^40..]);
                if (!context.WatchedAddresses.Contains(toAddress))
                {
                    continue;
                }

                var value = new HexBigInteger(log.Data).Value;
                if (value <= 0 || !TryFromBaseUnits(value, _options.UsdtDecimals, out var amount))
                {
                    continue;
                }

                var blockNumber = (long)log.BlockNumber.Value;
                found.Add(new ObservedDeposit(AssetCodes.USDT, log.TransactionHash, (int)log.LogIndex.Value, toAddress, amount, blockNumber,
                    (int)(context.TipHeight - blockNumber + 1)));
            }
        }

        return new DepositScanResult(found, to);
    }

    public async Task<ChainTxStatus> GetTransactionStatusAsync(string asset, string txHash, CancellationToken ct)
    {
        var web3 = ReadOnlyWeb3();
        var receipt = await web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
        if (receipt is null || receipt.BlockNumber is null)
        {
            var pending = await web3.Eth.Transactions.GetTransactionByHash.SendRequestAsync(txHash);
            return new ChainTxStatus(pending is not null, 0, null, false, null);
        }

        var tip = await GetTipHeightAsync(ct);
        var block = (long)receipt.BlockNumber.Value;
        var failed = receipt.Status is not null && receipt.Status.Value == 0;
        decimal? fee = null;
        if (receipt.GasUsed is not null && receipt.EffectiveGasPrice is not null &&
            TryFromBaseUnits(receipt.GasUsed.Value * receipt.EffectiveGasPrice.Value, 18, out var paid))
        {
            fee = paid;
        }

        return new ChainTxStatus(true, (int)Math.Max(0, tip - block + 1), block, failed, fee);
    }

    public async Task<SignedWithdrawal> SignWithdrawalAsync(WithdrawalSignRequest request, CancellationToken ct)
    {
        if (_hotAccount is null)
        {
            throw new InvalidOperationException("Ethereum hot wallet is not configured.");
        }

        var web3 = new Web3(_hotAccount, _options.RpcUrl);
        var from = _hotAccount.Address;
        var to = NormalizeAddress(request.ToAddress);

        var pendingNonce = (long)(await web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(from, BlockParameter.CreatePending())).Value;
        var nonce = Math.Max(pendingNonce, (request.PreviousNonce ?? -1) + 1L);

        var latest = await web3.Eth.Blocks.GetBlockWithTransactionsHashesByNumber.SendRequestAsync(BlockParameter.CreateLatest());
        var baseFee = latest?.BaseFeePerGas?.Value ?? Web3.Convert.ToWei(20, UnitConversion.EthUnit.Gwei);
        var priority = Web3.Convert.ToWei(_options.PriorityFeeGwei, UnitConversion.EthUnit.Gwei);
        var maxFee = baseFee * 2 + priority;
        var cap = Web3.Convert.ToWei(_options.MaxFeeGwei, UnitConversion.EthUnit.Gwei);
        if (maxFee > cap)
        {
            throw new HotWalletInsufficientFundsException($"Ethereum gas price is above the configured cap of {_options.MaxFeeGwei} gwei; waiting.");
        }

        var ethBalance = (await web3.Eth.GetBalance.SendRequestAsync(from)).Value;
        TransactionInput input;
        if (request.Asset == AssetCodes.ETH)
        {
            var value = Web3.Convert.ToWei(request.Amount, 18);
            var gas = new BigInteger(NativeTransferGas);
            if (ethBalance < value + gas * maxFee)
            {
                throw new HotWalletInsufficientFundsException($"Ethereum hot wallet {from} has {Web3.Convert.FromWei(ethBalance)} ETH; {request.Amount} ETH plus gas is needed.");
            }

            input = new TransactionInput
            {
                From = from,
                To = to,
                Value = new HexBigInteger(value),
                Gas = new HexBigInteger(gas),
            };
        }
        else if (request.Asset == AssetCodes.USDT)
        {
            if (string.IsNullOrWhiteSpace(_options.UsdtContract))
            {
                throw new InvalidOperationException("USDT contract is not configured.");
            }

            var units = Web3.Convert.ToWei(request.Amount, _options.UsdtDecimals);
            var token = web3.Eth.ERC20.GetContractService(_options.UsdtContract);
            var tokenBalance = await token.BalanceOfQueryAsync(from);
            if (tokenBalance < units)
            {
                throw new HotWalletInsufficientFundsException($"Ethereum hot wallet {from} has {Web3.Convert.FromWei(tokenBalance, _options.UsdtDecimals)} USDT; {request.Amount} USDT is needed.");
            }

            var data = new Nethereum.Contracts.Standards.ERC20.ContractDefinition.TransferFunction { To = to, Value = units }.GetCallData().ToHex(true);
            var estimate = await web3.Eth.Transactions.EstimateGas.SendRequestAsync(new CallInput { From = from, To = _options.UsdtContract, Data = data });
            var gas = estimate.Value * 12 / 10;
            if (ethBalance < gas * maxFee)
            {
                throw new HotWalletInsufficientFundsException($"Ethereum hot wallet {from} does not have enough ETH to pay gas for a USDT transfer.");
            }

            input = new TransactionInput
            {
                From = from,
                To = _options.UsdtContract,
                Value = new HexBigInteger(0),
                Data = data,
                Gas = new HexBigInteger(gas),
            };
        }
        else
        {
            throw new InvalidOperationException($"Unsupported asset {request.Asset} on Ethereum.");
        }

        input.Nonce = new HexBigInteger(nonce);
        input.MaxFeePerGas = new HexBigInteger(maxFee);
        input.MaxPriorityFeePerGas = new HexBigInteger(priority);
        input.Type = new HexBigInteger(2);
        input.ChainId = new HexBigInteger(_options.ChainId);

        var signed = await web3.TransactionManager.SignTransactionAsync(input);
        var raw = signed.EnsureHexPrefix();
        var hash = "0x" + Sha3Keccack.Current.CalculateHashFromHex(raw);
        TryFromBaseUnits(input.Gas.Value * maxFee, 18, out var maxNetworkFee);
        return new SignedWithdrawal(hash, raw, maxNetworkFee, AssetCodes.ETH, (int)nonce);
    }

    public async Task<BroadcastResult> BroadcastAsync(SignedWithdrawal transaction, CancellationToken ct)
    {
        try
        {
            var hash = await ReadOnlyWeb3().Eth.Transactions.SendRawTransaction.SendRequestAsync(transaction.RawTransaction.EnsureHexPrefix());
            return new BroadcastResult(BroadcastOutcome.Accepted, hash);
        }
        catch (RpcResponseException ex)
        {
            var message = ex.RpcError?.Message ?? ex.Message;
            if (message.Contains("already known", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("already imported", StringComparison.OrdinalIgnoreCase))
            {
                return new BroadcastResult(BroadcastOutcome.AlreadyKnown, message);
            }

            return new BroadcastResult(BroadcastOutcome.Rejected, message);
        }
        catch (Exception ex) when (ex is RpcClientTimeoutException or RpcClientUnknownException or HttpRequestException or TaskCanceledException)
        {
            return new BroadcastResult(BroadcastOutcome.Unknown, ex.Message);
        }
    }

    public async Task<decimal> GetHotWalletBalanceAsync(string asset, CancellationToken ct)
    {
        if (_hotAccount is null)
        {
            return 0m;
        }

        var web3 = ReadOnlyWeb3();
        if (asset == AssetCodes.USDT)
        {
            if (string.IsNullOrWhiteSpace(_options.UsdtContract))
            {
                return 0m;
            }

            var units = await web3.Eth.ERC20.GetContractService(_options.UsdtContract).BalanceOfQueryAsync(_hotAccount.Address);
            return TryFromBaseUnits(units, _options.UsdtDecimals, out var usdt) ? usdt : 0m;
        }

        var wei = (await web3.Eth.GetBalance.SendRequestAsync(_hotAccount.Address)).Value;
        return TryFromBaseUnits(wei, 18, out var eth) ? eth : 0m;
    }

    internal static bool TryFromBaseUnits(BigInteger value, int decimals, out decimal amount)
    {
        try
        {
            amount = MoneyMath.Floor(Web3.Convert.FromWei(value, decimals), 18);
            return true;
        }
        catch (OverflowException)
        {
            amount = 0;
            return false;
        }
    }

    private Web3 ReadOnlyWeb3() => new(_options.RpcUrl);
}
