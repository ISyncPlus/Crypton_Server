using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Crypton.Core.Domain;
using Crypton.Core.Wallets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Policy;

namespace Crypton.Integrations.Blockchain;

/// <summary>Bitcoin via an Esplora-compatible REST API (mempool.space / Blockstream) and NBitcoin signing.</summary>
public sealed class BitcoinGateway : IChainGateway
{
    public const string HttpClientName = "bitcoin";

    private readonly IHttpClientFactory _http;
    private readonly BitcoinOptions _options;
    private readonly ILogger<BitcoinGateway> _logger;
    private readonly Network _network;
    private readonly ExtPubKey? _accountXpub;
    private readonly Key? _hotKey;

    public BitcoinGateway(IHttpClientFactory http, IOptions<BitcoinOptions> options, ILogger<BitcoinGateway> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _network = HdKeys.BitcoinNetwork(_options.Network);

        if (!string.IsNullOrWhiteSpace(_options.AccountXpub))
        {
            _accountXpub = HdKeys.ParseAccountXpub(_options.AccountXpub, _network);
        }

        if (!string.IsNullOrWhiteSpace(_options.HotWalletWif))
        {
            _hotKey = Key.Parse(_options.HotWalletWif.Trim(), _network);
        }
    }

    public string Network => Networks.Bitcoin;

    public bool IsSimulated => false;

    public bool IsConfigured => _accountXpub is not null && _hotKey is not null && !string.IsNullOrWhiteSpace(_options.ApiBaseUrl);

    public bool PollsAddresses => true;

    public IReadOnlyList<string> Assets { get; } = [AssetCodes.BTC];

    public string? HotWalletAddress => _hotKey?.PubKey.GetAddress(ScriptPubKeyType.Segwit, _network).ToString();

    public string DeriveDepositAddress(int index) =>
        HdKeys.BitcoinAddress(_accountXpub ?? throw new InvalidOperationException("Bitcoin AccountXpub is not configured."), index, _network);

    public bool IsValidAddress(string address) => HdKeys.IsValidBitcoinAddress(address, _network);

    public string NormalizeAddress(string address) => HdKeys.NormalizeBitcoinAddress(address, _network);

