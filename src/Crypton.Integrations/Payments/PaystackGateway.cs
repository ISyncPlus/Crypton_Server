using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crypton.Core.Common;
using Crypton.Core.Fiat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crypton.Integrations.Payments;

public sealed class PaystackOptions
{
    public const string Section = "Payments:Paystack";

    /// <summary>Secret key from Paystack Dashboard > Settings > API Keys & Webhooks (sk_test_... or sk_live_...).</summary>
    public string SecretKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.paystack.co";
}

/// <summary>Paystack: card/bank/transfer checkout for NGN deposits and Transfers API for NGN payouts.</summary>
public sealed class PaystackGateway(IHttpClientFactory http, IOptions<PaystackOptions> options, ILogger<PaystackGateway> logger) : IFiatGateway
{
    public const string HttpClientName = "paystack";

    public string Name => "paystack";

    public bool IsSimulated => false;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.SecretKey);

    public async Task<IReadOnlyList<BankInfo>> ListBanksAsync(CancellationToken ct)
    {
        var banks = new List<BankInfo>();
        string? next = null;
        for (var page = 0; page < 20; page++)
        {
            var url = "bank?country=nigeria&currency=NGN&perPage=100&use_cursor=true" + (next is null ? "" : $"&next={Uri.EscapeDataString(next)}");
            using var doc = await SendAsync(HttpMethod.Get, url, null, ct);
            foreach (var bank in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var active = !bank.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False;
                var deleted = bank.TryGetProperty("is_deleted", out var d) && d.ValueKind == JsonValueKind.True;
                var code = bank.TryGetProperty("code", out var c) ? c.GetString() : null;
                if (active && !deleted && !string.IsNullOrEmpty(code))
                {
                    banks.Add(new BankInfo(code, bank.GetProperty("name").GetString() ?? code, bank.TryGetProperty("slug", out var s) ? s.GetString() : null));
                }
            }

            next = doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
            if (string.IsNullOrEmpty(next))
            {
                break;
            }
        }

        return banks.DistinctBy(b => b.Code).ToList();
    }

    public async Task<ResolvedBankAccount> ResolveAccountAsync(string accountNumber, string bankCode, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"bank/resolve?account_number={Uri.EscapeDataString(accountNumber)}&bank_code={Uri.EscapeDataString(bankCode)}", null, ct);
        var data = doc.RootElement.GetProperty("data");
        return new ResolvedBankAccount(
            data.GetProperty("account_number").GetString() ?? accountNumber,
            data.GetProperty("account_name").GetString() ?? "",
            bankCode);
    }

    public async Task<PaymentInitResult> InitializePaymentAsync(PaymentInitRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["email"] = request.Email,
            ["amount"] = request.AmountMinor.ToString(CultureInfo.InvariantCulture),
            ["currency"] = request.Currency,
            ["reference"] = request.Reference,
            ["callback_url"] = request.CallbackUrl,
            ["metadata"] = JsonSerializer.Serialize(request.Metadata),
        };
        using var doc = await SendAsync(HttpMethod.Post, "transaction/initialize", body, ct);
        var data = doc.RootElement.GetProperty("data");
        return new PaymentInitResult(
            data.GetProperty("authorization_url").GetString() ?? throw new FiatProviderException("Paystack returned no authorization URL.", false),
            data.TryGetProperty("reference", out var r) ? r.GetString() ?? request.Reference : request.Reference,
            data.TryGetProperty("access_code", out var ac) ? ac.GetString() : null);
    }

    public async Task<PaymentVerification> VerifyPaymentAsync(string reference, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await SendAsync(HttpMethod.Get, $"transaction/verify/{Uri.EscapeDataString(reference)}", null, ct);
        }
        catch (FiatProviderException ex) when (ex.IsDefinitive)
        {
            // Unknown reference: the customer has not started paying yet.
            return new PaymentVerification(reference, ProviderPaymentStatus.Pending, 0, "NGN", 0, null, ex.Message, null);
        }

        using (doc)
        {
            return ParseVerification(doc.RootElement.GetProperty("data"), reference);
        }
    }

    internal static PaymentVerification ParseVerification(JsonElement data, string reference)
    {
        var status = (data.TryGetProperty("status", out var s) ? s.GetString() : null)?.ToLowerInvariant() switch
        {
            "success" => ProviderPaymentStatus.Success,
            "failed" or "reversed" => ProviderPaymentStatus.Failed,
            "abandoned" => ProviderPaymentStatus.Abandoned,
            _ => ProviderPaymentStatus.Pending,
        };

        return new PaymentVerification(
            data.TryGetProperty("reference", out var r) ? r.GetString() ?? reference : reference,
            status,
            ReadLong(data, "amount"),
            data.TryGetProperty("currency", out var c) ? c.GetString() ?? "NGN" : "NGN",
            ReadLong(data, "fees"),
            data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null,
            data.TryGetProperty("gateway_response", out var g) ? g.GetString() : null,
            data.TryGetProperty("paid_at", out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var paidAt)
                ? paidAt.ToUniversalTime()
                : null);
    }

    private static long ReadLong(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    public async Task<string> CreateTransferRecipientAsync(string accountName, string accountNumber, string bankCode, string currency, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = "nuban",
            ["name"] = accountName,
            ["account_number"] = accountNumber,
            ["bank_code"] = bankCode,
            ["currency"] = currency,
        };
        using var doc = await SendAsync(HttpMethod.Post, "transferrecipient", body, ct);
        return doc.RootElement.GetProperty("data").GetProperty("recipient_code").GetString()
               ?? throw new FiatProviderException("Paystack returned no recipient code.", false);
    }

    public async Task<TransferResult> InitiateTransferAsync(TransferRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["source"] = "balance",
            ["amount"] = request.AmountMinor,
            ["recipient"] = request.RecipientCode,
            ["reference"] = request.Reference,
            ["reason"] = request.Reason,
            ["currency"] = request.Currency,
        };
        using var doc = await SendAsync(HttpMethod.Post, "transfer", body, ct);
        return ParseTransfer(doc.RootElement.GetProperty("data"));
    }

    public async Task<TransferResult> VerifyTransferAsync(string reference, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Get, $"transfer/verify/{Uri.EscapeDataString(reference)}", null, ct);
            return ParseTransfer(doc.RootElement.GetProperty("data"));
        }
        catch (FiatProviderException ex) when (ex.IsDefinitive)
        {
            return new TransferResult(ProviderTransferStatus.NotFound, null, ex.Message);
        }
    }

    internal static TransferResult ParseTransfer(JsonElement data)
    {
        var raw = (data.TryGetProperty("status", out var s) ? s.GetString() : null)?.ToLowerInvariant();
        var status = raw switch
        {
            "success" => ProviderTransferStatus.Success,
            "failed" or "abandoned" or "rejected" => ProviderTransferStatus.Failed,
            "reversed" => ProviderTransferStatus.Reversed,
            "otp" => ProviderTransferStatus.OtpRequired,
            _ => ProviderTransferStatus.Pending,
        };
        return new TransferResult(status, data.TryGetProperty("transfer_code", out var code) ? code.GetString() : null, raw);
    }

    public bool VerifyWebhookSignature(ReadOnlySpan<byte> body, string? signature)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        return VerifySignature(body, signature, options.Value.SecretKey);
    }

    internal static bool VerifySignature(ReadOnlySpan<byte> body, string signature, string secretKey)
    {
        var expected = HMACSHA512.HashData(Encoding.UTF8.GetBytes(secretKey), body);
        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, options.Value.BaseUrl.TrimEnd('/') + "/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new FiatProviderException($"Paystack request failed: {ex.Message}", isDefinitive: false, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch (JsonException)
            {
            }

            var ok = response.IsSuccessStatusCode && doc is not null &&
                     doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.True;
            if (ok)
            {
                return doc!;
            }

            var message = doc is not null && doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : text;
            doc?.Dispose();
            var definitive = (int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.TooManyRequests;
            logger.LogWarning("Paystack {Method} {Path} returned {Status}: {Message}", method, path.Split('?')[0], (int)response.StatusCode, message);
            throw new FiatProviderException(message ?? $"Paystack returned HTTP {(int)response.StatusCode}.", definitive);
        }
    }
}
