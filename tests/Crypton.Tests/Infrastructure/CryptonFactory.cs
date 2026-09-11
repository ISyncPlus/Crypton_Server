using System.Net.Http.Headers;
using Crypton.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Crypton.Tests.Infrastructure;

/// <summary>
/// Boots the real API against a throwaway PostgreSQL database with simulated blockchain, payments and prices,
/// and a controllable clock. Configuration is passed through environment variables so it is visible to
/// Program.cs from the very first line.
/// </summary>
public sealed class CryptonFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@test.crypton.local";
    public const string AdminPassword = "Admin-Test-Password-1";

    private readonly string _databaseName = $"crypton_test_{Guid.NewGuid():N}";
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "crypton-tests", Guid.NewGuid().ToString("N"));

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public string ConnectionString { get; private set; } = "";

    public static string ServerConnectionString =>
        Environment.GetEnvironmentVariable("CRYPTON_TEST_POSTGRES") ?? "Host=localhost;Port=5432;Username=crypton;Password=crypton;Database=postgres";

    public async ValueTask InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = _databaseName, Pooling = true, MaxPoolSize = 50 };
        ConnectionString = builder.ConnectionString;

        await using (var connection = new NpgsqlConnection(ServerConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["ConnectionStrings__Default"] = ConnectionString,
            ["Jobs__Enabled"] = "false",
            ["RateLimiting__Enabled"] = "false",
            ["Seed__AdminEmail"] = AdminEmail,
            ["Seed__AdminPassword"] = AdminPassword,
            ["Seed__DemoData"] = "false",
            ["Security__RequireTwoFactorForStaff"] = "false",
            ["Blockchain__Mode"] = "Simulated",
            ["Payments__Provider"] = "Simulated",
            ["Pricing__Provider"] = "Simulated",
            ["Pricing__Simulated__Volatility"] = "0",
            ["Email__Provider"] = "Log",
            ["Dev__EnableEndpoints"] = "true",
            ["Kyc__Provider"] = "Manual",
            ["Kyc__IdHashKey"] = "test-id-hash-key",
            ["Jwt__SigningKey"] = "dGVzdC1zaWduaW5nLWtleS1mb3ItY3J5cHRvbi1pbnRlZ3JhdGlvbi10ZXN0cy0xMjM0NQ==",
            ["Jwt__RefreshCookieSecure"] = "false",
            ["App__FrontendBaseUrl"] = "http://localhost:4200",
            ["DataProtection__KeysPath"] = Path.Combine(_tempRoot, "keys"),
            ["Storage__LocalPath"] = Path.Combine(_tempRoot, "uploads"),
        };

        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        // Force host startup (runs migrations and seeding).
        _ = Server;
        await Task.CompletedTask;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public HttpClient CreateApiClient(string? accessToken = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Crypton-Client", "tests");
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public AsyncServiceScope Scope() => Services.CreateAsyncScope();

    public async Task<T> WithDbAsync<T>(Func<CryptonDbContext, Task<T>> work)
    {
        await using var scope = Scope();
        return await work(scope.ServiceProvider.GetRequiredService<CryptonDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        try
        {
            await using var connection = new NpgsqlConnection(ServerConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort cleanup.
        }

        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<CryptonFactory>
{
    public const string Name = "api";
}
