using Crypton.Core.Assets;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Wallets;

public sealed record DepositAddressView(string Asset, string Network, string Address, int RequiredConfirmations, decimal MinDeposit, bool Simulated);

public sealed class DepositAddressService(CryptonDbContext db, ChainGatewayRegistry chains, AssetCatalog assets, TimeProvider clock)
{
    public async Task<DepositAddressView> GetOrCreateAsync(Guid userId, string assetCode, CancellationToken ct = default)
    {
        var asset = await assets.GetAsync(assetCode, ct);
        if (asset.IsFiat || asset.Network is null)
        {
            throw AppException.Validation("Use a naira deposit for NGN.");
        }

        if (!asset.DepositsEnabled)
        {
            throw new AppException(ErrorCodes.AssetDisabled, $"{asset.Code} deposits are paused.", 409);
        }

        var gateway = chains.Get(asset.Network);
        if (!gateway.IsConfigured)
        {
            throw new AppException(ErrorCodes.NotConfigured, $"{asset.Name} deposits are not configured yet.", 503);
        }

        var now = clock.GetUtcNow();
        var existing = await db.DepositAddresses.FirstOrDefaultAsync(a => a.UserId == userId && a.Network == asset.Network, ct);
        if (existing is null)
        {
            existing = await CreateAsync(userId, gateway, now, ct);
        }

        // Ask the watcher to check this address promptly for the next while.
        existing.WatchUntil = now.AddHours(2);
        await db.SaveChangesAsync(ct);

        return new DepositAddressView(asset.Code, asset.Network, existing.Address, asset.RequiredConfirmations, asset.MinDeposit, gateway.IsSimulated);
    }

    private async Task<DepositAddress> CreateAsync(Guid userId, IChainGateway gateway, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var nextIndex = (await db.DepositAddresses
                .Where(a => a.Network == gateway.Network)
                .MaxAsync(a => (int?)a.DerivationIndex, ct) ?? -1) + 1;

            var address = new DepositAddress
            {
                Id = Ids.New(),
                UserId = userId,
                Network = gateway.Network,
                DerivationIndex = nextIndex,
                Address = gateway.NormalizeAddress(gateway.DeriveDepositAddress(nextIndex)),
                CreatedAt = now,
            };

            db.DepositAddresses.Add(address);
            try
            {
                await db.SaveChangesAsync(ct);
                return address;
            }
            catch (DbUpdateException ex) when (TransactionExtensions.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                var mine = await db.DepositAddresses.FirstOrDefaultAsync(a => a.UserId == userId && a.Network == gateway.Network, ct);
                if (mine is not null)
                {
                    return mine;
                }

                // Another user took this index concurrently; try the next one.
            }
        }

        throw new InvalidOperationException("Could not allocate a deposit address.");
    }
}
