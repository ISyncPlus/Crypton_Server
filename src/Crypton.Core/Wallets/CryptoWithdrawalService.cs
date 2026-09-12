using Crypton.Core.Aml;
using Crypton.Core.Assets;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Kyc;
using Crypton.Core.Ledger;
using Crypton.Core.Notifications;
using Crypton.Core.Pricing;
using Crypton.Core.Security;
using Crypton.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Wallets;

public sealed record CryptoWithdrawalRequest(string Asset, string Address, decimal Amount, string? TwoFactorCode, string? IdempotencyKey);

public sealed record WithdrawalPreview(string Asset, decimal Amount, decimal Fee, decimal TotalDebit, decimal Available, decimal MinWithdrawal, decimal NgnValue);

public sealed record RequestContext(string? IpAddress, string? DeviceHash);

public sealed class CryptoWithdrawalService(
    CryptonDbContext db,
    AssetCatalog assets,
    ChainGatewayRegistry chains,
    LedgerService ledger,
    PriceService prices,
    LimitService limits,
    AmlEngine aml,
    AccountGuard guard,
    TwoFactorService twoFactor,
    SettingsService settings,
    NotificationService notifications,
    AuditService audit,
    TimeProvider clock)
{
    public async Task<WithdrawalPreview> PreviewAsync(Guid userId, string assetCode, decimal amount, CancellationToken ct = default)
    {
        var asset = await assets.GetAsync(assetCode, ct);
        if (asset.IsFiat)
        {
            throw AppException.Validation("Use a naira withdrawal for NGN.");
        }

        var available = await ledger.GetUserBalanceAsync(userId, asset.Code, AccountKind.Available, ct);
        var ngn = amount > 0 ? await SafeNgnAsync(asset.Code, amount, ct) : 0m;
        return new WithdrawalPreview(asset.Code, amount, asset.WithdrawalFee, amount + asset.WithdrawalFee, available, asset.MinWithdrawal, ngn);
    }

    public async Task<CryptoWithdrawal> RequestAsync(Guid userId, CryptoWithdrawalRequest request, RequestContext context, CancellationToken ct = default)
    {
        var user = await guard.RequireCanWithdrawAsync(userId, ct);
        var asset = await assets.GetAsync(request.Asset, ct);
        if (asset.IsFiat || asset.Network is null)
        {
            throw AppException.Validation("Use a naira withdrawal for NGN.");
        }

        if (!asset.WithdrawalsEnabled)
        {
            throw new AppException(ErrorCodes.AssetDisabled, $"{asset.Code} withdrawals are paused.", 409);
        }

        if (request.IdempotencyKey is { Length: > 64 })
        {
            throw AppException.Validation("idempotencyKey must be at most 64 characters.");
        }

        if (request.IdempotencyKey is not null)
        {
            var existing = await db.CryptoWithdrawals.AsNoTracking()
                .FirstOrDefaultAsync(w => w.UserId == userId && w.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null)
            {
                return existing;
            }
        }

        var gateway = chains.Get(asset.Network);
        if (!gateway.IsConfigured)
        {
            throw new AppException(ErrorCodes.NotConfigured, $"{asset.Name} withdrawals are not configured yet.", 503);
        }

        var rawAddress = (request.Address ?? "").Trim();
        if (!gateway.IsValidAddress(rawAddress))
        {
            throw new AppException(ErrorCodes.InvalidAddress, $"That is not a valid {asset.Name} address for this network.");
        }

        var address = gateway.NormalizeAddress(rawAddress);
        var ownDeposit = await db.DepositAddresses.AsNoTracking().AnyAsync(a => a.Network == asset.Network && a.Address == address, ct);
        if (ownDeposit)
        {
            throw new AppException(ErrorCodes.InvalidAddress, "Withdrawals to Crypton deposit addresses are not supported. Use the recipient's external wallet address.");
        }

        if (request.Amount <= 0 || !MoneyMath.HasMaxDecimals(request.Amount, asset.Precision))
        {
            throw new AppException(ErrorCodes.InvalidAmount, $"Enter a positive amount with at most {asset.Precision} decimals.");
        }

        if (request.Amount < asset.MinWithdrawal)
        {
            throw new AppException(ErrorCodes.AmountTooSmall, $"The minimum withdrawal is {MoneyMath.ToPlainString(asset.MinWithdrawal)} {asset.Code}.");
        }

        var withdrawalSettings = await settings.GetAsync<WithdrawalSettings>(ct);
        var ngnValue = await prices.ToNgnAsync(asset.Code, request.Amount, ct);
        await limits.EnsureWithinLimitAsync(userId, user.KycTier, LimitKind.CryptoWithdrawal, ngnValue, ct);

        var fee = asset.WithdrawalFee;
        var total = request.Amount + fee;
        var available = await ledger.GetUserBalanceAsync(userId, asset.Code, AccountKind.Available, ct);
        if (available < total)
        {
            throw new AppException(ErrorCodes.InsufficientFunds, $"Insufficient {asset.Code} balance. You need {MoneyMath.ToPlainString(total)} {asset.Code} including the network fee.", 409);
        }

        // Verify 2FA last so a code is only consumed once the request is otherwise valid.
        await twoFactor.RequireAsync(userId, request.TwoFactorCode, withdrawalSettings.RequireTwoFactor, ct);

        var withdrawalId = Ids.New();
        var decision = await aml.EvaluateWithdrawalAsync(
            new WithdrawalRiskContext(userId, asset.Code, false, asset.Network, address, request.Amount, ngnValue, context.DeviceHash), ct);

        if (decision.Blocked)
        {
            aml.QueueAlerts(decision, userId, AmlSubjectType.CryptoWithdrawal, null);
            await db.SaveChangesAsync(ct);
            throw AppException.Forbidden("This withdrawal cannot be processed. Contact support if you think this is a mistake.", ErrorCodes.AmlBlocked);
        }

        var needsReview = decision.NeedsReview || ngnValue > withdrawalSettings.ManualReviewAboveNgn;
        var now = clock.GetUtcNow();

        var withdrawal = await db.InTransactionAsync(async token =>
        {
            await ledger.PostAsync(
                new JournalBuilder(JournalTypes.CryptoWithdrawalHold)
                    .ForUser(userId)
                    .Reference("crypto_withdrawal", withdrawalId)
                    .Idempotent($"crypto_withdrawal:{withdrawalId}:hold")
                    .User(userId, asset.Code, AccountKind.Available, -total)
                    .User(userId, asset.Code, AccountKind.WithdrawalHold, total),
                token);

            var entity = new CryptoWithdrawal
            {
                Id = withdrawalId,
                UserId = userId,
                Asset = asset.Code,
                Network = asset.Network,
                ToAddress = address,
                Amount = request.Amount,
                Fee = fee,
                NgnValue = ngnValue,
                Status = needsReview ? CryptoWithdrawalStatus.PendingReview : CryptoWithdrawalStatus.Approved,
                IdempotencyKey = request.IdempotencyKey,
                RiskSummary = needsReview && decision.Summary is null ? "Above the manual review threshold." : decision.Summary,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.CryptoWithdrawals.Add(entity);
            aml.QueueAlerts(decision, userId, AmlSubjectType.CryptoWithdrawal, withdrawalId);
            audit.Record(AuditActions.WithdrawalRequested, userId, new AuditContext(userId, context.IpAddress), "crypto_withdrawal", withdrawalId.ToString(),
                new { entity.Asset, entity.Amount, entity.ToAddress, entity.Status });
            await notifications.QueueAsync(userId, NotificationTypes.Withdrawal,
                needsReview ? $"{asset.Code} withdrawal under review" : $"{asset.Code} withdrawal requested",
                needsReview
                    ? $"Your withdrawal of {MoneyMath.ToPlainString(request.Amount)} {asset.Code} is being reviewed. This usually takes less than a day."
                    : $"Your withdrawal of {MoneyMath.ToPlainString(request.Amount)} {asset.Code} is queued for sending.",
                "/wallets/withdrawals", email: true, token);

            try
            {
                await db.SaveChangesAsync(token);
            }
            catch (DbUpdateException ex) when (request.IdempotencyKey is not null && TransactionExtensions.IsUniqueViolation(ex))
            {
                throw AppException.Conflict(ErrorCodes.DuplicateRequest, "This withdrawal was already submitted.");
            }

            return entity;
        }, ct);

        if (needsReview)
        {
            await notifications.NotifyStaffAsync([Roles.Admin, Roles.Compliance], "Withdrawal needs review",
                $"{MoneyMath.ToPlainString(withdrawal.Amount)} {withdrawal.Asset} (NGN {ngnValue:N0}). {withdrawal.RiskSummary}", "/admin/withdrawals", ct);
        }

        return withdrawal;
    }

    public async Task<CryptoWithdrawal> CancelAsync(Guid userId, Guid withdrawalId, CancellationToken ct = default)
    {
        return await db.InTransactionAsync(async token =>
        {
            var withdrawal = await LockAsync(withdrawalId, token);
            if (withdrawal is null || withdrawal.UserId != userId)
            {
                throw AppException.NotFound("Withdrawal");
            }

            if (withdrawal.Status is not (CryptoWithdrawalStatus.PendingReview or CryptoWithdrawalStatus.Approved))
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "This withdrawal can no longer be cancelled.");
            }

            await ReleaseHoldAsync(withdrawal, CryptoWithdrawalStatus.Cancelled, "Cancelled by user", token);
            audit.Record(AuditActions.WithdrawalCancelled, userId, new AuditContext(userId, null), "crypto_withdrawal", withdrawal.Id.ToString());
            await db.SaveChangesAsync(token);
            return withdrawal;
        }, ct);
    }

    public async Task<CryptoWithdrawal> ApproveAsync(Guid adminId, Guid withdrawalId, string? note, CancellationToken ct = default)
    {
        return await db.InTransactionAsync(async token =>
        {
            var withdrawal = await LockAsync(withdrawalId, token) ?? throw AppException.NotFound("Withdrawal");
            if (withdrawal.Status != CryptoWithdrawalStatus.PendingReview)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, $"Only withdrawals pending review can be approved (current: {withdrawal.Status}).");
            }

            var now = clock.GetUtcNow();
            withdrawal.Status = CryptoWithdrawalStatus.Approved;
            withdrawal.ReviewedBy = adminId;
            withdrawal.ReviewedAt = now;
            withdrawal.ReviewNote = note;
            withdrawal.UpdatedAt = now;
            audit.Record(AuditActions.AdminWithdrawalApproved, withdrawal.UserId, new AuditContext(adminId, null), "crypto_withdrawal", withdrawal.Id.ToString(), new { note });
            await notifications.QueueAsync(withdrawal.UserId, NotificationTypes.Withdrawal, $"{withdrawal.Asset} withdrawal approved",
                $"Your withdrawal of {MoneyMath.ToPlainString(withdrawal.Amount)} {withdrawal.Asset} was approved and is being sent.", "/wallets/withdrawals", email: false, token);
            await db.SaveChangesAsync(token);
            return withdrawal;
        }, ct);
    }

    public async Task<CryptoWithdrawal> RejectAsync(Guid adminId, Guid withdrawalId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw AppException.Validation("A reason is required.");
        }

        return await db.InTransactionAsync(async token =>
        {
            var withdrawal = await LockAsync(withdrawalId, token) ?? throw AppException.NotFound("Withdrawal");
            if (withdrawal.Status is not (CryptoWithdrawalStatus.PendingReview or CryptoWithdrawalStatus.Approved))
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, $"This withdrawal can no longer be rejected (current: {withdrawal.Status}).");
            }

            withdrawal.ReviewedBy = adminId;
            withdrawal.ReviewedAt = clock.GetUtcNow();
            withdrawal.ReviewNote = reason;
            await ReleaseHoldAsync(withdrawal, CryptoWithdrawalStatus.Rejected, reason, token);
            audit.Record(AuditActions.AdminWithdrawalRejected, withdrawal.UserId, new AuditContext(adminId, null), "crypto_withdrawal", withdrawal.Id.ToString(), new { reason });
            await notifications.QueueAsync(withdrawal.UserId, NotificationTypes.Withdrawal, $"{withdrawal.Asset} withdrawal rejected",
                $"Your withdrawal of {MoneyMath.ToPlainString(withdrawal.Amount)} {withdrawal.Asset} was rejected and the funds were returned to your wallet. Reason: {reason}",
                "/wallets/withdrawals", email: true, token);
            await db.SaveChangesAsync(token);
            return withdrawal;
        }, ct);
    }

    /// <summary>Operator action for a withdrawal stuck in NeedsAttention that was never settled: sign and send again.</summary>
    public async Task<CryptoWithdrawal> RetryAsync(Guid adminId, Guid withdrawalId, string note, CancellationToken ct = default)
    {
        return await db.InTransactionAsync(async token =>
        {
            var withdrawal = await LockAsync(withdrawalId, token) ?? throw AppException.NotFound("Withdrawal");
            if (withdrawal.Status != CryptoWithdrawalStatus.NeedsAttention || withdrawal.BroadcastAt is not null)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "Only unsettled withdrawals that need attention can be retried.");
            }

            withdrawal.Status = CryptoWithdrawalStatus.Approved;
            withdrawal.TxHash = null;
            withdrawal.RawTransaction = null;
            withdrawal.BroadcastAttempts = 0;
            withdrawal.FailureReason = null;
            withdrawal.ReviewNote = note;
            withdrawal.UpdatedAt = clock.GetUtcNow();
            audit.Record(AuditActions.AdminWithdrawalRetried, withdrawal.UserId, new AuditContext(adminId, null), "crypto_withdrawal", withdrawal.Id.ToString(), new { note });
            await db.SaveChangesAsync(token);
            return withdrawal;
        }, ct);
    }

    /// <summary>Operator action: refund a withdrawal that needs attention (never sent, or confirmed failed on chain).</summary>
    public async Task<CryptoWithdrawal> RefundAsync(Guid adminId, Guid withdrawalId, string note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw AppException.Validation("A note is required.");
        }

        return await db.InTransactionAsync(async token =>
        {
            var withdrawal = await LockAsync(withdrawalId, token) ?? throw AppException.NotFound("Withdrawal");
            if (withdrawal.Status != CryptoWithdrawalStatus.NeedsAttention)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "Only withdrawals that need attention can be refunded.");
            }

            if (withdrawal.BroadcastAt is null)
            {
                await ReleaseHoldAsync(withdrawal, CryptoWithdrawalStatus.Failed, note, token);
            }
            else
            {
                // Already settled out of the hold: reverse the settlement.
                var total = withdrawal.Amount + withdrawal.Fee;
                await ledger.PostAsync(
                    new JournalBuilder(JournalTypes.CryptoWithdrawalRelease)
                        .ForUser(withdrawal.UserId)
                        .Reference("crypto_withdrawal", withdrawal.Id)
                        .Idempotent($"crypto_withdrawal:{withdrawal.Id}:refund")
                        .Describe("Refund of failed withdrawal")
                        .System(SystemAccounts.Custody, withdrawal.Asset, -withdrawal.Amount)
                        .System(SystemAccounts.Fees, withdrawal.Asset, -withdrawal.Fee)
                        .User(withdrawal.UserId, withdrawal.Asset, AccountKind.Available, total),
                    token);
                withdrawal.Status = CryptoWithdrawalStatus.Failed;
                withdrawal.FailureReason = note;
                withdrawal.UpdatedAt = clock.GetUtcNow();
            }

            audit.Record(AuditActions.AdminWithdrawalRefunded, withdrawal.UserId, new AuditContext(adminId, null), "crypto_withdrawal", withdrawal.Id.ToString(), new { note });
            await notifications.QueueAsync(withdrawal.UserId, NotificationTypes.Withdrawal, $"{withdrawal.Asset} withdrawal refunded",
                $"Your withdrawal of {MoneyMath.ToPlainString(withdrawal.Amount)} {withdrawal.Asset} could not be completed and the funds are back in your wallet.",
                "/wallets/withdrawals", email: true, token);
            await db.SaveChangesAsync(token);
            return withdrawal;
        }, ct);
    }

    /// <summary>Operator action: a NeedsAttention withdrawal was verified as sent (e.g. confirmed in a block explorer).</summary>
    public async Task<CryptoWithdrawal> MarkSentAsync(Guid adminId, Guid withdrawalId, string txHash, string note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(txHash))
        {
            throw AppException.Validation("A transaction hash is required.");
        }

        return await db.InTransactionAsync(async token =>
        {
            var withdrawal = await LockAsync(withdrawalId, token) ?? throw AppException.NotFound("Withdrawal");
            if (withdrawal.Status != CryptoWithdrawalStatus.NeedsAttention)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "Only withdrawals that need attention can be marked as sent.");
            }

            withdrawal.TxHash = txHash.Trim();
            if (withdrawal.BroadcastAt is null)
            {
                await SettleAsync(withdrawal, token);
            }

            withdrawal.Status = CryptoWithdrawalStatus.Broadcast;
            withdrawal.ReviewNote = note;
            withdrawal.UpdatedAt = clock.GetUtcNow();
            audit.Record(AuditActions.AdminWithdrawalMarkedSent, withdrawal.UserId, new AuditContext(adminId, null), "crypto_withdrawal", withdrawal.Id.ToString(), new { txHash, note });
            await db.SaveChangesAsync(token);
            return withdrawal;
        }, ct);
    }

    internal async Task SettleAsync(CryptoWithdrawal withdrawal, CancellationToken ct)
    {
        var total = withdrawal.Amount + withdrawal.Fee;
        var journal = new JournalBuilder(JournalTypes.CryptoWithdrawalSettle)
            .ForUser(withdrawal.UserId)
            .Reference("crypto_withdrawal", withdrawal.Id)
            .Idempotent($"crypto_withdrawal:{withdrawal.Id}:settle")
            .Describe($"Withdrawal {MoneyMath.ToPlainString(withdrawal.Amount)} {withdrawal.Asset} to {withdrawal.ToAddress}")
            .User(withdrawal.UserId, withdrawal.Asset, AccountKind.WithdrawalHold, -total)
            .System(SystemAccounts.Custody, withdrawal.Asset, withdrawal.Amount)
            .System(SystemAccounts.Fees, withdrawal.Asset, withdrawal.Fee);
        await ledger.PostAsync(journal, ct);
        withdrawal.BroadcastAt = clock.GetUtcNow();
    }

    internal async Task BookNetworkFeeAsync(CryptoWithdrawal withdrawal, CancellationToken ct)
    {
        if (withdrawal.NetworkFeeBooked || withdrawal.NetworkFee is not { } fee || fee <= 0 || withdrawal.NetworkFeeAsset is null)
        {
            return;
        }

        var precision = await assets.PrecisionAsync(withdrawal.NetworkFeeAsset, ct);
        var rounded = MoneyMath.Ceil(fee, precision);
        await ledger.PostAsync(
            new JournalBuilder(JournalTypes.NetworkFee)
                .Reference("crypto_withdrawal", withdrawal.Id)
                .Idempotent($"crypto_withdrawal:{withdrawal.Id}:network_fee")
                .Describe($"Network fee for {withdrawal.TxHash}")
                .System(SystemAccounts.Custody, withdrawal.NetworkFeeAsset, rounded)
                .System(SystemAccounts.NetworkFees, withdrawal.NetworkFeeAsset, -rounded),
            ct);
        withdrawal.NetworkFeeBooked = true;
    }

    private async Task ReleaseHoldAsync(CryptoWithdrawal withdrawal, CryptoWithdrawalStatus finalStatus, string reason, CancellationToken ct)
    {
        var total = withdrawal.Amount + withdrawal.Fee;
        await ledger.PostAsync(
            new JournalBuilder(JournalTypes.CryptoWithdrawalRelease)
                .ForUser(withdrawal.UserId)
                .Reference("crypto_withdrawal", withdrawal.Id)
                .Idempotent($"crypto_withdrawal:{withdrawal.Id}:release")
                .Describe(reason)
                .User(withdrawal.UserId, withdrawal.Asset, AccountKind.WithdrawalHold, -total)
                .User(withdrawal.UserId, withdrawal.Asset, AccountKind.Available, total),
            ct);
        withdrawal.Status = finalStatus;
        withdrawal.FailureReason = finalStatus == CryptoWithdrawalStatus.Failed ? reason : withdrawal.FailureReason;
        withdrawal.UpdatedAt = clock.GetUtcNow();
    }

    internal async Task<CryptoWithdrawal?> LockAsync(Guid id, CancellationToken ct) =>
        (await db.CryptoWithdrawals.FromSqlInterpolated($"SELECT * FROM crypto_withdrawals WHERE id = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();

    private async Task<decimal> SafeNgnAsync(string asset, decimal amount, CancellationToken ct)
    {
        try
        {
            return await prices.ToNgnAsync(asset, amount, ct);
        }
        catch (AppException)
        {
            return 0m;
        }
    }
}
