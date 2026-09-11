using Crypton.Api.Contracts;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Ledger;
using Crypton.Core.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Crypton.Tests.Infrastructure;

public sealed record TestUser(Guid Id, string Email, string Password, HttpClient Client, string? TotpKey)
{
    public string AccessToken => Client.DefaultRequestHeaders.Authorization?.Parameter ?? "";
}

public static class TestAccounts
{
    public const string Password = "Correct-Horse-Battery-9";

    /// <summary>Creates a confirmed user directly, optionally with KYC tier, 2FA and balances, and signs in through the API.</summary>
    public static async Task<TestUser> CreateAsync(
        CryptonFactory factory,
        int kycTier = 0,
        bool twoFactor = false,
        IReadOnlyDictionary<string, decimal>? balances = null,
        string[]? roles = null,
        string? firstName = null)
    {
        var email = $"user_{Guid.NewGuid():N}@test.crypton.local";
        Guid id;
        string? key = null;

        await using (var scope = factory.Scope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser
            {
                Id = Ids.New(),
                Email = email,
                UserName = email,
                EmailConfirmed = true,
                FirstName = firstName ?? "Test",
                LastName = "User",
                KycTier = kycTier,
                CreatedAt = factory.Clock.GetUtcNow(),
            };
            var created = await users.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            id = user.Id;

            if (twoFactor)
            {
                await users.ResetAuthenticatorKeyAsync(user);
                key = await users.GetAuthenticatorKeyAsync(user);
                await users.SetTwoFactorEnabledAsync(user, true);
            }

            foreach (var role in roles ?? [])
            {
                await users.AddToRoleAsync(user, role);
            }
        }

        if (balances is not null)
        {
            foreach (var (asset, amount) in balances)
            {
                await FundAsync(factory, id, asset, amount);
            }
        }

        var client = factory.CreateApiClient();
        var user1 = new TestUser(id, email, Password, client, key);
        await SignInAsync(factory, user1);
        return user1;
    }

    public static async Task SignInAsync(CryptonFactory factory, TestUser user)
    {
        user.Client.DefaultRequestHeaders.Authorization = null;
        var login = await user.Client.PostOk<AuthResponse>("/api/auth/login", new { email = user.Email, password = user.Password });
        if (login.RequiresTwoFactor)
        {
            login = await user.Client.PostOk<AuthResponse>("/api/auth/login/2fa", new { challengeToken = login.ChallengeToken, code = NextCode(factory, user) });
        }

        Assert.False(string.IsNullOrEmpty(login.AccessToken));
        user.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);
    }

    /// <summary>Returns a fresh TOTP code, advancing the fake clock to a new 30-second step so codes are never replayed.</summary>
    public static string NextCode(CryptonFactory factory, TestUser user)
    {
        Assert.NotNull(user.TotpKey);
        factory.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        return Totp.Compute(Base32.Decode(user.TotpKey!), Totp.TimeStep(factory.Clock.GetUtcNow()));
    }

    public static async Task FundAsync(CryptonFactory factory, Guid userId, string asset, decimal amount)
    {
        await using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<CryptonDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerService>();
        await db.InTransactionAsync(ct => ledger.PostAsync(
            new JournalBuilder(JournalTypes.AdminAdjustment)
                .ForUser(userId)
                .Idempotent($"test-fund:{Guid.NewGuid():N}")
                .System(SystemAccounts.Custody, asset, -amount)
                .User(userId, asset, AccountKind.Available, amount), ct), CancellationToken.None);
    }

    public static async Task FundTreasuryAsync(CryptonFactory factory, string asset, decimal amount)
    {
        await using var scope = factory.Scope();
        var db = scope.ServiceProvider.GetRequiredService<CryptonDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerService>();
        await db.InTransactionAsync(ct => ledger.PostAsync(
            new JournalBuilder(JournalTypes.TreasuryFunding)
                .Idempotent($"test-treasury:{Guid.NewGuid():N}")
                .System(SystemAccounts.Custody, asset, -amount)
                .System(SystemAccounts.Treasury, asset, amount), ct), CancellationToken.None);
    }

    public static async Task<decimal> AvailableAsync(CryptonFactory factory, Guid userId, string asset)
    {
        await using var scope = factory.Scope();
        return await scope.ServiceProvider.GetRequiredService<LedgerService>().GetUserBalanceAsync(userId, asset, AccountKind.Available);
    }

    public static async Task<decimal> BalanceAsync(CryptonFactory factory, Guid userId, string asset, AccountKind kind)
    {
        await using var scope = factory.Scope();
        return await scope.ServiceProvider.GetRequiredService<LedgerService>().GetUserBalanceAsync(userId, asset, kind);
    }

    public static async Task<decimal> SystemBalanceAsync(CryptonFactory factory, string code, string asset)
    {
        await using var scope = factory.Scope();
        return await scope.ServiceProvider.GetRequiredService<LedgerService>().GetSystemBalanceAsync(code, asset);
    }

    public static async Task<TestUser> AdminAsync(CryptonFactory factory)
    {
        var client = factory.CreateApiClient();
        var id = await factory.WithDbAsync(db => db.Users.Where(u => u.Email == CryptonFactory.AdminEmail).Select(u => u.Id).FirstAsync());
        var admin = new TestUser(id, CryptonFactory.AdminEmail, CryptonFactory.AdminPassword, client, null);
        await SignInAsync(factory, admin);
        return admin;
    }
}
