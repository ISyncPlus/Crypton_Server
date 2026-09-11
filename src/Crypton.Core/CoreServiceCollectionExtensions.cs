using Crypton.Core.Admin;
using Crypton.Core.Aml;
using Crypton.Core.Assets;
using Crypton.Core.Audit;
using Crypton.Core.Data;
using Crypton.Core.Fiat;
using Crypton.Core.Kyc;
using Crypton.Core.Ledger;
using Crypton.Core.Notifications;
using Crypton.Core.P2P;
using Crypton.Core.Pricing;
using Crypton.Core.Security;
using Crypton.Core.Settings;
using Crypton.Core.Storage;
using Crypton.Core.Trading;
using Crypton.Core.Wallets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Crypton.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddCryptonCore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddDbContext<CryptonDbContext>(options => DbConfig.Configure(options, connectionString));
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);

        services.Configure<AppOptions>(configuration.GetSection(AppOptions.Section));
        services.Configure<PricingOptions>(configuration.GetSection(PricingOptions.Section));
        services.Configure<BlockchainOptions>(configuration.GetSection(BlockchainOptions.Section));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.Configure<KycOptions>(configuration.GetSection(KycOptions.Section));
        services.Configure<ReportingOptions>(configuration.GetSection(ReportingOptions.Section));

        services.AddScoped<SettingsService>();
        services.AddScoped<AssetCatalog>();
        services.AddScoped<LedgerService>();
        services.AddScoped<AccountGuard>();
        services.AddScoped<TwoFactorService>();
        services.AddScoped<AuditService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<EmailOutboxProcessor>();
        services.AddScoped<LimitService>();
        services.AddScoped<AmlEngine>();
        services.AddScoped<TradeService>();
        services.AddScoped<ChainGatewayRegistry>();
        services.AddScoped<DepositAddressService>();
        services.AddScoped<DepositScanner>();
        services.AddScoped<CryptoWithdrawalService>();
        services.AddScoped<WithdrawalProcessor>();
        services.AddScoped<FiatService>();
        services.AddScoped<KycService>();
        services.AddScoped<TraderStatsService>();
        services.AddScoped<P2PAdService>();
        services.AddScoped<P2POrderService>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddScoped<TreasuryService>();
        services.AddScoped<LedgerCheckService>();
        services.AddScoped<AdminUserService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<ReportService>();

        services.AddSingleton<PriceCache>();
        services.AddSingleton<PriceService>();

        return services;
    }
}
