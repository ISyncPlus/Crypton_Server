using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crypton.Core.Wallets;

/// <summary>
/// Sends approved crypto withdrawals and tracks them to confirmation. Designed so that a crash at any point
/// never causes a double send: the signed transaction is persisted before broadcast, re-broadcasts reuse it,
/// and ambiguous outcomes escalate to an operator instead of refunding automatically.
/// </summary>
public sealed class WithdrawalProcessor(
    CryptonDbContext db,
    ChainGatewayRegistry chains,
    CryptoWithdrawalService withdrawals,
    NotificationService notifications,
    IOptions<BlockchainOptions> options,
    TimeProvider clock,
    ILogger<WithdrawalProcessor> logger)
{
    public async Task RunAsync(string network, CancellationToken ct)
    {
        var gateway = chains.Find(network);
        if (gateway is null || !gateway.IsConfigured)
        {
            return;
        }

        await RetryBroadcastingAsync(gateway, ct);
        await SendApprovedAsync(gateway, ct);
        await TrackConfirmationsAsync(gateway, ct);
    }

    private async Task SendApprovedAsync(IChainGateway gateway, CancellationToken ct)
    {
        var candidates = await db.CryptoWithdrawals.AsNoTracking()
            .Where(w => w.Network == gateway.Network && w.Status == CryptoWithdrawalStatus.Approved)
            .OrderBy(w => w.CreatedAt)
            .Select(w => w.Id)
            .Take(10)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            db.ChangeTracker.Clear();
            var snapshot = await db.CryptoWithdrawals.AsNoTracking().FirstAsync(w => w.Id == id, ct);
            if (snapshot.Status != CryptoWithdrawalStatus.Approved)
            {
                continue;
            }

            var previousNonce = await db.CryptoWithdrawals.AsNoTracking()
                .Where(w => w.Network == gateway.Network && w.Nonce != null)
                .MaxAsync(w => w.Nonce, ct);

            SignedWithdrawal signed;
            try
            {
                signed = await gateway.SignWithdrawalAsync(
                    new WithdrawalSignRequest(snapshot.Id, snapshot.Asset, snapshot.ToAddress, snapshot.Amount, previousNonce), ct);
            }
            catch (HotWalletInsufficientFundsException ex)
            {
                await NoteFailureAsync(id, ex.Message, ct);
                await notifications.NotifyStaffAsync([Roles.Admin], $"Hot wallet low on {snapshot.Asset}",
                    $"Withdrawal {snapshot.Id} is waiting: {ex.Message}", "/admin/treasury", ct);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Signing withdrawal {WithdrawalId} failed", id);
                await NoteFailureAsync(id, $"Signing failed: {ex.Message}", ct);
                continue;
            }

            // Persist the signed transaction before it leaves the process.
            var stored = await db.InTransactionAsync(async token =>
            {
                var w = await withdrawals.LockAsync(id, token);
                if (w is null || w.Status != CryptoWithdrawalStatus.Approved)
                {
                    return false;
                }

                w.Status = CryptoWithdrawalStatus.Broadcasting;
                w.TxHash = signed.TxHash;
                w.RawTransaction = signed.RawTransaction;
                w.Nonce = signed.Nonce;
                w.NetworkFee = signed.NetworkFee;
                w.NetworkFeeAsset = signed.NetworkFeeAsset;
                w.FailureReason = null;
                w.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(token);
                return true;
            }, ct);

            if (stored)
            {
                await BroadcastAsync(gateway, id, ct);
            }
        }
    }

    private async Task RetryBroadcastingAsync(IChainGateway gateway, CancellationToken ct)
    {
        var stuck = await db.CryptoWithdrawals.AsNoTracking()
            .Where(w => w.Network == gateway.Network && w.Status == CryptoWithdrawalStatus.Broadcasting)
            .OrderBy(w => w.UpdatedAt)
            .Select(w => w.Id)
            .Take(10)
            .ToListAsync(ct);

        foreach (var id in stuck)
        {
            await BroadcastAsync(gateway, id, ct);
        }
    }

    private async Task BroadcastAsync(IChainGateway gateway, Guid id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var w = await db.CryptoWithdrawals.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        if (w.Status != CryptoWithdrawalStatus.Broadcasting || w.TxHash is null || w.RawTransaction is null)
        {
            return;
        }

        BroadcastResult result;
        try
        {
            result = await gateway.BroadcastAsync(new SignedWithdrawal(w.TxHash, w.RawTransaction, w.NetworkFee, w.NetworkFeeAsset ?? w.Asset, w.Nonce), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new BroadcastResult(BroadcastOutcome.Unknown, ex.Message);
        }

        if (result.Outcome is BroadcastOutcome.Rejected or BroadcastOutcome.Unknown)
        {
            // The transaction may still have reached the network; check before deciding anything.
            try
            {
                var status = await gateway.GetTransactionStatusAsync(w.Asset, w.TxHash, ct);
                if (status.Found)
                {
                    result = new BroadcastResult(BroadcastOutcome.AlreadyKnown, result.Message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Status check for {TxHash} failed", w.TxHash);
            }
        }

        await db.InTransactionAsync(async token =>
        {
            var locked = await withdrawals.LockAsync(id, token);
            if (locked is null || locked.Status != CryptoWithdrawalStatus.Broadcasting)
            {
                return;
            }

            var now = clock.GetUtcNow();
            locked.BroadcastAttempts++;
            locked.UpdatedAt = now;

            switch (result.Outcome)
            {
                case BroadcastOutcome.Accepted:
                case BroadcastOutcome.AlreadyKnown:
                    await withdrawals.SettleAsync(locked, token);
                    locked.Status = CryptoWithdrawalStatus.Broadcast;
                    locked.FailureReason = null;
                    if (gateway.Network == Networks.Bitcoin || gateway.IsSimulated)
                    {
                        // Bitcoin fees are known exactly at signing time.
                        await withdrawals.BookNetworkFeeAsync(locked, token);
                    }

                    await notifications.QueueAsync(locked.UserId, NotificationTypes.Withdrawal, $"{locked.Asset} withdrawal sent",
                        $"{MoneyMath.ToPlainString(locked.Amount)} {locked.Asset} is on its way. Transaction: {locked.TxHash}", "/wallets/withdrawals", email: true, token);
                    break;

                case BroadcastOutcome.Rejected:
                    locked.Status = CryptoWithdrawalStatus.NeedsAttention;
                    locked.FailureReason = Truncate($"Network rejected the transaction: {result.Message}");
                    break;

                default:
                    if (locked.BroadcastAttempts >= options.Value.MaxBroadcastAttempts)
                    {
                        locked.Status = CryptoWithdrawalStatus.NeedsAttention;
                        locked.FailureReason = Truncate($"Broadcast outcome unknown after {locked.BroadcastAttempts} attempts: {result.Message}");
                    }
                    else
                    {
                        locked.FailureReason = Truncate($"Broadcast attempt {locked.BroadcastAttempts} inconclusive: {result.Message}");
                    }

                    break;
            }

            await db.SaveChangesAsync(token);
        }, ct);

        var after = await db.CryptoWithdrawals.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        if (after.Status == CryptoWithdrawalStatus.NeedsAttention)
        {
            await notifications.NotifyStaffAsync([Roles.Admin], "Withdrawal needs attention",
                $"{MoneyMath.ToPlainString(after.Amount)} {after.Asset}: {after.FailureReason}", "/admin/withdrawals", ct);
        }
    }

    private async Task TrackConfirmationsAsync(IChainGateway gateway, CancellationToken ct)
    {
        var inFlight = await db.CryptoWithdrawals.AsNoTracking()
            .Where(w => w.Network == gateway.Network && w.Status == CryptoWithdrawalStatus.Broadcast)
            .OrderBy(w => w.UpdatedAt)
            .Select(w => new { w.Id, w.Asset, w.TxHash })
            .Take(50)
            .ToListAsync(ct);

        foreach (var item in inFlight)
        {
            if (item.TxHash is null)
            {
                continue;
            }

            ChainTxStatus status;
            try
            {
                status = await gateway.GetTransactionStatusAsync(item.Asset, item.TxHash, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Confirmation check for {TxHash} failed", item.TxHash);
                continue;
            }

            db.ChangeTracker.Clear();
            await db.InTransactionAsync(async token =>
            {
                var w = await withdrawals.LockAsync(item.Id, token);
                if (w is null || w.Status != CryptoWithdrawalStatus.Broadcast)
                {
                    return;
                }

                var asset = await db.Assets.AsNoTracking().FirstAsync(a => a.Code == w.Asset, token);
                var now = clock.GetUtcNow();
                w.UpdatedAt = now;

                if (status.Failed)
                {
                    if (status.NetworkFee is { } spent)
                    {
                        w.NetworkFee = spent;
                        await withdrawals.BookNetworkFeeAsync(w, token);
                    }

                    w.Status = CryptoWithdrawalStatus.NeedsAttention;
                    w.FailureReason = "Transaction failed on chain (reverted).";
                }
                else if (status.Found)
                {
                    w.Confirmations = status.Confirmations;
                    if (status.NetworkFee is { } actualFee && !w.NetworkFeeBooked)
                    {
                        w.NetworkFee = actualFee;
                    }

                    if (status.Confirmations >= asset.RequiredConfirmations)
                    {
                        await withdrawals.BookNetworkFeeAsync(w, token);
                        w.Status = CryptoWithdrawalStatus.Confirmed;
                        w.ConfirmedAt = now;
                        await notifications.QueueAsync(w.UserId, NotificationTypes.Withdrawal, $"{w.Asset} withdrawal confirmed",
                            $"Your withdrawal of {MoneyMath.ToPlainString(w.Amount)} {w.Asset} is confirmed on the network.", "/wallets/withdrawals", email: false, token);
                    }
                }
                else if (w.BroadcastAt is { } sentAt && now - sentAt > TimeSpan.FromHours(6))
                {
                    w.Status = CryptoWithdrawalStatus.NeedsAttention;
                    w.FailureReason = "Transaction not found on the network 6 hours after broadcast.";
                }

                await db.SaveChangesAsync(token);
            }, ct);
        }
    }

    private async Task NoteFailureAsync(Guid id, string reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var w = await db.CryptoWithdrawals.FirstAsync(x => x.Id == id, ct);
        w.FailureReason = Truncate(reason);
        w.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string? value) => value is null ? "" : value.Length <= 500 ? value : value[..500];
}
