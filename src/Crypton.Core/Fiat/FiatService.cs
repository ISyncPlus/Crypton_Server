using System.Text.Json;
using System.Text.RegularExpressions;
using Crypton.Core.Aml;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Kyc;
using Crypton.Core.Ledger;
using Crypton.Core.Notifications;
using Crypton.Core.Security;
using Crypton.Core.Settings;
using Crypton.Core.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crypton.Core.Fiat;

public sealed record FiatWithdrawalRequest(Guid BankAccountId, decimal Amount, string? TwoFactorCode, string? IdempotencyKey);

public sealed partial class FiatService(
    CryptonDbContext db,
    IFiatGateway gateway,
    LedgerService ledger,
    LimitService limits,
    AmlEngine aml,
    AccountGuard guard,
    TwoFactorService twoFactor,
    SettingsService settings,
    NotificationService notifications,
    AuditService audit,
    IMemoryCache cache,
    IOptions<AppOptions> app,
    TimeProvider clock,
    ILogger<FiatService> logger)
{
    private const string Currency = AssetCodes.NGN;

    public string ProviderName => gateway.Name;

    public bool IsSimulated => gateway.IsSimulated;

    // ---------------------------------------------------------------- banks

    public async Task<IReadOnlyList<BankInfo>> ListBanksAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        if (cache.TryGetValue("fiat:banks", out IReadOnlyList<BankInfo>? cached) && cached is not null)
        {
            return cached;
        }

        var banks = (await gateway.ListBanksAsync(ct)).OrderBy(b => b.Name).ToList();
        cache.Set("fiat:banks", (IReadOnlyList<BankInfo>)banks, TimeSpan.FromHours(12));
        return banks;
    }

    public async Task<ResolvedBankAccount> ResolveAccountAsync(string accountNumber, string bankCode, CancellationToken ct = default)
    {
        EnsureConfigured();
        accountNumber = (accountNumber ?? "").Trim();
        if (!NubanRegex().IsMatch(accountNumber))
        {
            throw AppException.Validation("Account numbers are 10 digits.");
        }

        var bank = (await ListBanksAsync(ct)).FirstOrDefault(b => b.Code == bankCode)
            ?? throw AppException.Validation("Select a valid bank.");

        try
        {
            return await gateway.ResolveAccountAsync(accountNumber, bank.Code, ct);
        }
        catch (FiatProviderException ex) when (ex.IsDefinitive)
        {
            throw AppException.Validation("We couldn't verify that account number with the bank. Check the details and try again.", "account_not_resolved");
        }
    }

    public async Task<BankAccount> AddBankAccountAsync(Guid userId, string accountNumber, string bankCode, AuditContext context, CancellationToken ct = default)
    {
        var user = await guard.RequireActiveUserAsync(userId, ct);
        var count = await db.BankAccounts.CountAsync(b => b.UserId == userId && !b.IsDeleted, ct);
        if (count >= 10)
        {
            throw AppException.Validation("You can save up to 10 bank accounts.");
        }

        var resolved = await ResolveAccountAsync(accountNumber, bankCode, ct);
        if (user.KycTier >= 1 && !gateway.IsSimulated && !NamesMatch(resolved.AccountName, user.FirstName, user.LastName))
        {
            throw AppException.Validation("The bank account name must match the name on your verified profile.", "account_name_mismatch");
        }

        var bank = (await ListBanksAsync(ct)).First(b => b.Code == bankCode);
        var duplicate = await db.BankAccounts.FirstOrDefaultAsync(b => b.UserId == userId && !b.IsDeleted && b.BankCode == bankCode && b.AccountNumber == resolved.AccountNumber, ct);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var account = new BankAccount
        {
            Id = Ids.New(),
            UserId = userId,
            BankCode = bank.Code,
            BankName = bank.Name,
            AccountNumber = resolved.AccountNumber,
            AccountName = resolved.AccountName,
            CreatedAt = clock.GetUtcNow(),
        };
        db.BankAccounts.Add(account);
        audit.Record(AuditActions.BankAccountAdded, userId, context, "bank_account", account.Id.ToString(), new { account.BankName, last4 = account.AccountNumber[^4..] });
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task RemoveBankAccountAsync(Guid userId, Guid bankAccountId, AuditContext context, CancellationToken ct = default)
    {
        var account = await db.BankAccounts.FirstOrDefaultAsync(b => b.Id == bankAccountId && b.UserId == userId && !b.IsDeleted, ct)
            ?? throw AppException.NotFound("Bank account");

        var usedByActiveAd = await db.P2PAds.AnyAsync(a => a.UserId == userId && a.Status != P2PAdStatus.Closed && a.PaymentMethodIds.Contains(bankAccountId), ct);
        if (usedByActiveAd)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "This account is used by an active P2P ad. Close or edit the ad first.");
        }

        account.IsDeleted = true;
        audit.Record(AuditActions.BankAccountRemoved, userId, context, "bank_account", account.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- deposits

    public async Task<FiatDeposit> InitiateDepositAsync(Guid userId, decimal amount, CancellationToken ct = default)
    {
        EnsureConfigured();
        var user = await guard.RequireActiveUserAsync(userId, ct);
        var cfg = await settings.GetAsync<FiatSettings>(ct);

        if (amount <= 0 || !MoneyMath.HasMaxDecimals(amount, 2))
        {
            throw new AppException(ErrorCodes.InvalidAmount, "Enter a valid naira amount.");
        }

        if (amount < cfg.MinDepositNgn)
        {
            throw new AppException(ErrorCodes.AmountTooSmall, $"The minimum deposit is NGN {cfg.MinDepositNgn:N0}.");
        }

        if (amount > cfg.MaxDepositNgn)
        {
            throw new AppException(ErrorCodes.AmountTooLarge, $"The maximum single deposit is NGN {cfg.MaxDepositNgn:N0}.");
        }

        await limits.EnsureWithinLimitAsync(userId, user.KycTier, LimitKind.FiatDeposit, amount, ct);

        var fee = Math.Min(MoneyMath.Ceil(MoneyMath.Bps(amount, cfg.DepositFeeBps), 2), cfg.DepositFeeCapNgn);
        var reference = $"crp_dep_{Ids.RandomToken(20)}";
        var now = clock.GetUtcNow();
        var deposit = new FiatDeposit
        {
            Id = Ids.New(),
            UserId = userId,
            Provider = gateway.Name,
            Reference = reference,
            Currency = Currency,
            Amount = amount,
            Fee = fee,
            Status = FiatDepositStatus.Initiated,
            CreatedAt = now,
        };

        var callback = EmailTemplates.Combine(app.Value.FrontendBaseUrl, $"/wallets/fiat/deposit/return?reference={reference}");
        PaymentInitResult init;
        try
        {
            init = await gateway.InitializePaymentAsync(
                new PaymentInitRequest(user.Email!, MinorUnits.ToMinor(amount), Currency, reference, callback,
                    new Dictionary<string, string> { ["user_id"] = userId.ToString(), ["deposit_id"] = deposit.Id.ToString() }),
                ct);
        }
        catch (FiatProviderException ex)
        {
            logger.LogWarning(ex, "Payment initialization failed for {Reference}", reference);
            throw new AppException(ErrorCodes.ProviderError, "The payment provider is unavailable right now. Please try again.", 502);
        }

        deposit.AuthorizationUrl = init.AuthorizationUrl;
        db.FiatDeposits.Add(deposit);
        await db.SaveChangesAsync(ct);
        return deposit;
    }

    /// <summary>
    /// Confirms a deposit with the provider and credits it exactly once. Safe to call from the webhook, the
    /// browser return page and the reconciliation job, in any order and any number of times.
    /// </summary>
    public async Task<FiatDeposit> ConfirmDepositAsync(string reference, CancellationToken ct = default)
    {
        var snapshot = await db.FiatDeposits.AsNoTracking().FirstOrDefaultAsync(d => d.Reference == reference, ct)
            ?? throw AppException.NotFound("Deposit");

        if (snapshot.Status != FiatDepositStatus.Initiated)
        {
            return snapshot;
        }

        PaymentVerification verification;
        try
        {
            verification = await gateway.VerifyPaymentAsync(reference, ct);
        }
        catch (FiatProviderException ex)
        {
            logger.LogWarning(ex, "Verification failed for deposit {Reference}", reference);
            return snapshot;
        }

        return await db.InTransactionAsync(async token =>
        {
            var deposit = (await db.FiatDeposits
                    .FromSqlInterpolated($"SELECT * FROM fiat_deposits WHERE reference = {reference} FOR UPDATE")
                    .ToListAsync(token))
                .Single();

            if (deposit.Status != FiatDepositStatus.Initiated)
            {
                return deposit;
            }

            var now = clock.GetUtcNow();
            switch (verification.Status)
            {
                case ProviderPaymentStatus.Success:
                    var paid = MinorUnits.FromMinor(verification.AmountMinor);
                    if (!string.Equals(verification.Currency, Currency, StringComparison.OrdinalIgnoreCase) || paid != deposit.Amount)
                    {
                        logger.LogError("Deposit {Reference} amount/currency mismatch: expected {Expected} {Currency}, provider reported {Paid} {ProviderCurrency}",
                            reference, deposit.Amount, Currency, paid, verification.Currency);
                        deposit.Status = FiatDepositStatus.Failed;
                        deposit.GatewayResponse = "Amount or currency mismatch; flagged for review.";
                        aml.QueueAlerts(new AmlDecision([new AmlFinding("payment_mismatch", AmlAction.Review, 80,
                            $"Paystack reported {paid} {verification.Currency} for a {deposit.Amount} NGN deposit.", new { reference })]),
                            deposit.UserId, AmlSubjectType.FiatDeposit, deposit.Id);
                        break;
                    }

                    var providerFee = MinorUnits.FromMinor(verification.FeesMinor);
                    var credit = deposit.Amount - deposit.Fee;
                    var entry = await ledger.PostAsync(
                        new JournalBuilder(JournalTypes.FiatDeposit)
                            .ForUser(deposit.UserId)
                            .Reference("fiat_deposit", deposit.Id)
                            .Idempotent($"fiat_deposit:{deposit.Reference}")
                            .Describe($"Naira deposit {deposit.Reference}")
                            .System(SystemAccounts.Custody, Currency, -(deposit.Amount - providerFee))
                            .System(SystemAccounts.PaymentCosts, Currency, -providerFee)
                            .System(SystemAccounts.Fees, Currency, deposit.Fee)
                            .User(deposit.UserId, Currency, AccountKind.Available, credit),
                        token);

                    deposit.Status = FiatDepositStatus.Succeeded;
                    deposit.ProviderFee = providerFee;
                    deposit.CompletedAt = verification.PaidAt ?? now;
                    deposit.ProviderTransactionId = verification.ProviderTransactionId;
                    deposit.GatewayResponse = Truncate(verification.GatewayResponse, 300);
                    deposit.JournalEntryId = entry.Id;

                    var decision = await aml.EvaluateDepositAsync(deposit.UserId, isFiat: true, deposit.Amount, token);
                    aml.QueueAlerts(decision, deposit.UserId, AmlSubjectType.FiatDeposit, deposit.Id);
                    await notifications.QueueAsync(deposit.UserId, NotificationTypes.Deposit, "Naira deposit received",
                        $"NGN {credit:N2} has been added to your wallet.", "/wallets", email: true, token);
                    break;

                case ProviderPaymentStatus.Failed:
                    deposit.Status = FiatDepositStatus.Failed;
                    deposit.CompletedAt = now;
                    deposit.GatewayResponse = Truncate(verification.GatewayResponse, 300);
                    break;

                case ProviderPaymentStatus.Abandoned when now - deposit.CreatedAt > TimeSpan.FromHours(2):
                    deposit.Status = FiatDepositStatus.Abandoned;
                    deposit.CompletedAt = now;
                    break;
            }

            await db.SaveChangesAsync(token);
            return deposit;
        }, ct);
    }

    // ---------------------------------------------------------------- withdrawals

    public async Task<FiatWithdrawal> RequestWithdrawalAsync(Guid userId, FiatWithdrawalRequest request, RequestContext context, CancellationToken ct = default)
    {
        EnsureConfigured();
        var user = await guard.RequireCanWithdrawAsync(userId, ct);
        var cfg = await settings.GetAsync<WithdrawalSettings>(ct);

        if (request.IdempotencyKey is { Length: > 64 })
        {
            throw AppException.Validation("idempotencyKey must be at most 64 characters.");
        }

        if (request.IdempotencyKey is not null)
        {
            var existing = await db.FiatWithdrawals.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == userId && w.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null)
            {
                return existing;
            }
        }

        var account = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BankAccountId && b.UserId == userId && !b.IsDeleted, ct)
            ?? throw AppException.NotFound("Bank account");

        if (request.Amount <= 0 || !MoneyMath.HasMaxDecimals(request.Amount, 2))
        {
            throw new AppException(ErrorCodes.InvalidAmount, "Enter a valid naira amount.");
        }

        if (request.Amount < cfg.MinFiatWithdrawalNgn)
        {
            throw new AppException(ErrorCodes.AmountTooSmall, $"The minimum withdrawal is NGN {cfg.MinFiatWithdrawalNgn:N0}.");
        }

        if (request.Amount > cfg.MaxFiatWithdrawalNgn)
        {
            throw new AppException(ErrorCodes.AmountTooLarge, $"The maximum single withdrawal is NGN {cfg.MaxFiatWithdrawalNgn:N0}.");
        }

        await limits.EnsureWithinLimitAsync(userId, user.KycTier, LimitKind.FiatWithdrawal, request.Amount, ct);

        var fee = cfg.FiatWithdrawalFeeNgn;
        var total = request.Amount + fee;
        var available = await ledger.GetUserBalanceAsync(userId, Currency, AccountKind.Available, ct);
        if (available < total)
        {
            throw new AppException(ErrorCodes.InsufficientFunds, $"Insufficient NGN balance. You need NGN {total:N2} including the NGN {fee:N2} fee.", 409);
        }

        await twoFactor.RequireAsync(userId, request.TwoFactorCode, cfg.RequireTwoFactor, ct);

        var id = Ids.New();
        var decision = await aml.EvaluateWithdrawalAsync(new WithdrawalRiskContext(userId, Currency, true, null, null, request.Amount, request.Amount, context.DeviceHash), ct);
        if (decision.Blocked)
        {
            aml.QueueAlerts(decision, userId, AmlSubjectType.FiatWithdrawal, null);
            await db.SaveChangesAsync(ct);
            throw AppException.Forbidden("This withdrawal cannot be processed. Contact support if you think this is a mistake.", ErrorCodes.AmlBlocked);
        }

        var needsReview = decision.NeedsReview || request.Amount > cfg.ManualReviewAboveNgn;
        var now = clock.GetUtcNow();

        var withdrawal = await db.InTransactionAsync(async token =>
        {
            await ledger.PostAsync(
                new JournalBuilder(JournalTypes.FiatWithdrawalHold)
                    .ForUser(userId)
                    .Reference("fiat_withdrawal", id)
                    .Idempotent($"fiat_withdrawal:{id}:hold")
                    .User(userId, Currency, AccountKind.Available, -total)
                    .User(userId, Currency, AccountKind.WithdrawalHold, total),
                token);

            var entity = new FiatWithdrawal
            {
                Id = id,
                UserId = userId,
                BankAccountId = account.Id,
                Provider = gateway.Name,
                Currency = Currency,
                Amount = request.Amount,
                Fee = fee,
                Reference = $"crp_wd_{Ids.RandomToken(24)}",
                IdempotencyKey = request.IdempotencyKey,
                Status = needsReview ? FiatWithdrawalStatus.PendingReview : FiatWithdrawalStatus.Approved,
                RiskSummary = needsReview && decision.Summary is null ? "Above the manual review threshold." : decision.Summary,
                BankName = account.BankName,
                AccountNumber = account.AccountNumber,
                AccountName = account.AccountName,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.FiatWithdrawals.Add(entity);
            aml.QueueAlerts(decision, userId, AmlSubjectType.FiatWithdrawal, id);
            audit.Record(AuditActions.WithdrawalRequested, userId, new AuditContext(userId, context.IpAddress), "fiat_withdrawal", id.ToString(),
                new { entity.Amount, entity.BankName, last4 = entity.AccountNumber[^4..], entity.Status });
            await notifications.QueueAsync(userId, NotificationTypes.Withdrawal,
                needsReview ? "Naira withdrawal under review" : "Naira withdrawal requested",
                needsReview
                    ? $"Your withdrawal of NGN {request.Amount:N2} is being reviewed."
                    : $"Your withdrawal of NGN {request.Amount:N2} to {account.BankName} ••{account.AccountNumber[^4..]} is being processed.",
                "/wallets/withdrawals", email: true, token);
            await db.SaveChangesAsync(token);
            return entity;
        }, ct);

        if (needsReview)
        {
            await notifications.NotifyStaffAsync([Roles.Admin, Roles.Compliance], "Naira withdrawal needs review",
                $"NGN {withdrawal.Amount:N2}. {withdrawal.RiskSummary}", "/admin/withdrawals", ct);
        }

        return withdrawal;
    }

    public async Task<FiatWithdrawal> CancelWithdrawalAsync(Guid userId, Guid id, CancellationToken ct = default) =>
        await db.InTransactionAsync(async token =>
        {
            var w = await LockWithdrawalAsync(id, token);
            if (w is null || w.UserId != userId)
            {
                throw AppException.NotFound("Withdrawal");
            }

            if (w.Status is not (FiatWithdrawalStatus.PendingReview or FiatWithdrawalStatus.Approved))
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "This withdrawal can no longer be cancelled.");
            }

            await ReleaseHoldAsync(w, FiatWithdrawalStatus.Cancelled, "Cancelled by user", token);
            audit.Record(AuditActions.WithdrawalCancelled, userId, new AuditContext(userId, null), "fiat_withdrawal", id.ToString());
            await db.SaveChangesAsync(token);
            return w;
        }, ct);

    public async Task<FiatWithdrawal> ApproveWithdrawalAsync(Guid adminId, Guid id, string? note, CancellationToken ct = default) =>
        await db.InTransactionAsync(async token =>
        {
            var w = await LockWithdrawalAsync(id, token) ?? throw AppException.NotFound("Withdrawal");
            if (w.Status != FiatWithdrawalStatus.PendingReview)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, $"Only withdrawals pending review can be approved (current: {w.Status}).");
            }

            var now = clock.GetUtcNow();
            w.Status = FiatWithdrawalStatus.Approved;
            w.ReviewedBy = adminId;
            w.ReviewedAt = now;
            w.ReviewNote = note;
            w.UpdatedAt = now;
            audit.Record(AuditActions.AdminWithdrawalApproved, w.UserId, new AuditContext(adminId, null), "fiat_withdrawal", id.ToString(), new { note });
            await db.SaveChangesAsync(token);
            return w;
        }, ct);

    public async Task<FiatWithdrawal> RejectWithdrawalAsync(Guid adminId, Guid id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw AppException.Validation("A reason is required.");
        }

        return await db.InTransactionAsync(async token =>
        {
            var w = await LockWithdrawalAsync(id, token) ?? throw AppException.NotFound("Withdrawal");
            if (w.Status is not (FiatWithdrawalStatus.PendingReview or FiatWithdrawalStatus.Approved or FiatWithdrawalStatus.NeedsAttention))
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, $"This withdrawal can no longer be rejected (current: {w.Status}).");
            }

            if (w.Status == FiatWithdrawalStatus.NeedsAttention)
            {
                // Make sure the provider never paid it before returning funds.
                var check = await SafeVerifyTransferAsync(w.Reference, token);
                if (check.Status is ProviderTransferStatus.Success or ProviderTransferStatus.Pending or ProviderTransferStatus.OtpRequired)
                {
                    throw AppException.Conflict(ErrorCodes.InvalidState, $"The provider reports this transfer as {check.Status}; it cannot be rejected.");
                }
            }

            w.ReviewedBy = adminId;
            w.ReviewedAt = clock.GetUtcNow();
            w.ReviewNote = reason;
            await ReleaseHoldAsync(w, FiatWithdrawalStatus.Rejected, reason, token);
            audit.Record(AuditActions.AdminWithdrawalRejected, w.UserId, new AuditContext(adminId, null), "fiat_withdrawal", id.ToString(), new { reason });
            await notifications.QueueAsync(w.UserId, NotificationTypes.Withdrawal, "Naira withdrawal rejected",
                $"Your withdrawal of NGN {w.Amount:N2} was rejected and the funds were returned. Reason: {reason}", "/wallets/withdrawals", email: true, token);
            await db.SaveChangesAsync(token);
            return w;
        }, ct);
    }

    /// <summary>Submits approved withdrawals to the provider. Called by the payout job.</summary>
    public async Task ProcessApprovedWithdrawalsAsync(CancellationToken ct)
    {
        if (!gateway.IsConfigured)
        {
            return;
        }

        var ids = await db.FiatWithdrawals.AsNoTracking()
            .Where(w => w.Status == FiatWithdrawalStatus.Approved)
            .OrderBy(w => w.CreatedAt)
            .Select(w => w.Id)
            .Take(20)
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            try
            {
                await SubmitAsync(id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Submitting fiat withdrawal {WithdrawalId} failed", id);
            }
        }
    }

    private async Task SubmitAsync(Guid id, CancellationToken ct)
    {
        var w = await db.FiatWithdrawals.FirstAsync(x => x.Id == id, ct);
        if (w.Status != FiatWithdrawalStatus.Approved)
        {
            return;
        }

        var account = await db.BankAccounts.FirstAsync(b => b.Id == w.BankAccountId, ct);
        if (account.RecipientCode is null)
        {
            try
            {
                account.RecipientCode = await gateway.CreateTransferRecipientAsync(account.AccountName, account.AccountNumber, account.BankCode, Currency, ct);
                await db.SaveChangesAsync(ct);
            }
            catch (FiatProviderException ex)
            {
                w.FailureReason = Truncate($"Could not register bank account with provider: {ex.Message}", 500);
                w.SubmitAttempts++;
                if (ex.IsDefinitive || w.SubmitAttempts >= 5)
                {
                    await ReleaseHoldAsync(w, FiatWithdrawalStatus.Failed, w.FailureReason, ct);
                }

                await db.SaveChangesAsync(ct);
                return;
            }
        }

        // Mark as processing first; the reference makes the provider call idempotent if we crash after sending.
        w.Status = FiatWithdrawalStatus.Processing;
        w.SubmitAttempts++;
        w.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        TransferResult result;
        try
        {
            result = await gateway.InitiateTransferAsync(
                new TransferRequest(MinorUnits.ToMinor(w.Amount), Currency, account.RecipientCode, w.Reference, "Crypton withdrawal"), ct);
        }
        catch (FiatProviderException ex) when (ex.IsDefinitive)
        {
            // Definitive refusal (validation, insufficient balance...). Confirm nothing exists, then return funds.
            var check = await SafeVerifyTransferAsync(w.Reference, ct);
            if (check.Status is ProviderTransferStatus.NotFound or ProviderTransferStatus.Failed)
            {
                await db.InTransactionAsync(async token =>
                {
                    var locked = await LockWithdrawalAsync(id, token);
                    if (locked is { Status: FiatWithdrawalStatus.Processing })
                    {
                        await ReleaseHoldAsync(locked, FiatWithdrawalStatus.Failed, Truncate(ex.Message, 500), token);
                        await notifications.QueueAsync(locked.UserId, NotificationTypes.Withdrawal, "Naira withdrawal failed",
                            $"Your withdrawal of NGN {locked.Amount:N2} could not be completed and the funds are back in your wallet.", "/wallets/withdrawals", email: true, token);
                        await db.SaveChangesAsync(token);
                    }
                }, ct);
            }
            else
            {
                await ApplyTransferStatusAsync(w.Reference, check, ct);
            }

            return;
        }
        catch (FiatProviderException ex)
        {
            // Ambiguous: leave in Processing; reconciliation will verify by reference.
            logger.LogWarning(ex, "Transfer {Reference} outcome unknown", w.Reference);
            return;
        }

        await ApplyTransferStatusAsync(w.Reference, result, ct);
    }

    /// <summary>Checks in-flight withdrawals and old initiated deposits with the provider.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (!gateway.IsConfigured)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var processing = await db.FiatWithdrawals.AsNoTracking()
            .Where(w => w.Status == FiatWithdrawalStatus.Processing && w.UpdatedAt < now.AddSeconds(gateway.IsSimulated ? -2 : -60))
            .OrderBy(w => w.UpdatedAt)
            .Select(w => w.Reference)
            .Take(50)
            .ToListAsync(ct);

        foreach (var reference in processing)
        {
            var status = await SafeVerifyTransferAsync(reference, ct);
            if (status.Status == ProviderTransferStatus.NotFound)
            {
                var w = await db.FiatWithdrawals.FirstAsync(x => x.Reference == reference, ct);
                if (now - w.UpdatedAt > TimeSpan.FromMinutes(30))
                {
                    w.Status = FiatWithdrawalStatus.NeedsAttention;
                    w.FailureReason = "Provider has no record of this transfer 30 minutes after submission.";
                    w.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                }

                continue;
            }

            await ApplyTransferStatusAsync(reference, status, ct);
        }

        var staleDeposits = await db.FiatDeposits.AsNoTracking()
            .Where(d => d.Status == FiatDepositStatus.Initiated && d.CreatedAt < now.AddMinutes(-2) && d.CreatedAt > now.AddDays(-2))
            .OrderBy(d => d.CreatedAt)
            .Select(d => d.Reference)
            .Take(50)
            .ToListAsync(ct);

        foreach (var reference in staleDeposits)
        {
            await ConfirmDepositAsync(reference, ct);
        }

        await db.FiatDeposits
            .Where(d => d.Status == FiatDepositStatus.Initiated && d.CreatedAt <= now.AddDays(-2))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, FiatDepositStatus.Abandoned).SetProperty(d => d.CompletedAt, now), ct);
    }

    // ---------------------------------------------------------------- webhooks

    public bool VerifyWebhookSignature(ReadOnlySpan<byte> body, string? signature) => gateway.VerifyWebhookSignature(body, signature);

    public async Task HandleWebhookAsync(JsonElement payload, CancellationToken ct)
    {
        var eventName = payload.TryGetProperty("event", out var e) ? e.GetString() : null;
        if (!payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var reference = data.TryGetProperty("reference", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        if (string.IsNullOrEmpty(reference))
        {
            return;
        }

        switch (eventName)
        {
            case "charge.success":
                if (await db.FiatDeposits.AsNoTracking().AnyAsync(d => d.Reference == reference, ct))
                {
                    await ConfirmDepositAsync(reference, ct);
                }

                break;

            case "transfer.success":
            case "transfer.failed":
            case "transfer.reversed":
                if (await db.FiatWithdrawals.AsNoTracking().AnyAsync(w => w.Reference == reference, ct))
                {
                    // Never trust the webhook body for money: re-verify with the provider.
                    var status = await SafeVerifyTransferAsync(reference, ct);
                    await ApplyTransferStatusAsync(reference, status, ct);
                }

                break;
        }
    }

    // ---------------------------------------------------------------- internals

    private async Task ApplyTransferStatusAsync(string reference, TransferResult result, CancellationToken ct)
    {
        await db.InTransactionAsync(async token =>
        {
            var w = (await db.FiatWithdrawals.FromSqlInterpolated($"SELECT * FROM fiat_withdrawals WHERE reference = {reference} FOR UPDATE").ToListAsync(token)).SingleOrDefault();
            if (w is null)
            {
                return;
            }

            var now = clock.GetUtcNow();
            w.TransferCode = result.TransferCode ?? w.TransferCode;

            switch (result.Status)
            {
                case ProviderTransferStatus.Success when w.Status is FiatWithdrawalStatus.Processing or FiatWithdrawalStatus.NeedsAttention:
                    await ledger.PostAsync(
                        new JournalBuilder(JournalTypes.FiatWithdrawalSettle)
                            .ForUser(w.UserId)
                            .Reference("fiat_withdrawal", w.Id)
                            .Idempotent($"fiat_withdrawal:{w.Id}:settle")
                            .Describe($"Naira withdrawal {w.Reference}")
                            .User(w.UserId, Currency, AccountKind.WithdrawalHold, -(w.Amount + w.Fee))
                            .System(SystemAccounts.Custody, Currency, w.Amount)
                            .System(SystemAccounts.Fees, Currency, w.Fee),
                        token);
                    w.Status = FiatWithdrawalStatus.Succeeded;
                    w.CompletedAt = now;
                    w.FailureReason = null;
                    await notifications.QueueAsync(w.UserId, NotificationTypes.Withdrawal, "Naira withdrawal completed",
                        $"NGN {w.Amount:N2} was sent to {w.BankName} ••{w.AccountNumber[^4..]}.", "/wallets/withdrawals", email: true, token);
                    break;

                case ProviderTransferStatus.Failed when w.Status is FiatWithdrawalStatus.Processing or FiatWithdrawalStatus.NeedsAttention:
                case ProviderTransferStatus.Reversed when w.Status is FiatWithdrawalStatus.Processing or FiatWithdrawalStatus.NeedsAttention:
                    await ReleaseHoldAsync(w, result.Status == ProviderTransferStatus.Reversed ? FiatWithdrawalStatus.Reversed : FiatWithdrawalStatus.Failed,
                        Truncate(result.Message ?? $"Transfer {result.Status.ToString().ToLowerInvariant()}", 500), token);
                    await notifications.QueueAsync(w.UserId, NotificationTypes.Withdrawal, "Naira withdrawal failed",
                        $"Your withdrawal of NGN {w.Amount:N2} could not be completed and the funds are back in your wallet.", "/wallets/withdrawals", email: true, token);
                    break;

                case ProviderTransferStatus.Reversed when w.Status == FiatWithdrawalStatus.Succeeded:
                    // Paid out, then returned by the bank: give the user their money back.
                    await ledger.PostAsync(
                        new JournalBuilder(JournalTypes.FiatWithdrawalReversal)
                            .ForUser(w.UserId)
                            .Reference("fiat_withdrawal", w.Id)
                            .Idempotent($"fiat_withdrawal:{w.Id}:reversal")
                            .Describe($"Reversal of naira withdrawal {w.Reference}")
                            .System(SystemAccounts.Custody, Currency, -w.Amount)
                            .System(SystemAccounts.Fees, Currency, -w.Fee)
                            .User(w.UserId, Currency, AccountKind.Available, w.Amount + w.Fee),
                        token);
                    w.Status = FiatWithdrawalStatus.Reversed;
                    w.FailureReason = "Transfer was reversed by the bank.";
                    await notifications.QueueAsync(w.UserId, NotificationTypes.Withdrawal, "Naira withdrawal reversed",
                        $"Your withdrawal of NGN {w.Amount:N2} was returned by the bank and credited back to your wallet.", "/wallets/withdrawals", email: true, token);
                    break;

                case ProviderTransferStatus.OtpRequired when w.Status == FiatWithdrawalStatus.Processing:
                    w.Status = FiatWithdrawalStatus.NeedsAttention;
                    w.FailureReason = "The payment provider requires an OTP. Disable transfer OTP in the Paystack dashboard for automated payouts, then finalize this transfer there.";
                    break;
            }

            w.UpdatedAt = now;
            await db.SaveChangesAsync(token);
        }, ct);
    }

    private async Task ReleaseHoldAsync(FiatWithdrawal w, FiatWithdrawalStatus status, string reason, CancellationToken ct)
    {
        await ledger.PostAsync(
            new JournalBuilder(JournalTypes.FiatWithdrawalRelease)
                .ForUser(w.UserId)
                .Reference("fiat_withdrawal", w.Id)
                .Idempotent($"fiat_withdrawal:{w.Id}:release")
                .Describe(reason)
                .User(w.UserId, Currency, AccountKind.WithdrawalHold, -(w.Amount + w.Fee))
                .User(w.UserId, Currency, AccountKind.Available, w.Amount + w.Fee),
            ct);
        w.Status = status;
        if (status is FiatWithdrawalStatus.Failed or FiatWithdrawalStatus.Reversed)
        {
            w.FailureReason = reason;
        }

        w.UpdatedAt = clock.GetUtcNow();
    }

    private async Task<TransferResult> SafeVerifyTransferAsync(string reference, CancellationToken ct)
    {
        try
        {
            return await gateway.VerifyTransferAsync(reference, ct);
        }
        catch (FiatProviderException ex)
        {
            return new TransferResult(ProviderTransferStatus.Pending, null, ex.Message);
        }
    }

    private async Task<FiatWithdrawal?> LockWithdrawalAsync(Guid id, CancellationToken ct) =>
        (await db.FiatWithdrawals.FromSqlInterpolated($"SELECT * FROM fiat_withdrawals WHERE id = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();

    private void EnsureConfigured()
    {
        if (!gateway.IsConfigured)
        {
            throw new AppException(ErrorCodes.NotConfigured, "Naira payments are not configured yet.", 503);
        }
    }

    internal static bool NamesMatch(string accountName, string firstName, string lastName)
    {
        static HashSet<string> Tokens(string s) =>
            Regex.Split(s.ToUpperInvariant(), "[^A-Z]+").Where(t => t.Length > 1).ToHashSet();

        var account = Tokens(accountName);
        var first = Tokens(firstName);
        var last = Tokens(lastName);
        return first.Overlaps(account) && last.Overlaps(account);
    }

    private static string Truncate(string? value, int max) => value is null ? "" : value.Length <= max ? value : value[..max];

    [GeneratedRegex("^[0-9]{10}$")]
    private static partial Regex NubanRegex();
}
