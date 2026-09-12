using Crypton.Core.Assets;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Crypton.Core.Kyc;
using Crypton.Core.Ledger;
using Crypton.Core.Pricing;
using Crypton.Core.Security;
using Crypton.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Trading;

public sealed record QuoteRequest(TradeKind Kind, string FromAsset, string ToAsset, decimal Amount, AmountSide Side);

/// <summary>
/// Instant buy / sell / swap against the platform treasury. A quote locks a price for a short time;
/// executing it moves funds atomically and can only happen once.
/// </summary>
public sealed class TradeService(
    CryptonDbContext db,
    AssetCatalog assets,
    PriceService prices,
    SettingsService settings,
    LedgerService ledger,
    LimitService limits,
    AccountGuard guard,
    TimeProvider clock)
{
    public async Task<Quote> CreateQuoteAsync(Guid userId, QuoteRequest request, CancellationToken ct = default)
    {
        await guard.RequireActiveUserAsync(userId, ct);
        var cfg = await settings.GetAsync<TradingSettings>(ct);
        var from = await assets.GetAsync(request.FromAsset, ct);
        var to = await assets.GetAsync(request.ToAsset, ct);

        if (!from.TradingEnabled || !to.TradingEnabled)
        {
            throw new AppException(ErrorCodes.AssetDisabled, "Trading is currently paused for this pair.", 409);
        }

        if (request.Amount <= 0)
        {
            throw new AppException(ErrorCodes.InvalidAmount, "Amount must be greater than zero.");
        }

        var maxAge = TimeSpan.FromSeconds(cfg.MaxPriceAgeSeconds);
        var now = clock.GetUtcNow();
        var quote = new Quote
        {
            Id = Ids.New(),
            UserId = userId,
            Kind = request.Kind,
            FromAsset = from.Code,
            ToAsset = to.Code,
            AmountSide = request.Side,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(cfg.QuoteTtlSeconds),
        };

        switch (request.Kind)
        {
            case TradeKind.Buy:
                ValidatePair(from.IsFiat && !to.IsFiat, "A buy must spend NGN to receive a crypto asset.");
                await PriceBuyAsync(quote, from, to, request, cfg, maxAge, ct);
                break;

            case TradeKind.Sell:
                ValidatePair(!from.IsFiat && to.IsFiat, "A sell must spend a crypto asset to receive NGN.");
                if (request.Side != AmountSide.From)
                {
                    throw AppException.Validation("For sells, enter the amount of crypto to sell.");
                }

                await PriceSellAsync(quote, from, to, request, cfg, maxAge, ct);
                break;

            case TradeKind.Swap:
                ValidatePair(!from.IsFiat && !to.IsFiat && from.Code != to.Code, "A swap needs two different crypto assets.");
                if (request.Side != AmountSide.From)
                {
                    throw AppException.Validation("For swaps, enter the amount you want to convert.");
                }

                await PriceSwapAsync(quote, from, to, request, cfg, maxAge, ct);
                break;

            default:
                throw AppException.Validation("Unknown trade type.");
        }

        if (quote.NgnValue < cfg.MinOrderNgn)
        {
            throw new AppException(ErrorCodes.AmountTooSmall, $"The minimum order is NGN {cfg.MinOrderNgn:N0}.", 400,
                new Dictionary<string, object?> { ["minNgn"] = cfg.MinOrderNgn });
        }

        if (quote.NgnValue > cfg.MaxOrderNgn)
        {
            throw new AppException(ErrorCodes.AmountTooLarge, $"The maximum order is NGN {cfg.MaxOrderNgn:N0}.", 400,
                new Dictionary<string, object?> { ["maxNgn"] = cfg.MaxOrderNgn });
        }

        // Advisory liquidity check so the user learns early; execution re-checks atomically.
        var treasuryBalance = await ledger.GetSystemBalanceAsync(SystemAccounts.Treasury, quote.ToAsset, ct);
        if (treasuryBalance < quote.TreasuryToAmount)
        {
            throw new AppException(ErrorCodes.InsufficientLiquidity, $"Not enough {quote.ToAsset} liquidity is available right now. Try a smaller amount.", 409);
        }

        db.Quotes.Add(quote);
        await db.SaveChangesAsync(ct);
        return quote;
    }

    public async Task<TradeOrder> ExecuteAsync(Guid userId, Guid quoteId, string? clientOrderId, CancellationToken ct = default)
    {
        if (clientOrderId is { Length: > 64 })
        {
            throw AppException.Validation("clientOrderId must be at most 64 characters.");
        }

        var user = await guard.RequireActiveUserAsync(userId, ct);

        return await db.InTransactionAsync(async token =>
        {
            var quote = (await db.Quotes
                    .FromSqlInterpolated($"SELECT * FROM quotes WHERE id = {quoteId} FOR UPDATE")
                    .ToListAsync(token))
                .SingleOrDefault();

            if (quote is null || quote.UserId != userId)
            {
                throw AppException.NotFound("Quote");
            }

            if (clientOrderId is not null)
            {
                var existingByClientId = await db.TradeOrders.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.UserId == userId && o.ClientOrderId == clientOrderId, token);
                if (existingByClientId is not null)
                {
                    if (existingByClientId.QuoteId != quoteId)
                    {
                        throw AppException.Conflict(ErrorCodes.DuplicateRequest, "clientOrderId was already used for a different quote.");
                    }

                    return existingByClientId;
                }
            }

            if (quote.ConsumedAt is not null)
            {
                var existing = await db.TradeOrders.AsNoTracking().FirstOrDefaultAsync(o => o.QuoteId == quoteId, token);
                return existing ?? throw AppException.Conflict(ErrorCodes.QuoteUsed, "This quote has already been used.");
            }

            var now = clock.GetUtcNow();
            if (now > quote.ExpiresAt)
            {
                throw new AppException(ErrorCodes.QuoteExpired, "This quote has expired. Get a new quote to continue.", 409);
            }

            var from = await assets.GetAsync(quote.FromAsset, token);
            var to = await assets.GetAsync(quote.ToAsset, token);
            if (!from.TradingEnabled || !to.TradingEnabled)
            {
                throw new AppException(ErrorCodes.AssetDisabled, "Trading is currently paused for this pair.", 409);
            }

            await limits.EnsureWithinLimitAsync(userId, user.KycTier, LimitKind.Trade, quote.NgnValue, token);

            var orderId = Ids.New();
            var journal = BuildJournal(quote, orderId);
            var entry = await ledger.PostAsync(journal, token);

            quote.ConsumedAt = now;
            var order = new TradeOrder
            {
                Id = orderId,
                UserId = userId,
                QuoteId = quote.Id,
                ClientOrderId = clientOrderId,
                Kind = quote.Kind,
                FromAsset = quote.FromAsset,
                ToAsset = quote.ToAsset,
                FromAmount = quote.FromAmount,
                ToAmount = quote.ToAmount,
                Fee = quote.Fee,
                FeeAsset = quote.FeeAsset,
                Rate = quote.Rate,
                NgnValue = quote.NgnValue,
                JournalEntryId = entry.Id,
                CreatedAt = now,
            };
            db.TradeOrders.Add(order);
            await db.SaveChangesAsync(token);
            return order;
        }, ct);
    }

    internal static JournalBuilder BuildJournal(Quote quote, Guid orderId)
    {
        var type = quote.Kind switch
        {
            TradeKind.Buy => JournalTypes.TradeBuy,
            TradeKind.Sell => JournalTypes.TradeSell,
            _ => JournalTypes.TradeSwap,
        };

        var journal = new JournalBuilder(type)
            .ForUser(quote.UserId)
            .Reference("trade_order", orderId)
            .Idempotent($"trade:{quote.Id}")
            .Describe($"{quote.Kind} {MoneyMath.ToPlainString(quote.FromAmount)} {quote.FromAsset} -> {MoneyMath.ToPlainString(quote.ToAmount)} {quote.ToAsset}");

        switch (quote.Kind)
        {
            case TradeKind.Buy:
                // User pays FromAmount NGN = treasury net + fee; receives crypto from the treasury.
                journal
                    .User(quote.UserId, quote.FromAsset, AccountKind.Available, -quote.FromAmount)
                    .System(SystemAccounts.Treasury, quote.FromAsset, quote.TreasuryFromAmount)
                    .System(SystemAccounts.Fees, quote.FromAsset, quote.Fee)
                    .System(SystemAccounts.Treasury, quote.ToAsset, -quote.TreasuryToAmount)
                    .User(quote.UserId, quote.ToAsset, AccountKind.Available, quote.ToAmount);
                break;

            case TradeKind.Sell:
            case TradeKind.Swap:
                // User gives crypto to the treasury; treasury pays gross, fee is split off from it.
                journal
                    .User(quote.UserId, quote.FromAsset, AccountKind.Available, -quote.FromAmount)
                    .System(SystemAccounts.Treasury, quote.FromAsset, quote.TreasuryFromAmount)
                    .System(SystemAccounts.Treasury, quote.ToAsset, -quote.TreasuryToAmount)
                    .User(quote.UserId, quote.ToAsset, AccountKind.Available, quote.ToAmount)
                    .System(SystemAccounts.Fees, quote.ToAsset, quote.Fee);
                break;
        }

        return journal;
    }

    internal static void PriceBuy(Quote quote, Asset ngn, Asset crypto, decimal amount, AmountSide side, decimal marketPriceNgn, TradingSettings cfg)
    {
        var rate = MoneyMath.Ceil(marketPriceNgn * (1m + cfg.SpreadBps / 10_000m), ngn.Precision);
        decimal spend, fee, net, quantity;

        if (side == AmountSide.From)
        {
            if (!MoneyMath.HasMaxDecimals(amount, ngn.Precision))
            {
                throw new AppException(ErrorCodes.InvalidAmount, $"NGN amounts can have at most {ngn.Precision} decimals.");
            }

            spend = amount;
            fee = MoneyMath.Ceil(spend * cfg.BuyFeeBps / (10_000m + cfg.BuyFeeBps), ngn.Precision);
            net = spend - fee;
            quantity = MoneyMath.Floor(net / rate, crypto.Precision);
        }
        else
        {
            if (!MoneyMath.HasMaxDecimals(amount, crypto.Precision))
            {
                throw new AppException(ErrorCodes.InvalidAmount, $"{crypto.Code} amounts can have at most {crypto.Precision} decimals.");
            }

            quantity = amount;
            net = MoneyMath.Ceil(quantity * rate, ngn.Precision);
            fee = MoneyMath.Ceil(MoneyMath.Bps(net, cfg.BuyFeeBps), ngn.Precision);
            spend = net + fee;
        }

        if (quantity <= 0 || net <= 0)
        {
            throw new AppException(ErrorCodes.AmountTooSmall, "Amount is too small.");
        }

        quote.FeeAsset = ngn.Code;
        quote.Rate = rate;
        quote.FromAmount = spend;
        quote.ToAmount = quantity;
        quote.Fee = fee;
        quote.TreasuryFromAmount = net;
        quote.TreasuryToAmount = quantity;
        quote.NgnValue = spend;
    }

    internal static void PriceSell(Quote quote, Asset crypto, Asset ngn, decimal quantity, decimal marketPriceNgn, TradingSettings cfg)
    {
        if (!MoneyMath.HasMaxDecimals(quantity, crypto.Precision))
        {
            throw new AppException(ErrorCodes.InvalidAmount, $"{crypto.Code} amounts can have at most {crypto.Precision} decimals.");
        }

        var rate = MoneyMath.Floor(marketPriceNgn * (1m - cfg.SpreadBps / 10_000m), ngn.Precision);
        var gross = MoneyMath.Floor(quantity * rate, ngn.Precision);
        var fee = MoneyMath.Ceil(MoneyMath.Bps(gross, cfg.SellFeeBps), ngn.Precision);
        var receive = gross - fee;
        if (receive <= 0)
        {
            throw new AppException(ErrorCodes.AmountTooSmall, "Amount is too small.");
        }

        quote.FeeAsset = ngn.Code;
        quote.Rate = rate;
        quote.FromAmount = quantity;
        quote.ToAmount = receive;
        quote.Fee = fee;
        quote.TreasuryFromAmount = quantity;
        quote.TreasuryToAmount = gross;
        quote.NgnValue = gross;
    }

    internal static void PriceSwap(Quote quote, Asset from, Asset to, decimal quantity, decimal fromPriceNgn, decimal toPriceNgn, TradingSettings cfg)
    {
        if (!MoneyMath.HasMaxDecimals(quantity, from.Precision))
        {
            throw new AppException(ErrorCodes.InvalidAmount, $"{from.Code} amounts can have at most {from.Precision} decimals.");
        }

        var rawRate = fromPriceNgn / toPriceNgn * (1m - cfg.SpreadBps / 10_000m);
        var gross = MoneyMath.Floor(quantity * fromPriceNgn * (1m - cfg.SpreadBps / 10_000m) / toPriceNgn, to.Precision);
        var fee = MoneyMath.Ceil(MoneyMath.Bps(gross, cfg.SwapFeeBps), to.Precision);
        var receive = gross - fee;
        if (receive <= 0)
        {
            throw new AppException(ErrorCodes.AmountTooSmall, "Amount is too small.");
        }

        quote.FeeAsset = to.Code;
        quote.Rate = MoneyMath.RoundHalfUp(rawRate, 12);
        quote.FromAmount = quantity;
        quote.ToAmount = receive;
        quote.Fee = fee;
        quote.TreasuryFromAmount = quantity;
        quote.TreasuryToAmount = gross;
        quote.NgnValue = MoneyMath.RoundHalfUp(quantity * fromPriceNgn, 2);
    }

    private async Task PriceBuyAsync(Quote quote, Asset ngn, Asset crypto, QuoteRequest request, TradingSettings cfg, TimeSpan maxAge, CancellationToken ct)
    {
        var price = await prices.GetFreshPriceAsync(crypto.Code, maxAge, ct);
        PriceBuy(quote, ngn, crypto, request.Amount, request.Side, price.PriceNgn, cfg);
    }

    private async Task PriceSellAsync(Quote quote, Asset crypto, Asset ngn, QuoteRequest request, TradingSettings cfg, TimeSpan maxAge, CancellationToken ct)
    {
        var price = await prices.GetFreshPriceAsync(crypto.Code, maxAge, ct);
        PriceSell(quote, crypto, ngn, request.Amount, price.PriceNgn, cfg);
    }

    private async Task PriceSwapAsync(Quote quote, Asset from, Asset to, QuoteRequest request, TradingSettings cfg, TimeSpan maxAge, CancellationToken ct)
    {
        var fromPrice = await prices.GetFreshPriceAsync(from.Code, maxAge, ct);
        var toPrice = await prices.GetFreshPriceAsync(to.Code, maxAge, ct);
        PriceSwap(quote, from, to, request.Amount, fromPrice.PriceNgn, toPrice.PriceNgn, cfg);
    }

    private static void ValidatePair(bool valid, string message)
    {
        if (!valid)
        {
            throw AppException.Validation(message, "invalid_pair");
        }
    }
}
