using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Crypton.Api.Infrastructure;

namespace Crypton.Tests.Infrastructure;

public static class Http
{
    public static readonly JsonSerializerOptions Json = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new DecimalStringConverter());
        options.Converters.Add(new NullableDecimalStringConverter());
        return options;
    }

    public static async Task<T> GetOk<T>(this HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await ReadOk<T>(response, $"GET {url}");
    }

    public static async Task<T> PostOk<T>(this HttpClient client, string url, object? body = null)
    {
        using var response = await client.PostAsJsonAsync(url, body ?? new { }, Json);
        return await ReadOk<T>(response, $"POST {url}");
    }

    public static async Task PostNoContent(this HttpClient client, string url, object? body = null)
    {
        using var response = await client.PostAsJsonAsync(url, body ?? new { }, Json);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpAssertException($"POST {url} failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    public static async Task<T> PutOk<T>(this HttpClient client, string url, object body)
    {
        using var response = await client.PutAsJsonAsync(url, body, Json);
        return await ReadOk<T>(response, $"PUT {url}");
    }

    /// <summary>Sends a request expecting a problem response and returns (status, code).</summary>
    public static async Task<(HttpStatusCode Status, string? Code, string Body)> PostProblem(this HttpClient client, string url, object? body = null)
    {
        using var response = await client.PostAsJsonAsync(url, body ?? new { }, Json);
        return await ReadProblem(response);
    }

    public static async Task<(HttpStatusCode Status, string? Code, string Body)> GetProblem(this HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await ReadProblem(response);
    }

    public static async Task<(HttpStatusCode Status, string? Code, string Body)> ReadProblem(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
        {
            throw new HttpAssertException($"Expected a failure but got {(int)response.StatusCode}: {text}");
        }

        string? code = null;
        try
        {
            code = JsonNode.Parse(text)?["code"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        return (response.StatusCode, code, text);
    }

    private static async Task<T> ReadOk<T>(HttpResponseMessage response, string what)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpAssertException($"{what} failed with {(int)response.StatusCode}: {text}");
        }

        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new HttpAssertException($"{what} returned an empty body.");
    }
}

public sealed class HttpAssertException(string message) : Exception(message);
