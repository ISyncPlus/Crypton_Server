using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crypton.Core.Domain;
using Crypton.Core.Fiat;
using Crypton.Core.Kyc;
using Crypton.Integrations.Kyc;
using Crypton.Integrations.Payments;
using Crypton.Integrations.Pricing;

namespace Crypton.Tests.Unit;

public class IntegrationParsingTests
{
    [Fact]
    public void Paystack_signature_is_hmac_sha512_of_raw_body()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"charge.success\",\"data\":{\"reference\":\"crp_dep_abc\"}}");
        const string secret = "sk_test_example";
        var signature = Convert.ToHexStringLower(HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), body));
        Assert.True(PaystackGateway.VerifySignature(body, signature, secret));
        Assert.True(PaystackGateway.VerifySignature(body, signature.ToUpperInvariant(), secret));
        Assert.False(PaystackGateway.VerifySignature(body, signature, "sk_test_other"));
        Assert.False(PaystackGateway.VerifySignature(body, "not-hex", secret));
    }

    [Fact]
    public void Paystack_verify_response_parsing()
    {
        using var doc = JsonDocument.Parse("""
            {"id": 4099260516, "status": "success", "reference": "crp_dep_abc", "amount": 5000000, "currency": "NGN",
             "gateway_response": "Approved", "paid_at": "2026-09-10T10:12:00.000Z", "fees": 75000}
            """);
        var v = PaystackGateway.ParseVerification(doc.RootElement, "crp_dep_abc");
        Assert.Equal(ProviderPaymentStatus.Success, v.Status);
        Assert.Equal(5_000_000, v.AmountMinor);
        Assert.Equal(75_000, v.FeesMinor);
        Assert.Equal("4099260516", v.ProviderTransactionId);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 10, 12, 0, TimeSpan.Zero), v.PaidAt);

        using var abandoned = JsonDocument.Parse("""{"status":"abandoned","reference":"r","amount":100,"currency":"NGN","fees":null}""");
        Assert.Equal(ProviderPaymentStatus.Abandoned, PaystackGateway.ParseVerification(abandoned.RootElement, "r").Status);
    }

    [Theory]
    [InlineData("success", ProviderTransferStatus.Success)]
    [InlineData("pending", ProviderTransferStatus.Pending)]
    [InlineData("otp", ProviderTransferStatus.OtpRequired)]
    [InlineData("failed", ProviderTransferStatus.Failed)]
    [InlineData("reversed", ProviderTransferStatus.Reversed)]
    public void Paystack_transfer_status_mapping(string raw, ProviderTransferStatus expected)
    {
        using var doc = JsonDocument.Parse($$"""{"status":"{{raw}}","transfer_code":"TRF_x","reference":"crp_wd_1"}""");
        var result = PaystackGateway.ParseTransfer(doc.RootElement);
        Assert.Equal(expected, result.Status);
        Assert.Equal("TRF_x", result.TransferCode);
    }

    [Fact]
    public void CoinGecko_response_parsing()
    {
        using var doc = JsonDocument.Parse("""
            {"bitcoin":{"ngn":147250012.123,"usd":95000.51,"ngn_24h_change":-1.234567,"last_updated_at":1789120000},
             "tether":{"ngn":1551.2,"usd":1.0001},
             "ethereum":{"usd":3500}}
            """);
        var map = new Dictionary<string, string> { ["bitcoin"] = AssetCodes.BTC, ["tether"] = AssetCodes.USDT, ["ethereum"] = AssetCodes.ETH };
        var now = DateTimeOffset.FromUnixTimeSeconds(1789120100);
        var prices = CoinGeckoPriceProvider.Parse(doc.RootElement, map, now);
        Assert.Equal(2, prices.Count);
        var btc = prices.Single(p => p.Asset == AssetCodes.BTC);
        Assert.Equal(147_250_012.12m, btc.PriceNgn);
        Assert.Equal(-1.23m, btc.Change24hPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789120000), btc.UpdatedAt);
        Assert.Equal(now, prices.Single(p => p.Asset == AssetCodes.USDT).UpdatedAt);
    }

    [Fact]
    public void Dojah_match_evaluation()
    {
        var check = new KycIdentityCheck(KycIdType.NIN, "70123456789", "Adaora", "Okafor", new DateOnly(1991, 4, 12), null);
        using var match = JsonDocument.Parse("""{"first_name":"ADAORA","middle_name":"CHIDINMA","last_name":"OKAFOR","date_of_birth":"12-04-1991"}""");
        Assert.Equal(KycVerificationOutcome.Verified, DojahKycVerifier.Evaluate(check, match.RootElement).Outcome);

        using var wrongDob = JsonDocument.Parse("""{"first_name":"ADAORA","last_name":"OKAFOR","date_of_birth":"1990-01-01"}""");
        Assert.Equal(KycVerificationOutcome.NotVerified, DojahKycVerifier.Evaluate(check, wrongDob.RootElement).Outcome);

        using var noDob = JsonDocument.Parse("""{"first_name":"ADAORA","last_name":"OKAFOR"}""");
        Assert.Equal(KycVerificationOutcome.ManualReview, DojahKycVerifier.Evaluate(check, noDob.RootElement).Outcome);
    }
}
