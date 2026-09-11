using System.Net;
using System.Text;
using Crypton.Core.Wallets;
using Crypton.Integrations.Blockchain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace Crypton.Tests.Unit;

public class BitcoinGatewayTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (BitcoinGateway Gateway, Key HotKey, StubHandler Handler) Create(long utxoSats)
    {
        var hot = new Key();
        var hotAddress = hot.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet).ToString();
        var fundingTx = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith($"/address/{hotAddress}/utxo") => Json("[{\"txid\":\"" + fundingTx + "\",\"vout\":1,\"value\":" + utxoSats + ",\"status\":{\"confirmed\":true}}]"),
            var p when p.EndsWith("/v1/fees/recommended") => Json("""{"fastestFee":12,"halfHourFee":6,"hourFee":4,"economyFee":2,"minimumFee":1}"""),
            var p when p.EndsWith("/blocks/tip/height") => Json("100200"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var options = Options.Create(new BitcoinOptions
        {
            Network = "testnet4",
            ApiBaseUrl = "https://mempool.test/testnet4/api",
            HotWalletWif = hot.GetWif(Network.TestNet).ToString(),
        });
        return (new BitcoinGateway(new StubFactory(handler), options, NullLogger<BitcoinGateway>.Instance), hot, handler);
    }

    [Fact]
    public async Task Signs_a_valid_segwit_transaction_with_change()
    {
        var (gateway, hot, _) = Create(utxoSats: 200_000);
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet);

        var signed = await gateway.SignWithdrawalAsync(new WithdrawalSignRequest(Guid.NewGuid(), "BTC", destination.ToString(), 0.0015m, null), CancellationToken.None);
        var tx = Transaction.Parse(signed.RawTransaction, Network.TestNet);

        Assert.Equal(tx.GetHash().ToString(), signed.TxHash);
        Assert.Contains(tx.Outputs, o => o.ScriptPubKey == destination.ScriptPubKey && o.Value == Money.Satoshis(150_000));
        var change = Assert.Single(tx.Outputs, o => o.ScriptPubKey == hot.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet).ScriptPubKey);
        var feeSats = 200_000 - 150_000 - change.Value.Satoshi;
        Assert.Equal(Money.Satoshis(feeSats).ToDecimal(MoneyUnit.BTC), signed.NetworkFee);

        // ~6 sat/vB for a 1-in/2-out P2WPKH transaction (~141 vbytes).
        Assert.InRange(feeSats, 6 * 130, 6 * 160);
        Assert.All(tx.Inputs, i => Assert.NotEmpty(i.WitScript.Pushes));
    }

    [Fact]
    public async Task Reports_insufficient_hot_wallet_funds_without_signing()
    {
        var (gateway, _, _) = Create(utxoSats: 10_000);
        var destination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.TestNet).ToString();
        await Assert.ThrowsAsync<HotWalletInsufficientFundsException>(() =>
            gateway.SignWithdrawalAsync(new WithdrawalSignRequest(Guid.NewGuid(), "BTC", destination, 0.001m, null), CancellationToken.None));
    }
}
