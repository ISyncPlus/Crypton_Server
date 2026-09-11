using Crypton.Core.Assets;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Ledger;
using Crypton.Core.Wallets;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crypton.Api.Seeding;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>Creates this admin account on startup if it does not exist.</summary>
    public string AdminEmail { get; set; } = "";

    public string AdminPassword { get; set; } = "";

    /// <summary>Simulated mode only: fund the treasury and create demo users.</summary>
    public bool DemoData { get; set; }

    public string DemoPassword { get; set; } = "Demo-Password-2026";
}

public sealed class DbSeeder(
    CryptonDbContext db,
    UserManager<AppUser> users,
    RoleManager<AppRole> roles,
    LedgerService ledger,
    AssetCatalog catalog,
    IOptions<SeedOptions> seed,
    IOptions<BlockchainOptions> blockchain,
    TimeProvider clock,
    ILogger<DbSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct)
    {
        await SeedRolesAsync();
        await SeedAssetsAsync(ct);
        await SeedAdminAsync();
        if (seed.Value.DemoData && blockchain.Value.IsSimulated)
        {
            await SeedDemoAsync(ct);
        }
    }

    private async Task SeedRolesAsync()
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new AppRole(role));
            }
        }
    }

    private async Task SeedAssetsAsync(CancellationToken ct)
    {
        var simulated = blockchain.Value.IsSimulated;
        var now = clock.GetUtcNow();
        var defaults = new[]
        {
            new Asset { Code = AssetCodes.BTC, Name = "Bitcoin", Precision = 8, ChainDecimals = 8, Network = Networks.Bitcoin, RequiredConfirmations = 2, MinDeposit = 0.0001m, MinWithdrawal = 0.0002m, WithdrawalFee = 0.00005m, SortOrder = 1 },
            new Asset { Code = AssetCodes.ETH, Name = "Ethereum", Precision = 8, ChainDecimals = 18, Network = Networks.Ethereum, RequiredConfirmations = simulated ? 3 : 12, MinDeposit = 0.002m, MinWithdrawal = 0.005m, WithdrawalFee = 0.001m, SortOrder = 2 },
            new Asset { Code = AssetCodes.USDT, Name = "Tether USD", Precision = 6, ChainDecimals = 6, Network = Networks.Ethereum, RequiredConfirmations = simulated ? 3 : 12, MinDeposit = 1m, MinWithdrawal = 10m, WithdrawalFee = 3m, SortOrder = 3 },
            new Asset { Code = AssetCodes.NGN, Name = "Nigerian Naira", IsFiat = true, Precision = 2, ChainDecimals = 2, Network = null, RequiredConfirmations = 0, MinDeposit = 1_000m, MinWithdrawal = 1_000m, WithdrawalFee = 50m, SortOrder = 4 },
        };

        var existing = await db.Assets.Select(a => a.Code).ToListAsync(ct);
        foreach (var asset in defaults.Where(a => !existing.Contains(a.Code)))
        {
            asset.UpdatedAt = now;
            db.Assets.Add(asset);
        }

        await db.SaveChangesAsync(ct);
        catalog.Invalidate();
    }

    private async Task SeedAdminAsync()
    {
        var o = seed.Value;
        if (string.IsNullOrWhiteSpace(o.AdminEmail) || string.IsNullOrWhiteSpace(o.AdminPassword))
        {
            return;
        }

        var admin = await users.FindByEmailAsync(o.AdminEmail);
        if (admin is null)
        {
            admin = new AppUser
            {
                Id = Ids.New(),
                UserName = o.AdminEmail,
                Email = o.AdminEmail,
                EmailConfirmed = true,
                FirstName = "Platform",
                LastName = "Admin",
                DisplayName = "crypton_admin",
                CreatedAt = clock.GetUtcNow(),
            };
            var result = await users.CreateAsync(admin, o.AdminPassword);
            if (!result.Succeeded)
            {
                logger.LogError("Could not create seed admin: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }

            logger.LogWarning("Created admin account {Email}. Sign in, enable 2FA and change the password.", o.AdminEmail);
        }

        if (!await users.IsInRoleAsync(admin, Roles.Admin))
        {
            await users.AddToRoleAsync(admin, Roles.Admin);
        }
    }

    private async Task SeedDemoAsync(CancellationToken ct)
    {
        var inventory = new Dictionary<string, decimal>
        {
            [AssetCodes.BTC] = 25m,
            [AssetCodes.ETH] = 600m,
            [AssetCodes.USDT] = 2_000_000m,
            [AssetCodes.NGN] = 3_000_000_000m,
        };

        foreach (var (asset, amount) in inventory)
        {
            await PostOnceAsync(new JournalBuilder(JournalTypes.TreasuryFunding)
                .Idempotent($"seed:treasury:{asset}")
                .Describe("Demo treasury inventory (simulated mode)")
                .System(SystemAccounts.Custody, asset, -amount)
                .System(SystemAccounts.Treasury, asset, amount), ct);
        }

        var demoUsers = new[]
        {
            (Email: "ada@demo.crypton.local", First: "Ada", Last: "Okafor", Display: "ada_trades", Tier: 1, Balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 2_500_000m, [AssetCodes.USDT] = 1_500m, [AssetCodes.BTC] = 0.05m }),
            (Email: "tunde@demo.crypton.local", First: "Tunde", Last: "Balogun", Display: "tunde_otc", Tier: 2, Balances: new Dictionary<string, decimal> { [AssetCodes.NGN] = 8_000_000m, [AssetCodes.USDT] = 12_000m, [AssetCodes.ETH] = 2.5m }),
        };

        foreach (var demo in demoUsers)
        {
            var user = await users.FindByEmailAsync(demo.Email);
            if (user is null)
            {
                user = new AppUser
                {
                    Id = Ids.New(),
                    UserName = demo.Email,
                    Email = demo.Email,
                    EmailConfirmed = true,
                    FirstName = demo.First,
                    LastName = demo.Last,
                    DisplayName = demo.Display,
                    KycTier = demo.Tier,
                    CreatedAt = clock.GetUtcNow(),
                };
                var result = await users.CreateAsync(user, seed.Value.DemoPassword);
                if (!result.Succeeded)
                {
                    logger.LogError("Could not create demo user {Email}: {Errors}", demo.Email, string.Join("; ", result.Errors.Select(e => e.Description)));
                    continue;
                }
            }

            foreach (var (asset, amount) in demo.Balances)
            {
                await PostOnceAsync(new JournalBuilder(JournalTypes.AdminAdjustment)
                    .ForUser(user.Id)
                    .Idempotent($"seed:demo:{demo.Email}:{asset}")
                    .Describe("Demo starting balance (simulated mode)")
                    .System(SystemAccounts.Custody, asset, -amount)
                    .User(user.Id, asset, AccountKind.Available, amount), ct);
            }
        }
    }

    private async Task PostOnceAsync(JournalBuilder journal, CancellationToken ct)
    {
        if (await db.JournalEntries.AnyAsync(j => j.IdempotencyKey == journal.IdempotencyKey, ct))
        {
            return;
        }

        await db.InTransactionAsync(token => ledger.PostAsync(journal, token), ct);
    }
}