    public async Task<long> GetTipHeightAsync(CancellationToken ct)
    {
        var text = await Client().GetStringAsync("blocks/tip/height", ct);
        return long.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<DepositScanResult> ScanAsync(DepositScanContext context, CancellationToken ct)
    {
        var found = new List<ObservedDeposit>();
        foreach (var address in context.AddressesToPoll)
        {
            List<EsploraTx>? txs;
            try
            {
                txs = await Client().GetFromJsonAsync<List<EsploraTx>>($"address/{address}/txs", ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Fetching transactions for {Address} failed", address);
                continue;
            }

            foreach (var tx in txs ?? [])
            {
                for (var i = 0; i < tx.Vout.Count; i++)
                {
                    var output = tx.Vout[i];
                    if (output.ScriptPubKeyAddress is null || output.Value <= 0)
                    {
                        continue;
                    }

                    if (!string.Equals(output.ScriptPubKeyAddress, address, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var confirmations = tx.Status.Confirmed && tx.Status.BlockHeight is { } h ? (int)Math.Max(0, context.TipHeight - h + 1) : 0;
                    found.Add(new ObservedDeposit(AssetCodes.BTC, tx.Txid, i, address, Money.Satoshis(output.Value).ToDecimal(MoneyUnit.BTC), tx.Status.BlockHeight, confirmations));
                }
            }
        }

        return new DepositScanResult(found, null);
    }

    public async Task<ChainTxStatus> GetTransactionStatusAsync(string asset, string txHash, CancellationToken ct)
    {
        using var response = await Client().GetAsync($"tx/{txHash}", ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return new ChainTxStatus(false, 0, null, false, null);
        }

        response.EnsureSuccessStatusCode();
        var tx = await response.Content.ReadFromJsonAsync<EsploraTx>(ct) ?? throw new InvalidOperationException("Empty transaction response.");
        var fee = Money.Satoshis(tx.Fee).ToDecimal(MoneyUnit.BTC);
        if (!tx.Status.Confirmed || tx.Status.BlockHeight is null)
        {
            return new ChainTxStatus(true, 0, null, false, fee);
        }

        var tip = await GetTipHeightAsync(ct);
        return new ChainTxStatus(true, (int)Math.Max(0, tip - tx.Status.BlockHeight.Value + 1), tx.Status.BlockHeight, false, fee);
    }

    public async Task<SignedWithdrawal> SignWithdrawalAsync(WithdrawalSignRequest request, CancellationToken ct)
    {
        if (_hotKey is null)
        {
            throw new InvalidOperationException("Bitcoin hot wallet is not configured.");
        }

        var hotAddress = _hotKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, _network);
        var destination = NBitcoin.BitcoinAddress.Create(request.ToAddress, _network);
        var utxos = await Client().GetFromJsonAsync<List<EsploraUtxo>>($"address/{hotAddress}/utxo", ct) ?? [];
        var coins = utxos
            .Where(u => u.Status.Confirmed || _options.SpendUnconfirmedChange)
            .Select(u => (ICoin)new Coin(uint256.Parse(u.Txid), (uint)u.Vout, Money.Satoshis(u.Value), hotAddress.ScriptPubKey))
            .ToArray();

        var feeRateSatPerVb = await GetFeeRateAsync(ct);
        var builder = _network.CreateTransactionBuilder();
        builder.AddCoins(coins);
        builder.AddKeys(_hotKey);
        builder.Send(destination, Money.Coins(request.Amount));
        builder.SetChange(hotAddress);
        builder.SendEstimatedFees(new FeeRate(Money.Satoshis((long)Math.Ceiling(feeRateSatPerVb * 1000m))));

        Transaction tx;
        try
        {
            tx = builder.BuildTransaction(sign: true);
        }
        catch (NotEnoughFundsException ex)
        {
            var balance = Money.Satoshis(utxos.Sum(u => u.Value)).ToDecimal(MoneyUnit.BTC);
            throw new HotWalletInsufficientFundsException($"Bitcoin hot wallet {hotAddress} has {balance} BTC available; {request.Amount} BTC plus fees is needed. ({ex.Message})");
        }

        if (!builder.Verify(tx, out TransactionPolicyError[] errors))
        {
            throw new InvalidOperationException("Signed Bitcoin transaction failed policy checks: " + string.Join("; ", errors.Select(e => e.ToString())));
        }

        var fee = tx.GetFee(coins).ToDecimal(MoneyUnit.BTC);
        return new SignedWithdrawal(tx.GetHash().ToString(), tx.ToHex(), fee, AssetCodes.BTC, null);
    }

    public async Task<BroadcastResult> BroadcastAsync(SignedWithdrawal transaction, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(transaction.RawTransaction, Encoding.ASCII, "text/plain");
            using var response = await Client().PostAsync("tx", content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                return new BroadcastResult(BroadcastOutcome.Accepted, body.Trim());
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                return body.Contains("already", StringComparison.OrdinalIgnoreCase)
                    ? new BroadcastResult(BroadcastOutcome.AlreadyKnown, body)
                    : new BroadcastResult(BroadcastOutcome.Rejected, body);
            }

            return new BroadcastResult(BroadcastOutcome.Unknown, $"HTTP {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new BroadcastResult(BroadcastOutcome.Unknown, ex.Message);
        }
    }

    public async Task<decimal> GetHotWalletBalanceAsync(string asset, CancellationToken ct)
    {
        if (HotWalletAddress is null)
        {
            return 0m;
        }

        var utxos = await Client().GetFromJsonAsync<List<EsploraUtxo>>($"address/{HotWalletAddress}/utxo", ct) ?? [];
        return Money.Satoshis(utxos.Sum(u => u.Value)).ToDecimal(MoneyUnit.BTC);
    }

    private async Task<decimal> GetFeeRateAsync(CancellationToken ct)
    {
        decimal rate;
        try
        {
            var fees = await Client().GetFromJsonAsync<RecommendedFees>("v1/fees/recommended", ct) ?? throw new InvalidOperationException("No fee data.");
            rate = _options.FeeTarget.ToLowerInvariant() switch
            {
                "fastest" => fees.FastestFee,
                "hour" => fees.HourFee,
                "economy" => fees.EconomyFee,
                _ => fees.HalfHourFee,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Plain Esplora (Blockstream) exposes confirmation-target estimates instead.
            var estimates = await Client().GetFromJsonAsync<Dictionary<string, decimal>>("fee-estimates", ct) ?? [];
            rate = estimates.TryGetValue("3", out var r3) ? r3 : estimates.TryGetValue("6", out var r6) ? r6 : 5m;
        }

        return Math.Clamp(rate <= 0 ? 1m : rate, 1m, _options.MaxFeeRateSatPerVb);
    }

    private HttpClient Client()
    {
        var client = _http.CreateClient(HttpClientName);
        var baseUrl = _options.ApiBaseUrl.TrimEnd('/') + "/";
        client.BaseAddress = new Uri(baseUrl);
        return client;
    }

    internal sealed class EsploraTx
    {
        [JsonPropertyName("txid")]
        public string Txid { get; set; } = "";

        [JsonPropertyName("fee")]
        public long Fee { get; set; }

        [JsonPropertyName("vout")]
        public List<EsploraVout> Vout { get; set; } = [];

        [JsonPropertyName("status")]
        public EsploraStatus Status { get; set; } = new();
    }

    internal sealed class EsploraVout
    {
        [JsonPropertyName("scriptpubkey_address")]
        public string? ScriptPubKeyAddress { get; set; }

        [JsonPropertyName("value")]
        public long Value { get; set; }
    }

    internal sealed class EsploraStatus
    {
        [JsonPropertyName("confirmed")]
        public bool Confirmed { get; set; }

        [JsonPropertyName("block_height")]
        public long? BlockHeight { get; set; }
    }

    internal sealed class EsploraUtxo
    {
        [JsonPropertyName("txid")]
        public string Txid { get; set; } = "";

        [JsonPropertyName("vout")]
        public int Vout { get; set; }

        [JsonPropertyName("value")]
        public long Value { get; set; }

        [JsonPropertyName("status")]
        public EsploraStatus Status { get; set; } = new();
    }

    internal sealed class RecommendedFees
    {
        [JsonPropertyName("fastestFee")]
        public decimal FastestFee { get; set; }

        [JsonPropertyName("halfHourFee")]
        public decimal HalfHourFee { get; set; }

        [JsonPropertyName("hourFee")]
        public decimal HourFee { get; set; }

        [JsonPropertyName("economyFee")]
        public decimal EconomyFee { get; set; }
    }
}
