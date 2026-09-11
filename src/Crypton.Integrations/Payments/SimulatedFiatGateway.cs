using System.Security.Cryptography;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Fiat;
using Crypton.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Crypton.Integrations.Payments;

/// <summary>Stand-in for Paystack in development, demos and tests. No real money moves.</summary>
public sealed class SimulatedFiatGateway(CryptonDbContext db, IOptions<AppOptions> app, TimeProvider clock) : IFiatGateway
{
    private static readonly BankInfo[] Banks =
    [
        new("044", "Access Bank", "access-bank"),
        new("023", "Citibank Nigeria", "citibank-nigeria"),
        new("050", "Ecobank Nigeria", "ecobank-nigeria"),
        new("070", "Fidelity Bank", "fidelity-bank"),
        new("011", "First Bank of Nigeria", "first-bank-of-nigeria"),
        new("214", "First City Monument Bank", "first-city-monument-bank"),
        new("058", "Guaranty Trust Bank", "guaranty-trust-bank"),
        new("082", "Keystone Bank", "keystone-bank"),
        new("50211", "Kuda Bank", "kuda-bank"),
        new("50515", "Moniepoint MFB", "moniepoint-mfb-ng"),
        new("999992", "OPay Digital Services Limited (OPay)", "paycom"),
        new("999991", "PalmPay", "palmpay"),
        new("076", "Polaris Bank", "polaris-bank"),
        new("101", "Providus Bank", "providus-bank"),
        new("221", "Stanbic IBTC Bank", "stanbic-ibtc-bank"),
        new("232", "Sterling Bank", "sterling-bank"),
        new("032", "Union Bank of Nigeria", "union-bank-of-nigeria"),
        new("033", "United Bank For Africa", "united-bank-for-africa"),
        new("035", "Wema Bank", "wema-bank"),
        new("057", "Zenith Bank", "zenith-bank"),
    ];

    public string Name => "simulated";

    public bool IsSimulated => true;

    public bool IsConfigured => true;

    public Task<IReadOnlyList<BankInfo>> ListBanksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BankInfo>>(Banks);

    public Task<ResolvedBankAccount> ResolveAccountAsync(string accountNumber, string bankCode, CancellationToken ct)
    {
        if (accountNumber.StartsWith("000", StringComparison.Ordinal))
        {
            throw new FiatProviderException("Could not resolve account name.", isDefinitive: true);
        }

        return Task.FromResult(new ResolvedBankAccount(accountNumber, $"DEMO ACCOUNT {accountNumber[^4..]}", bankCode));
    }

    public async Task<PaymentInitResult> InitializePaymentAsync(PaymentInitRequest request, CancellationToken ct)
    {
        db.SimulatedPayments.Add(new SimulatedPayment
        {
            Reference = request.Reference,
            Kind = SimulatedPaymentKind.Charge,
            AmountMinor = request.AmountMinor,
            Status = "pending",
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        var url = EmailTemplates.Combine(app.Value.FrontendBaseUrl, $"/wallets/fiat/simulated-checkout?reference={Uri.EscapeDataString(request.Reference)}");
        return new PaymentInitResult(url, request.Reference, null);
    }

    public async Task<PaymentVerification> VerifyPaymentAsync(string reference, CancellationToken ct)
    {
        var payment = await db.SimulatedPayments.AsNoTracking().FirstOrDefaultAsync(p => p.Reference == reference && p.Kind == SimulatedPaymentKind.Charge, ct);
        if (payment is null)
        {
            return new PaymentVerification(reference, ProviderPaymentStatus.Pending, 0, "NGN", 0, null, "Unknown reference", null);
        }

        var status = payment.Status switch
        {
            "success" => ProviderPaymentStatus.Success,
            "failed" => ProviderPaymentStatus.Failed,
            "abandoned" => ProviderPaymentStatus.Abandoned,
            _ => ProviderPaymentStatus.Pending,
        };

        // Mimic Paystack's local card pricing: 1.5% capped at NGN 2,000.
        var fees = Math.Min((long)Math.Ceiling(payment.AmountMinor * 0.015m), 200_000L);
        return new PaymentVerification(reference, status, payment.AmountMinor, "NGN", status == ProviderPaymentStatus.Success ? fees : 0,
            "sim_" + reference, status == ProviderPaymentStatus.Success ? "Approved (simulated)" : payment.Status, payment.CompletedAt);
    }

    public Task<string> CreateTransferRecipientAsync(string accountName, string accountNumber, string bankCode, string currency, CancellationToken ct) =>
        Task.FromResult("RCP_sim_" + Ids.RandomToken(12));

    public async Task<TransferResult> InitiateTransferAsync(TransferRequest request, CancellationToken ct)
    {
        var existing = await db.SimulatedPayments.FirstOrDefaultAsync(p => p.Reference == request.Reference, ct);
        if (existing is not null)
        {
            return ToTransferResult(existing);
        }

        var payment = new SimulatedPayment
        {
            Reference = request.Reference,
            Kind = SimulatedPaymentKind.Transfer,
            AmountMinor = request.AmountMinor,
            RecipientCode = request.RecipientCode,
            Status = "pending",
            CreatedAt = clock.GetUtcNow(),
        };
        db.SimulatedPayments.Add(payment);
        await db.SaveChangesAsync(ct);
        return ToTransferResult(payment);
    }

    public async Task<TransferResult> VerifyTransferAsync(string reference, CancellationToken ct)
    {
        var payment = await db.SimulatedPayments.FirstOrDefaultAsync(p => p.Reference == reference && p.Kind == SimulatedPaymentKind.Transfer, ct);
        if (payment is null)
        {
            return new TransferResult(ProviderTransferStatus.NotFound, null, "Transfer not found");
        }

        // Simulated banks settle a couple of seconds after submission.
        if (payment.Status == "pending" && clock.GetUtcNow() - payment.CreatedAt >= TimeSpan.FromSeconds(2))
        {
            payment.Status = "success";
            payment.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        return ToTransferResult(payment);
    }

    public bool VerifyWebhookSignature(ReadOnlySpan<byte> body, string? signature) => false;

    /// <summary>Completes a simulated checkout, as if the customer paid (or declined) on the provider page.</summary>
    public async Task<bool> CompleteChargeAsync(string reference, bool success, CancellationToken ct)
    {
        var payment = await db.SimulatedPayments.FirstOrDefaultAsync(p => p.Reference == reference && p.Kind == SimulatedPaymentKind.Charge, ct);
        if (payment is null || payment.Status != "pending")
        {
            return false;
        }

        payment.Status = success ? "success" : "failed";
        payment.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static TransferResult ToTransferResult(SimulatedPayment payment) => payment.Status switch
    {
        "success" => new TransferResult(ProviderTransferStatus.Success, "TRF_sim_" + payment.Reference[^8..], "success"),
        "failed" => new TransferResult(ProviderTransferStatus.Failed, "TRF_sim_" + payment.Reference[^8..], "failed"),
        _ => new TransferResult(ProviderTransferStatus.Pending, "TRF_sim_" + payment.Reference[^8..], "pending"),
    };
}
