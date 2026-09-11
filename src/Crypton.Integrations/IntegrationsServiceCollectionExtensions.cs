using Crypton.Core.Aml;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Fiat;
using Crypton.Core.Kyc;
using Crypton.Core.Notifications;
using Crypton.Core.Pricing;
using Crypton.Core.Wallets;
using Crypton.Integrations.Blockchain;
using Crypton.Integrations.Compliance;
using Crypton.Integrations.Email;
using Crypton.Integrations.Kyc;
using Crypton.Integrations.Payments;
using Crypton.Integrations.Pricing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crypton.Integrations;

public sealed class PaymentsOptions
{
    public const string Section = "Payments";

    /// <summary>Simulated | Paystack</summary>
    public string Provider { get; set; } = "Simulated";
}

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Smtp | Log</summary>
    public string Provider { get; set; } = "Log";
}

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddCryptonIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BitcoinOptions>(configuration.GetSection(BitcoinOptions.Section));
        services.Configure<EthereumOptions>(configuration.GetSection(EthereumOptions.Section));
        services.Configure<SanctionsOracleOptions>(configuration.GetSection(SanctionsOracleOptions.Section));
        services.Configure<CoinGeckoOptions>(configuration.GetSection(CoinGeckoOptions.Section));
        services.Configure<PaystackOptions>(configuration.GetSection(PaystackOptions.Section));
        services.Configure<DojahOptions>(configuration.GetSection(DojahOptions.Section));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.Section));

        void Configure(HttpClient client, TimeSpan timeout)
        {
            client.Timeout = timeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Crypton/1.0");
        }

        services.AddHttpClient(BitcoinGateway.HttpClientName, c => Configure(c, TimeSpan.FromSeconds(30)));
        services.AddHttpClient(CoinGeckoPriceProvider.HttpClientName, c => Configure(c, TimeSpan.FromSeconds(15)));
        services.AddHttpClient(PaystackGateway.HttpClientName, c => Configure(c, TimeSpan.FromSeconds(30)));
        services.AddHttpClient(DojahKycVerifier.HttpClientName, c => Configure(c, TimeSpan.FromSeconds(30)));

        // Blockchain
        var blockchain = configuration.GetSection(BlockchainOptions.Section).Get<BlockchainOptions>() ?? new BlockchainOptions();
        if (blockchain.IsSimulated)
        {
            services.AddScoped<IChainGateway>(sp => new SimulatedChainGateway(Networks.Bitcoin, sp.GetRequiredService<CryptonDbContext>(), sp.GetRequiredService<TimeProvider>()));
            services.AddScoped<IChainGateway>(sp => new SimulatedChainGateway(Networks.Ethereum, sp.GetRequiredService<CryptonDbContext>(), sp.GetRequiredService<TimeProvider>()));
        }
        else
        {
            services.AddScoped<IChainGateway, BitcoinGateway>();
            services.AddScoped<IChainGateway, EthereumGateway>();
        }

        // Prices
        var pricing = configuration.GetSection(PricingOptions.Section).Get<PricingOptions>() ?? new PricingOptions();
        if (string.Equals(pricing.Provider, "CoinGecko", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IPriceProvider, CoinGeckoPriceProvider>();
        }
        else
        {
            services.AddSingleton<IPriceProvider, SimulatedPriceProvider>();
        }

        // Payments
        var payments = configuration.GetSection(PaymentsOptions.Section).Get<PaymentsOptions>() ?? new PaymentsOptions();
        if (string.Equals(payments.Provider, "Paystack", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IFiatGateway, PaystackGateway>();
        }
        else
        {
            services.AddScoped<SimulatedFiatGateway>();
            services.AddScoped<IFiatGateway>(sp => sp.GetRequiredService<SimulatedFiatGateway>());
        }

        // KYC
        var kyc = configuration.GetSection(KycOptions.Section).Get<KycOptions>() ?? new KycOptions();
        if (string.Equals(kyc.Provider, "Dojah", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IKycVerifier, DojahKycVerifier>();
        }
        else
        {
            services.AddScoped<IKycVerifier, ManualKycVerifier>();
        }

        // Compliance screening (the rule engine iterates every registered provider)
        services.AddSingleton<IAddressScreeningProvider, ChainalysisOracleScreening>();

        // Email
        var email = configuration.GetSection(EmailOptions.Section).Get<EmailOptions>() ?? new EmailOptions();
        if (string.Equals(email.Provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, LogEmailSender>();
        }

        return services;
    }
}
