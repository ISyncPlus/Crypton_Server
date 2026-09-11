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

namespace Crypton.Core.P2P;

public sealed class P2PAdService(
    CryptonDbContext db,
    AssetCatalog assets,
    LedgerService ledger,
    PriceService prices,
    SettingsService settings,
    LimitService limits,
    AccountGuard guard,
    TraderStatsService traders,
    TimeProvider clock)
{
    private const int MaxOpenAdsPerUser = 10;

    public async Task<P2PAd> CreateAsync(Guid userId, CreateAdRequest request, CancellationToken ct = default)
    {
        var user = await guard.RequireActiveUserAsync(userId, ct);
        var cfg = await settings.GetAsync<P2PSettings>(ct);
        await EnsureP2PAllowedAsync(user, cfg, ct);

        var asset = await assets.GetAsync(request.Asset, ct);
        if (asset.IsFiat || !asset.TradingEnabled)
        {
            throw new AppException(ErrorCodes.AssetDisabled, "P2P trading is not available for this asset.", 409);
        }

        var openAds = await db.P2PAds.CountAsync(a => a.UserId == userId && a.Status != P2PAdStatus.Closed, ct);
        if (openAds >= MaxOpenAdsPerUser)
        {
            throw AppException.Validation($"You can have at most {MaxOpenAdsPerUser} open ads.");
        }

        if (request.TotalQuantity <= 0 || !MoneyMath.HasMaxDecimals(request.TotalQuantity, asset.Precision))
        {
            throw new AppException(ErrorCodes.InvalidAmount, $"Quantity must be positive with at most {asset.Precision} decimals.");
        }

        var market = await prices.GetFreshPriceAsync(asset.Code, TimeSpan.FromMinutes(10), ct);
        await ValidateTermsAsync(userId, request.Side, request.PriceType, request.FixedPrice, request.FloatingMarginBps, request.MinOrderFiat,
            request.MaxOrderFiat, request.PaymentWindowMinutes, request.PaymentMethodIds, request.Terms, market.PriceNgn, cfg, ct);

        await traders.EnsureDisplayNameAsync(userId, ct);

        var now = clock.GetUtcNow();
        var ad = new P2PAd
        {
            Id = Ids.New(),
            UserId = userId,
            Side = request.Side,
            Asset = asset.Code,
            FiatCurrency = AssetCodes.NGN,
            PriceType = request.PriceType,
            FixedPrice = request.PriceType == P2PPriceType.Fixed ? request.FixedPrice : null,
            FloatingMarginBps = request.PriceType == P2PPriceType.Floating ? request.FloatingMarginBps : 0,
            TotalQuantity = request.TotalQuantity,
            RemainingQuantity = request.TotalQuantity,
            MinOrderFiat = request.MinOrderFiat,
            MaxOrderFiat = request.MaxOrderFiat,
            PaymentWindowMinutes = request.PaymentWindowMinutes,
            PaymentMethodIds = request.Side == P2PAdSide.Sell ? request.PaymentMethodIds.Distinct().ToArray() : [],
            Terms = string.IsNullOrWhiteSpace(request.Terms) ? null : request.Terms.Trim(),
            Status = P2PAdStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        return await db.InTransactionAsync(async token =>
        {
            if (ad.Side == P2PAdSide.Sell)
            {
                ad.ReservedAmount = P2PPricing.SellAdReserve(ad.TotalQuantity, cfg.MakerFeeBps, asset.Precision);
                await ledger.PostAsync(
                    new JournalBuilder(JournalTypes.P2PAdReserve)
                        .ForUser(userId)
                        .Reference("p2p_ad", ad.Id)
                        .Idempotent($"p2p_ad:{ad.Id}:reserve")
                        .Describe($"Reserve for P2P sell ad ({ad.TotalQuantity} {ad.Asset} + fees)")
                        .User(userId, ad.Asset, AccountKind.Available, -ad.ReservedAmount)
                        .User(userId, ad.Asset, AccountKind.P2PReserve, ad.ReservedAmount),
                    token);
            }

            db.P2PAds.Add(ad);
            await db.SaveChangesAsync(token);
            return ad;
        }, ct);
    }

    public async Task<P2PAd> UpdateAsync(Guid userId, Guid adId, UpdateAdRequest request, CancellationToken ct = default)
    {
        var user = await guard.RequireActiveUserAsync(userId, ct);
        var cfg = await settings.GetAsync<P2PSettings>(ct);

        return await db.InTransactionAsync(async token =>
        {
            var ad = await LockAsync(adId, token);
            if (ad is null || ad.UserId != userId)
            {
                throw AppException.NotFound("Ad");
            }

            if (ad.Status == P2PAdStatus.Closed)
            {
                throw AppException.Conflict(ErrorCodes.InvalidState, "Closed ads cannot be edited.");
            }

            if (request.Status == P2PAdStatus.Closed)
            {
                throw AppException.Validation("Use the close action to close an ad.");
            }

            if (request.Status == P2PAdStatus.Active)
            {
                await EnsureP2PAllowedAsync(user, cfg, token);
                if (ad.SuspendedByAdmin)
                {
                    throw AppException.Forbidden("This ad was suspended by compliance.");
                }
            }

            var market = await prices.GetFreshPriceAsync(ad.Asset, TimeSpan.FromMinutes(10), token);
            await ValidateTermsAsync(userId, ad.Side, request.PriceType, request.FixedPrice, request.FloatingMarginBps, request.MinOrderFiat,
                request.MaxOrderFiat, request.PaymentWindowMinutes, request.PaymentMethodIds, request.Terms, market.PriceNgn, cfg, token);

            ad.PriceType = request.PriceType;
            ad.FixedPrice = request.PriceType == P2PPriceType.Fixed ? request.FixedPrice : null;
            ad.FloatingMarginBps = request.PriceType == P2PPriceType.Floating ? request.FloatingMarginBps : 0;
            ad.MinOrderFiat = request.MinOrderFiat;
            ad.MaxOrderFiat = request.MaxOrderFiat;
            ad.PaymentWindowMinutes = request.PaymentWindowMinutes;
            ad.PaymentMethodIds = ad.Side == P2PAdSide.Sell ? request.PaymentMethodIds.Distinct().ToArray() : [];
            ad.Terms = string.IsNullOrWhiteSpace(request.Terms) ? null : request.Terms.Trim();
            ad.Status = request.Status;
            ad.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(token);
            return ad;
        }, ct);
    }

    public async Task<P2PAd> CloseAsync(Guid userId, Guid adId, CancellationToken ct = default) =>
        await db.InTransactionAsync(async token =>
        {
            var ad = await LockAsync(adId, token);
            if (ad is null || ad.UserId != userId)
            {
                throw AppException.NotFound("Ad");
            }

            await CloseLockedAsync(ad, token);
            await db.SaveChangesAsync(token);
            return ad;
        }, ct);

    internal async Task CloseLockedAsync(P2PAd ad, CancellationToken ct)
    {
        if (ad.Status == P2PAdStatus.Closed)
        {
            return;
        }

        if (ad.Side == P2PAdSide.Sell && ad.ReservedAmount > 0)
        {
            await ledger.PostAsync(
                new JournalBuilder(JournalTypes.P2PAdRelease)
                    .ForUser(ad.UserId)
                    .Reference("p2p_ad", ad.Id)
                    .Idempotent($"p2p_ad:{ad.Id}:close")
                    .Describe("Release of unused P2P ad reserve")
                    .User(ad.UserId, ad.Asset, AccountKind.P2PReserve, -ad.ReservedAmount)
                    .User(ad.UserId, ad.Asset, AccountKind.Available, ad.ReservedAmount),
                ct);
        }

        ad.ReservedAmount = 0;
        ad.RemainingQuantity = 0;
        ad.Status = P2PAdStatus.Closed;
        ad.UpdatedAt = clock.GetUtcNow();
    }

    public async Task<PagedResult<MarketAd>> ListMarketAsync(Guid? viewerId, MarketFilter filter, CancellationToken ct = default)
    {
        var asset = await assets.GetAsync(filter.Asset, ct);
        var market = await prices.GetFreshPriceAsync(asset.Code, TimeSpan.FromMinutes(30), ct);

        var candidates = await (from ad in db.P2PAds.AsNoTracking()
                                join maker in db.Users.AsNoTracking() on ad.UserId equals maker.Id
                                where ad.Status == P2PAdStatus.Active && !ad.SuspendedByAdmin && ad.Side == filter.AdSide &&
                                      ad.Asset == asset.Code && ad.RemainingQuantity > 0 && maker.Status == UserStatus.Active &&
                                      (viewerId == null || ad.UserId != viewerId)
                                select ad)
            .Take(500)
            .ToListAsync(ct);

        var priced = candidates
            .Select(ad =>
            {
                var price = P2PPricing.EffectivePrice(ad, market.PriceNgn);
                var maxFiat = Math.Min(ad.MaxOrderFiat, MoneyMath.Floor(ad.RemainingQuantity * price, 2));
                return (ad, price, maxFiat);
            })
            .Where(x => x.price > 0 && x.maxFiat >= x.ad.MinOrderFiat)
            .Where(x => filter.FiatAmount is null || (filter.FiatAmount >= x.ad.MinOrderFiat && filter.FiatAmount <= x.maxFiat));

        // Makers selling: best (lowest) price first. Makers buying: best (highest) price first.
        priced = filter.AdSide == P2PAdSide.Sell ? priced.OrderBy(x => x.price) : priced.OrderByDescending(x => x.price);
        var all = priced.ToList();

        var page = new PageRequest(filter.Page, filter.PageSize);
        var slice = all.Skip(page.Skip).Take(page.SafePageSize).ToList();
        var stats = await traders.GetManyAsync(slice.Select(x => x.ad.UserId).ToList(), ct);
        var methodIds = slice.SelectMany(x => x.ad.PaymentMethodIds).Distinct().ToList();
        var banks = await db.BankAccounts.AsNoTracking().Where(b => methodIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.BankName, ct);

        var items = slice.Select(x => new MarketAd(
                x.ad,
                x.price,
                x.maxFiat,
                stats[x.ad.UserId],
                x.ad.PaymentMethodIds.Where(banks.ContainsKey).Select(id => banks[id]).Distinct().ToList()))
            .ToList();

        return new PagedResult<MarketAd>(items, page.SafePage, page.SafePageSize, all.Count);
    }

    public async Task<MarketAd> GetPublicAsync(Guid adId, CancellationToken ct = default)
    {
        var ad = await db.P2PAds.AsNoTracking().FirstOrDefaultAsync(a => a.Id == adId, ct) ?? throw AppException.NotFound("Ad");
        var market = await prices.GetFreshPriceAsync(ad.Asset, TimeSpan.FromMinutes(30), ct);
        var price = P2PPricing.EffectivePrice(ad, market.PriceNgn);
        var stats = await traders.GetAsync(ad.UserId, ct);
        var banks = await db.BankAccounts.AsNoTracking().Where(b => ad.PaymentMethodIds.Contains(b.Id)).Select(b => b.BankName).Distinct().ToListAsync(ct);
        return new MarketAd(ad, price, Math.Min(ad.MaxOrderFiat, MoneyMath.Floor(ad.RemainingQuantity * price, 2)), stats, banks);
    }

    public async Task<IReadOnlyList<(P2PAd Ad, decimal EffectivePrice)>> ListMineAsync(Guid userId, CancellationToken ct = default)
    {
        var ads = await db.P2PAds.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync(ct);
        var result = new List<(P2PAd, decimal)>();
        foreach (var group in ads.GroupBy(a => a.Asset))
        {
            decimal market = 0;
            try
            {
                market = (await prices.GetFreshPriceAsync(group.Key, TimeSpan.FromHours(1), ct)).PriceNgn;
            }
            catch (AppException)
            {
            }

            result.AddRange(group.Select(a => (a, P2PPricing.EffectivePrice(a, market))));
        }

        return result.OrderByDescending(x => x.Item1.CreatedAt).ToList();
    }

    public async Task<P2PAd?> LockAsync(Guid adId, CancellationToken ct) =>
        (await db.P2PAds.FromSqlInterpolated($"SELECT * FROM p2p_ads WHERE id = {adId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();

    internal async Task EnsureP2PAllowedAsync(AppUser user, P2PSettings cfg, CancellationToken ct)
    {
        var tierLimits = await limits.GetTierLimitsAsync(user.KycTier, ct);
        if (user.KycTier < cfg.MinKycTier || !tierLimits.P2PEnabled)
        {
            throw new AppException(ErrorCodes.KycRequired, "Verify your identity to trade on the P2P marketplace.", 403,
                new Dictionary<string, object?> { ["requiredTier"] = Math.Max(cfg.MinKycTier, 1) });
        }
    }

    private async Task ValidateTermsAsync(
        Guid userId,
        P2PAdSide side,
        P2PPriceType priceType,
        decimal? fixedPrice,
        int floatingMarginBps,
        decimal minOrderFiat,
        decimal maxOrderFiat,
        int paymentWindowMinutes,
        IReadOnlyList<Guid> paymentMethodIds,
        string? terms,
        decimal marketPrice,
        P2PSettings cfg,
        CancellationToken ct)
    {
        if (priceType == P2PPriceType.Fixed)
        {
            if (fixedPrice is not { } price || price <= 0 || !MoneyMath.HasMaxDecimals(price, 2))
            {
                throw AppException.Validation("Enter a valid fixed price in naira.");
            }

            var deviation = Math.Abs(price - marketPrice) / marketPrice * 10_000m;
            if (deviation > cfg.MaxFloatingMarginBps)
            {
                throw AppException.Validation($"The price must be within {cfg.MaxFloatingMarginBps / 100m:0.##}% of the market price (NGN {marketPrice:N2}).");
            }
        }
        else if (Math.Abs(floatingMarginBps) > cfg.MaxFloatingMarginBps)
        {
            throw AppException.Validation($"The margin must be between -{cfg.MaxFloatingMarginBps} and {cfg.MaxFloatingMarginBps} basis points.");
        }

        if (minOrderFiat < cfg.MinOrderFiat || !MoneyMath.HasMaxDecimals(minOrderFiat, 2))
        {
            throw AppException.Validation($"The minimum order must be at least NGN {cfg.MinOrderFiat:N0}.");
        }

        if (maxOrderFiat < minOrderFiat || !MoneyMath.HasMaxDecimals(maxOrderFiat, 2))
        {
            throw AppException.Validation("The maximum order must be greater than or equal to the minimum order.");
        }

        if (!cfg.PaymentWindowsMinutes.Contains(paymentWindowMinutes))
        {
            throw AppException.Validation($"Payment window must be one of: {string.Join(", ", cfg.PaymentWindowsMinutes)} minutes.");
        }

        if (terms is { Length: > 1000 })
        {
            throw AppException.Validation("Terms must be at most 1000 characters.");
        }

        if (side == P2PAdSide.Sell)
        {
            var ids = paymentMethodIds.Distinct().ToList();
            if (ids.Count is 0 or > 5)
            {
                throw AppException.Validation("Choose between 1 and 5 bank accounts where buyers will pay you.");
            }

            var owned = await db.BankAccounts.CountAsync(b => ids.Contains(b.Id) && b.UserId == userId && !b.IsDeleted, ct);
            if (owned != ids.Count)
            {
                throw AppException.Validation("One or more selected bank accounts are not available.");
            }
        }
    }
}
