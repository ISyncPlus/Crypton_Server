using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;

namespace Crypton.Core.Audit;

public static class AuditActions
{
    public const string Register = "auth.register";
    public const string EmailConfirmed = "auth.email_confirmed";
    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string Logout = "auth.logout";
    public const string TokenReuseDetected = "auth.refresh_token_reuse";
    public const string PasswordChanged = "security.password_changed";
    public const string PasswordReset = "security.password_reset";
    public const string TwoFactorEnabled = "security.2fa_enabled";
    public const string TwoFactorDisabled = "security.2fa_disabled";
    public const string RecoveryCodesRegenerated = "security.recovery_codes_regenerated";
    public const string SessionRevoked = "security.session_revoked";
    public const string ProfileUpdated = "account.profile_updated";
    public const string WithdrawalRequested = "wallet.withdrawal_requested";
    public const string WithdrawalCancelled = "wallet.withdrawal_cancelled";
    public const string BankAccountAdded = "fiat.bank_account_added";
    public const string BankAccountRemoved = "fiat.bank_account_removed";
    public const string KycSubmitted = "kyc.submitted";
    public const string AdminWithdrawalApproved = "admin.withdrawal_approved";
    public const string AdminWithdrawalRejected = "admin.withdrawal_rejected";
    public const string AdminWithdrawalRetried = "admin.withdrawal_retried";
    public const string AdminWithdrawalRefunded = "admin.withdrawal_refunded";
    public const string AdminWithdrawalMarkedSent = "admin.withdrawal_marked_sent";
    public const string AdminKycApproved = "admin.kyc_approved";
    public const string AdminKycRejected = "admin.kyc_rejected";
    public const string AdminUserFrozen = "admin.user_frozen";
    public const string AdminUserUnfrozen = "admin.user_unfrozen";
    public const string AdminTwoFactorReset = "admin.2fa_reset";
    public const string AdminRolesChanged = "admin.roles_changed";
    public const string AdminSessionsRevoked = "admin.sessions_revoked";
    public const string AdminBalanceAdjusted = "admin.balance_adjusted";
    public const string AdminTreasuryFunded = "admin.treasury_funded";
    public const string AdminTreasuryDefunded = "admin.treasury_defunded";
    public const string AdminSettingsChanged = "admin.settings_changed";
    public const string AdminAssetChanged = "admin.asset_changed";
    public const string AdminAmlAlertResolved = "admin.aml_alert_resolved";
    public const string AdminBlockedAddressAdded = "admin.blocked_address_added";
    public const string AdminBlockedAddressRemoved = "admin.blocked_address_removed";
    public const string AdminDisputeResolved = "admin.p2p_dispute_resolved";
    public const string AdminAdSuspended = "admin.p2p_ad_suspended";
}

public sealed record AuditContext(Guid? ActorUserId, string? IpAddress);

public sealed class AuditService(CryptonDbContext db, TimeProvider clock)
{
    /// <summary>Adds an audit record to the current unit of work. Caller saves.</summary>
    public void Record(string action, Guid? userId, AuditContext? context = null, string? entityType = null, string? entityId = null, object? data = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            ActorUserId = context?.ActorUserId ?? userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            IpAddress = context?.IpAddress,
            Data = data is null ? null : Json.Serialize(data),
            CreatedAt = clock.GetUtcNow(),
        });
    }

    public async Task RecordNowAsync(string action, Guid? userId, AuditContext? context = null, string? entityType = null, string? entityId = null, object? data = null, CancellationToken ct = default)
    {
        Record(action, userId, context, entityType, entityId, data);
        await db.SaveChangesAsync(ct);
    }
}
