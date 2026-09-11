using Crypton.Core.Aml;
using Crypton.Core.Assets;
using Crypton.Core.Audit;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Ledger;
using Crypton.Core.Notifications;
using Crypton.Core.Pricing;
using Crypton.Core.Security;
using Crypton.Core.Settings;
using Crypton.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Crypton.Core.P2P;

public sealed class P2POrderService(
    CryptonDbContext db,
    AssetCatalog assets,
    LedgerService ledger,
    PriceService prices,
    SettingsService settings,
    AccountGuard guard,
    TwoFactorService twoFactor,
    P2PAdService ads,
    TraderStatsService traders,
    AmlEngine aml,
    NotificationService notifications,
    AuditService audit,
    IFileStorage storage,
    TimeProvider clock,
    ILogger<P2POrderService> logger)
{
    private static readonly P2POrderStatus[] OpenStatuses = [P2POrderStatus.PendingPayment, P2POrderStatus.Paid, P2POrderStatus.Disputed];

    public async Task<P2POrder> CreateAsync(Guid takerId, CreateOrderRequest request, CancellationToken ct = default)
    {
        var taker = await guard.RequireActiveUserAsync(takerId, ct);
        var cfg = await settings.GetAsync<P2PSettings>(ct);
        await ads.EnsureP2PAllowedAsync(taker, cfg, ct);

        var openOrders = await db.P2POrders.CountAsync(o => (o.BuyerId == takerId || o.SellerId == takerId) && OpenStatuses.Contains(o.Status), ct);
        if (openOrders >= cfg.MaxOpenOrdersPerUser)
        {
            throw AppException.Validation($"You have {openOrders} open orders. Complete or cancel some before starting another.");
        }

        var adSnapshot = await db.P2PAds.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AdId, ct) ?? throw AppException.NotFound("Ad");
        var asset = await assets.GetAsync(adSnapshot.Asset, ct);
        var market = await prices.GetFreshPriceAsync(asset.Code, TimeSpan.FromMinutes(10), ct);
        await traders.EnsureDisplayNameAsync(takerId, ct);

        BankAccount? takerBank = null;
        if (adSnapshot.Side == P2PAdSide.Buy)
        {
            if (request.PaymentMethodId is not { } bankId)
            {
                throw AppException.Validation("Choose the bank account where the buyer should pay you.");
            }

            takerBank = await db.BankAccounts.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bankId && b.UserId == takerId && !b.IsDeleted, ct)
                ?? throw AppException.Validation("That bank account is not available.");
        }

        var order = await db.InTransactionAsync(async token =>
        {
            var ad = await ads.LockAsync(request.AdId, token) ?? throw AppException.NotFound("Ad");
            if (ad.Status != P2PAdStatus.Active || ad.SuspendedByAdmin)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "This ad is no longer available.");
            }

            if (ad.UserId == takerId)
            {
                throw AppException.Validation("You cannot trade with your own ad.");
            }

            var maker = await db.Users.AsNoTracking().FirstAsync(u => u.Id == ad.UserId, token);
            if (maker.Status != UserStatus.Active)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "This ad is no longer available.");
            }

            var price = P2PPricing.EffectivePrice(ad, market.PriceNgn);
            var (quantity, fiatAmount) = P2PPricing.Size(price, request.FiatAmount, request.Quantity, asset.Precision);
            if (quantity <= 0)
            {
                throw new AppException(ErrorCodes.AmountTooSmall, "Amount is too small.");
            }

            if (fiatAmount < ad.MinOrderFiat || fiatAmount > ad.MaxOrderFiat)
            {
                throw new AppException(ErrorCodes.LimitExceeded, $"This ad accepts orders between NGN {ad.MinOrderFiat:N2} and NGN {ad.MaxOrderFiat:N2}.");
            }

            if (quantity > ad.RemainingQuantity)
            {
                throw new AppException(ErrorCodes.InsufficientLiquidity, $"Only {MoneyMath.ToPlainString(ad.RemainingQuantity)} {ad.Asset} is left on this ad.", 409);
            }

            var now = clock.GetUtcNow();
            var entity = new P2POrder
            {
                Id = Ids.New(),
                OrderNumber = await NewOrderNumberAsync(now, token),
                AdId = ad.Id,
                AdSide = ad.Side,
                MakerId = ad.UserId,
                TakerId = takerId,
                BuyerId = ad.Side == P2PAdSide.Sell ? takerId : ad.UserId,
                SellerId = ad.Side == P2PAdSide.Sell ? ad.UserId : takerId,
                Asset = ad.Asset,
                FiatCurrency = ad.FiatCurrency,
                Quantity = quantity,
                Price = price,
                FiatAmount = fiatAmount,
                Status = P2POrderStatus.PendingPayment,
                PaymentDeadline = now.AddMinutes(ad.PaymentWindowMinutes),
                NgnValue = fiatAmount,
                CreatedAt = now,
            };

            var journal = new JournalBuilder(JournalTypes.P2PEscrowLock)
                .ForUser(entity.SellerId)
                .Reference("p2p_order", entity.Id)
                .Idempotent($"p2p_order:{entity.Id}:lock")
                .Describe($"Escrow for P2P order {entity.OrderNumber}");

            if (ad.Side == P2PAdSide.Sell)
            {
                entity.Fee = P2PPricing.SellOrderFee(quantity, ad.RemainingQuantity, ad.ReservedAmount, cfg.MakerFeeBps, asset.Precision);
                entity.EscrowAmount = quantity + entity.Fee;
                if (entity.EscrowAmount > ad.ReservedAmount)
                {
                    throw new InvalidOperationException($"Ad {ad.Id} reserve {ad.ReservedAmount} cannot cover escrow {entity.EscrowAmount}.");
                }

                journal.User(ad.UserId, ad.Asset, AccountKind.P2PReserve, -entity.EscrowAmount)
                    .System(SystemAccounts.P2PEscrow, ad.Asset, entity.EscrowAmount);
                ad.ReservedAmount -= entity.EscrowAmount;

                var methods = await db.BankAccounts.AsNoTracking()
                    .Where(b => ad.PaymentMethodIds.Contains(b.Id) && !b.IsDeleted)
                    .Select(b => new PaymentDetail(b.BankName, b.AccountNumber, b.AccountName))
                    .ToListAsync(token);
                if (methods.Count == 0)
                {
                    throw AppException.Conflict(ErrorCodes.InvalidState, "The seller has no payment method available on this ad.");
                }

                entity.PaymentDetails = Json.Serialize(methods);
            }
            else
            {
                entity.Fee = P2PPricing.BuyOrderFee(quantity, cfg.MakerFeeBps, asset.Precision);
                if (entity.Fee >= quantity)
                {
                    throw new AppException(ErrorCodes.AmountTooSmall, "Amount is too small.");
                }

                entity.EscrowAmount = quantity;
                journal.User(takerId, ad.Asset, AccountKind.Available, -quantity)
                    .System(SystemAccounts.P2PEscrow, ad.Asset, quantity);
                entity.PaymentDetails = Json.Serialize(new[] { new PaymentDetail(takerBank!.BankName, takerBank.AccountNumber, takerBank.AccountName) });
            }

            await ledger.PostAsync(journal, token);
            ad.RemainingQuantity -= quantity;
            ad.UpdatedAt = now;
            db.P2POrders.Add(entity);

            var decision = await aml.EvaluateP2POrderAsync(takerId, fiatAmount, token);
            aml.QueueAlerts(decision, takerId, AmlSubjectType.P2POrder, entity.Id);

            var link = $"/p2p/orders/{entity.Id}";
            await notifications.QueueAsync(entity.BuyerId, NotificationTypes.P2P, $"Order {entity.OrderNumber}: pay the seller",
                $"Send NGN {fiatAmount:N2} to the seller within {ad.PaymentWindowMinutes} minutes, then mark the order as paid.", link, email: false, token);
            await notifications.QueueAsync(entity.SellerId, NotificationTypes.P2P, $"Order {entity.OrderNumber} opened",
                $"{MoneyMath.ToPlainString(quantity)} {ad.Asset} is locked in escrow. Release it only after the NGN {fiatAmount:N2} arrives in your bank account.", link, email: true, token);

            await db.SaveChangesAsync(token);
            return entity;
        }, ct);

        return order;
    }

    public async Task<P2POrder> MarkPaidAsync(Guid userId, Guid orderId, string? paymentReference, CancellationToken ct = default) =>
        await db.InTransactionAsync(async token =>
        {
            var order = await LockOrderAsync(orderId, token);
            if (order is null || (order.BuyerId != userId && order.SellerId != userId))
            {
                throw AppException.NotFound("Order");
            }

            if (order.BuyerId != userId)
            {
                throw AppException.Forbidden("Only the buyer can mark an order as paid.");
            }

            if (order.Status != P2POrderStatus.PendingPayment)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, $"This order is {Describe(order.Status)}.");
            }

            var now = clock.GetUtcNow();
            if (now > order.PaymentDeadline)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "The payment window has closed for this order.");
            }

            order.Status = P2POrderStatus.Paid;
            order.PaidAt = now;
            order.BuyerPaymentReference = string.IsNullOrWhiteSpace(paymentReference) ? null : paymentReference.Trim()[..Math.Min(120, paymentReference.Trim().Length)];
            await notifications.QueueAsync(order.SellerId, NotificationTypes.P2P, $"Order {order.OrderNumber}: buyer has paid",
                $"The buyer marked NGN {order.FiatAmount:N2} as sent. Check your bank account, then release the {order.Asset}.", $"/p2p/orders/{order.Id}", email: true, token);
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    public async Task<P2POrder> ReleaseAsync(Guid userId, Guid orderId, string? twoFactorCode, CancellationToken ct = default)
    {
        var snapshot = await db.P2POrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (snapshot is null || (snapshot.BuyerId != userId && snapshot.SellerId != userId))
        {
            throw AppException.NotFound("Order");
        }

        if (snapshot.SellerId != userId)
        {
            throw AppException.Forbidden("Only the seller can release crypto.");
        }

        if (snapshot.Status is not (P2POrderStatus.PendingPayment or P2POrderStatus.Paid))
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, $"This order is {Describe(snapshot.Status)}.");
        }

        await guard.RequireActiveUserAsync(userId, ct);
        var withdrawalSettings = await settings.GetAsync<WithdrawalSettings>(ct);
        await twoFactor.RequireAsync(userId, twoFactorCode, withdrawalSettings.RequireTwoFactor, ct);

        return await db.InTransactionAsync(async token =>
        {
            var order = await LockOrderAsync(orderId, token) ?? throw AppException.NotFound("Order");
            if (order.Status is not (P2POrderStatus.PendingPayment or P2POrderStatus.Paid))
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, $"This order is {Describe(order.Status)}.");
            }

            await PostReleaseAsync(order, "release", token);
            order.Status = P2POrderStatus.Completed;
            order.CompletedAt = clock.GetUtcNow();
            await notifications.QueueAsync(order.BuyerId, NotificationTypes.P2P, $"Order {order.OrderNumber} completed",
                $"The seller released the {order.Asset}. It's now in your wallet.", $"/p2p/orders/{order.Id}", email: true, token);
            await db.SaveChangesAsync(token);
            return order;
        }, ct);
    }

    public async Task<P2POrder> CancelAsync(Guid userId, Guid orderId, string? reason, CancellationToken ct = default) =>
        await db.InTransactionAsync(async token =>
        {
            var order = await LockOrderAsync(orderId, token);
            if (order is null || (order.BuyerId != userId && order.SellerId != userId))
            {
                throw AppException.NotFound("Order");
            }

            if (order.BuyerId != userId)
            {
                throw AppException.Forbidden("Only the buyer can cancel an order. If you haven't been paid, wait for the payment window to end or open a dispute.");
            }

            if (order.Status is not (P2POrderStatus.PendingPayment or P2POrderStatus.Paid))
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, $"This order is {Describe(order.Status)}.");
            }

            await PostRefundAsync(order, "cancel", token);
            order.Status = P2POrderStatus.Cancelled;
            order.CancelledAt = clock.GetUtcNow();
            order.CancelledBy = userId;
            order.CancelReason = string.IsNullOrWhiteSpace(reason) ? "Cancelled by buyer" : reason.Trim()[..Math.Min(300, reason.Trim().Length)];
            await notifications.QueueAsync(order.SellerId, NotificationTypes.P2P, $"Order {order.OrderNumber} cancelled",
                "The buyer cancelled the order and your crypto was returned from escrow.", $"/p2p/orders/{order.Id}", email: false, token);
            await db.SaveChangesAsync(token);
            return order;
        }, ct);

    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.P2POrders.AsNoTracking()
            .Where(o => o.Status == P2POrderStatus.PendingPayment && o.PaymentDeadline < now)
            .OrderBy(o => o.PaymentDeadline)
            .Select(o => o.Id)
            .Take(100)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var id in due)
        {
            try
            {
                var changed = await db.InTransactionAsync(async token =>
                {
                    var order = await LockOrderAsync(id, token);
                    if (order is null || order.Status != P2POrderStatus.PendingPayment || order.PaymentDeadline >= clock.GetUtcNow())
                    {
                        return false;
                    }

                    await PostRefundAsync(order, "expire", token);
                    order.Status = P2POrderStatus.Expired;
                    order.CancelledAt = clock.GetUtcNow();
                    order.CancelReason = "Payment window expired";
                    foreach (var party in new[] { order.BuyerId, order.SellerId })
                    {
                        await notifications.QueueAsync(party, NotificationTypes.P2P, $"Order {order.OrderNumber} expired",
                            "The payment window ended before the order was marked as paid. Escrow was returned to the seller.", $"/p2p/orders/{order.Id}", email: false, token);
                    }

                    await db.SaveChangesAsync(token);
                    return true;
                }, ct);
                expired += changed ? 1 : 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogError(ex, "Expiring P2P order {OrderId} failed", id);
            }
        }

        return expired;
    }

    public async Task<P2PDispute> OpenDisputeAsync(Guid userId, Guid orderId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
        {
            throw AppException.Validation("Describe the problem in at least 10 characters.");
        }

        var cfg = await settings.GetAsync<P2PSettings>(ct);
        var dispute = await db.InTransactionAsync(async token =>
        {
            var order = await LockOrderAsync(orderId, token);
            if (order is null || (order.BuyerId != userId && order.SellerId != userId))
            {
                throw AppException.NotFound("Order");
            }

            if (order.Status != P2POrderStatus.Paid)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "A dispute can only be opened after the buyer has marked the order as paid.");
            }

            var now = clock.GetUtcNow();
            var openableAt = order.PaidAt!.Value.AddMinutes(cfg.DisputeAfterMinutes);
            if (now < openableAt)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, $"Give the other party a few minutes. You can open a dispute after {openableAt:HH:mm} UTC.");
            }

            order.Status = P2POrderStatus.Disputed;
            var entity = new P2PDispute
            {
                Id = Ids.New(),
                OrderId = order.Id,
                OpenedBy = userId,
                Reason = reason.Trim()[..Math.Min(1000, reason.Trim().Length)],
                Status = P2PDisputeStatus.Open,
                CreatedAt = now,
            };
            db.P2PDisputes.Add(entity);
            var other = order.BuyerId == userId ? order.SellerId : order.BuyerId;
            await notifications.QueueAsync(other, NotificationTypes.P2P, $"Dispute opened on order {order.OrderNumber}",
                "The other party opened a dispute. Add your evidence; our team will review and decide.", $"/p2p/orders/{order.Id}", email: true, token);
            await db.SaveChangesAsync(token);
            return entity;
        }, ct);

        await notifications.NotifyStaffAsync([Roles.Admin, Roles.Compliance], "P2P dispute opened", dispute.Reason, "/admin/p2p/disputes", ct);
        return dispute;
    }

    public async Task<P2PDisputeEvidence> AddEvidenceAsync(Guid userId, Guid orderId, string? text, (Stream Content, string FileName, long Length)? file, CancellationToken ct = default)
    {
        var order = await db.P2POrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || (order.BuyerId != userId && order.SellerId != userId))
        {
            throw AppException.NotFound("Order");
        }

        var dispute = await db.P2PDisputes.FirstOrDefaultAsync(d => d.OrderId == orderId, ct);
        if (dispute is null || dispute.Status != P2PDisputeStatus.Open)
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "There is no open dispute on this order.");
        }

        if (string.IsNullOrWhiteSpace(text) && file is null)
        {
            throw AppException.Validation("Add a note or a file.");
        }

        var evidence = new P2PDisputeEvidence
        {
            Id = Ids.New(),
            DisputeId = dispute.Id,
            UserId = userId,
            Text = string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(2000, text.Trim().Length)],
            CreatedAt = clock.GetUtcNow(),
        };

        if (file is { } upload)
        {
            if (upload.Length <= 0 || upload.Length > UploadValidation.MaxBytes)
            {
                throw AppException.Validation("Files must be up to 5 MB.");
            }

            using var buffer = new MemoryStream();
            await upload.Content.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            var type = UploadValidation.DetectContentType(bytes.AsSpan(0, Math.Min(bytes.Length, 16)))
                ?? throw AppException.Validation("Evidence files must be JPEG, PNG or PDF.");
            buffer.Position = 0;
            evidence.StorageKey = await storage.SaveAsync(buffer, UploadValidation.Extensions[type], ct);
            evidence.ContentType = type;
            evidence.FileName = Path.GetFileName(upload.FileName) is { Length: > 0 and <= 200 } name ? name : "evidence" + UploadValidation.Extensions[type];
        }

        db.P2PDisputeEvidence.Add(evidence);
        await db.SaveChangesAsync(ct);
        return evidence;
    }

    public async Task<P2PDispute> ResolveDisputeAsync(Guid adminId, Guid disputeId, bool releaseToBuyer, string note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw AppException.Validation("A resolution note is required.");
        }

        return await db.InTransactionAsync(async token =>
        {
            var dispute = await db.P2PDisputes.FirstOrDefaultAsync(d => d.Id == disputeId, token) ?? throw AppException.NotFound("Dispute");
            if (dispute.Status != P2PDisputeStatus.Open)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "This dispute is already resolved.");
            }

            var order = await LockOrderAsync(dispute.OrderId, token) ?? throw AppException.NotFound("Order");
            if (order.Status != P2POrderStatus.Disputed)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "The order is not in dispute.");
            }

            var now = clock.GetUtcNow();
            if (releaseToBuyer)
            {
                await PostReleaseAsync(order, "dispute_release", token);
                order.Status = P2POrderStatus.ResolvedToBuyer;
                order.CompletedAt = now;
                dispute.Status = P2PDisputeStatus.ResolvedToBuyer;
            }
            else
            {
                await PostRefundAsync(order, "dispute_refund", token);
                order.Status = P2POrderStatus.ResolvedToSeller;
                order.CancelledAt = now;
                order.CancelReason = "Dispute resolved in the seller's favour";
                dispute.Status = P2PDisputeStatus.ResolvedToSeller;
            }

            dispute.ResolvedBy = adminId;
            dispute.ResolvedAt = now;
            dispute.ResolutionNote = note.Trim();
            audit.Record(AuditActions.AdminDisputeResolved, order.BuyerId, new AuditContext(adminId, null), "p2p_dispute", dispute.Id.ToString(),
                new { order.OrderNumber, releaseToBuyer, note });

            var outcome = releaseToBuyer ? $"The {order.Asset} was released to the buyer." : $"The {order.Asset} was returned to the seller.";
            foreach (var party in new[] { order.BuyerId, order.SellerId })
            {
                await notifications.QueueAsync(party, NotificationTypes.P2P, $"Dispute on order {order.OrderNumber} resolved",
                    $"{outcome} Note from our team: {note.Trim()}", $"/p2p/orders/{order.Id}", email: true, token);
            }

            await db.SaveChangesAsync(token);
            return dispute;
        }, ct);
    }

    public async Task<P2PFeedback> LeaveFeedbackAsync(Guid userId, Guid orderId, bool positive, string? comment, CancellationToken ct = default)
    {
        var order = await db.P2POrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || (order.BuyerId != userId && order.SellerId != userId))
        {
            throw AppException.NotFound("Order");
        }

        if (order.Status is not (P2POrderStatus.Completed or P2POrderStatus.ResolvedToBuyer or P2POrderStatus.ResolvedToSeller))
        {
            throw AppException.Conflict(ErrorCodes.InvalidState, "Feedback can be left once an order is finished.");
        }

        if (await db.P2PFeedback.AnyAsync(f => f.OrderId == orderId && f.FromUserId == userId, ct))
        {
            throw AppException.Conflict(ErrorCodes.DuplicateRequest, "You already left feedback for this order.");
        }

        var feedback = new P2PFeedback
        {
            Id = Ids.New(),
            OrderId = orderId,
            FromUserId = userId,
            ToUserId = order.BuyerId == userId ? order.SellerId : order.BuyerId,
            Positive = positive,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(500, comment.Trim().Length)],
            CreatedAt = clock.GetUtcNow(),
        };
        db.P2PFeedback.Add(feedback);
        await db.SaveChangesAsync(ct);
        return feedback;
    }

    private async Task PostReleaseAsync(P2POrder order, string step, CancellationToken ct)
    {
        var journal = new JournalBuilder(JournalTypes.P2PEscrowRelease)
            .ForUser(order.BuyerId)
            .Reference("p2p_order", order.Id)
            .Idempotent($"p2p_order:{order.Id}:settle")
            .Describe($"P2P order {order.OrderNumber} {step}")
            .System(SystemAccounts.P2PEscrow, order.Asset, -order.EscrowAmount)
            .System(SystemAccounts.Fees, order.Asset, order.Fee);

        if (order.AdSide == P2PAdSide.Sell)
        {
            // Escrow held quantity + maker (seller) fee; buyer receives the full quantity.
            journal.User(order.BuyerId, order.Asset, AccountKind.Available, order.Quantity);
        }
        else
        {
            // Escrow held the quantity; maker (buyer) pays the fee out of what they receive.
            journal.User(order.BuyerId, order.Asset, AccountKind.Available, order.Quantity - order.Fee);
        }

        await ledger.PostAsync(journal, ct);
    }

    private async Task PostRefundAsync(P2POrder order, string step, CancellationToken ct)
    {
        var journal = new JournalBuilder(JournalTypes.P2PEscrowRefund)
            .ForUser(order.SellerId)
            .Reference("p2p_order", order.Id)
            .Idempotent($"p2p_order:{order.Id}:settle")
            .Describe($"P2P order {order.OrderNumber} {step}")
            .System(SystemAccounts.P2PEscrow, order.Asset, -order.EscrowAmount);

        var ad = await ads.LockAsync(order.AdId, ct) ?? throw new InvalidOperationException($"Ad {order.AdId} missing for order {order.Id}.");
        var adOpen = ad.Status != P2PAdStatus.Closed;

        if (order.AdSide == P2PAdSide.Sell)
        {
            if (adOpen)
            {
                journal.User(order.SellerId, order.Asset, AccountKind.P2PReserve, order.EscrowAmount);
                ad.ReservedAmount += order.EscrowAmount;
                ad.RemainingQuantity += order.Quantity;
            }
            else
            {
                journal.User(order.SellerId, order.Asset, AccountKind.Available, order.EscrowAmount);
            }
        }
        else
        {
            journal.User(order.SellerId, order.Asset, AccountKind.Available, order.EscrowAmount);
            if (adOpen)
            {
                ad.RemainingQuantity += order.Quantity;
            }
        }

        ad.UpdatedAt = clock.GetUtcNow();
        await ledger.PostAsync(journal, ct);
    }

    internal async Task<P2POrder?> LockOrderAsync(Guid orderId, CancellationToken ct) =>
        (await db.P2POrders.FromSqlInterpolated($"SELECT * FROM p2p_orders WHERE id = {orderId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();

    private async Task<string> NewOrderNumberAsync(DateTimeOffset now, CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var candidate = $"{now:yyMMdd}{Random.Shared.Next(100_000, 999_999)}";
            if (!await db.P2POrders.AnyAsync(o => o.OrderNumber == candidate, ct))
            {
                return candidate;
            }
        }

        return $"{now:yyMMdd}{Ids.RandomToken(8).ToUpperInvariant()}";
    }

    private static string Describe(P2POrderStatus status) => status switch
    {
        P2POrderStatus.PendingPayment => "waiting for payment",
        P2POrderStatus.Paid => "marked as paid",
        P2POrderStatus.Completed => "already completed",
        P2POrderStatus.Cancelled => "cancelled",
        P2POrderStatus.Expired => "expired",
        P2POrderStatus.Disputed => "in dispute",
        P2POrderStatus.ResolvedToBuyer or P2POrderStatus.ResolvedToSeller => "resolved",
        _ => status.ToString(),
    };
}
