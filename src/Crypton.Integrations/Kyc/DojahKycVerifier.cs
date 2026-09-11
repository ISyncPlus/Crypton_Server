using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Crypton.Core.Domain;
using Crypton.Core.Kyc;
using Microsoft.Extensions.Options;

namespace Crypton.Integrations.Kyc;

public sealed class DojahOptions
{
    public const string Section = "Kyc:Dojah";

    /// <summary>https://sandbox.dojah.io for testing, https://api.dojah.io for production.</summary>
    public string BaseUrl { get; set; } = "https://sandbox.dojah.io";

    public string AppId { get; set; } = "";

    /// <summary>Secret key (sandbox keys start with test_sk_, production with prod_sk_).</summary>
    public string SecretKey { get; set; } = "";
}

/// <summary>Tier-1 identity check against Nigerian NIN / BVN records through Dojah. Stores no provider PII.</summary>
public sealed class DojahKycVerifier(IHttpClientFactory http, IOptions<DojahOptions> options) : IKycVerifier
{
    public const string HttpClientName = "dojah";

    public string Name => "dojah";

    public async Task<KycVerificationResult> VerifyIdentityAsync(KycIdentityCheck check, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.AppId) || string.IsNullOrWhiteSpace(o.SecretKey))
        {
            return new KycVerificationResult(KycVerificationOutcome.ManualReview, "Automated verification is not configured.");
        }

        var path = check.IdType == KycIdType.NIN
            ? $"api/v1/kyc/nin?nin={Uri.EscapeDataString(check.IdNumber)}"
            : $"api/v1/kyc/bvn/full?bvn={Uri.EscapeDataString(check.IdNumber)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, o.BaseUrl.TrimEnd('/') + "/" + path);
        request.Headers.TryAddWithoutValidation("AppId", o.AppId);
        request.Headers.TryAddWithoutValidation("Authorization", o.SecretKey);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return new KycVerificationResult(KycVerificationOutcome.NotVerified, $"No record was found for that {check.IdType}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Dojah returned HTTP {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.Object)
        {
            return new KycVerificationResult(KycVerificationOutcome.NotVerified, $"No record was found for that {check.IdType}.");
        }

        return Evaluate(check, entity);
    }

    internal static KycVerificationResult Evaluate(KycIdentityCheck check, JsonElement entity)
    {
        var recordNames = string.Join(' ', new[] { "first_name", "middle_name", "last_name" }
            .Select(k => entity.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
            .Where(v => !string.IsNullOrWhiteSpace(v)));

        var firstMatches = Tokens(check.FirstName).Overlaps(Tokens(recordNames));
        var lastMatches = Tokens(check.LastName).Overlaps(Tokens(recordNames));
        var dob = entity.TryGetProperty("date_of_birth", out var d) && d.ValueKind == JsonValueKind.String ? ParseDate(d.GetString()) : null;
        var dobMatches = dob == check.DateOfBirth;

        var details = new { firstNameMatch = firstMatches, lastNameMatch = lastMatches, dateOfBirthMatch = dobMatches };
        if (firstMatches && lastMatches && dobMatches)
        {
            return new KycVerificationResult(KycVerificationOutcome.Verified, $"{check.IdType} record matched.", details);
        }

        if (dob is null)
        {
            return new KycVerificationResult(KycVerificationOutcome.ManualReview, $"{check.IdType} record found but incomplete; needs manual review.", details);
        }

        return new KycVerificationResult(KycVerificationOutcome.NotVerified,
            "Your name or date of birth doesn't match the government record.", details);
    }

    internal static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats = ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "yyyy/MM/dd", "MM/dd/yyyy"];
        return DateOnly.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static HashSet<string> Tokens(string? value) =>
        Regex.Split((value ?? "").ToUpperInvariant(), "[^A-Z]+").Where(t => t.Length > 1).ToHashSet();
}
