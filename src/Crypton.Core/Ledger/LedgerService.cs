using Crypton.Core.Assets;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Crypton.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Ledger;

public sealed class DuplicateJournalException(string idempotencyKey)
    : Exception($"A journal entry with idempotency key '{idempotencyKey}' already exists.")
{
    public string IdempotencyKey { get; } = idempotencyKey;
}

public sealed record BalanceView(string Asset, decimal Available, decimal WithdrawalHold, decimal P2PReserve)
{
    public decimal Locked => WithdrawalHold + P2PReserve;

    public decimal Total => Available + Locked;
}

/// <summary>
/// Double-entry ledger. Every posting must run inside a database transaction. Balances are changed with
/// guarded atomic updates (<c>balance + delta &gt;= 0</c> unless the account may go negative), in a fixed
/// account order to avoid deadlocks, and a CHECK constraint backs this up at the database level.
/// </summary>
public sealed class LedgerService(CryptonDbContext db, AssetCatalog assets, TimeProvider clock)
{
    public async Task<JournalEntry> PostAsync(JournalBuilder journal, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Ledger postings must run inside a database transaction.");
        }

        if (journal.Legs.Count < 2)
        {
            throw new InvalidOperationException("A journal entry needs at least two legs.");
        }

        foreach (var group in journal.Legs.GroupBy(l => l.Asset))
        {
            var precision = await assets.PrecisionAsync(group.Key, ct);
            foreach (var leg in group)
            {
                if (leg.Amount == 0)
                {
                    throw new InvalidOperationException($"Zero amount leg on {leg.AccountKey}.");
                }

                if (!MoneyMath.HasMaxDecimals(leg.Amount, precision))
                {
                    throw new InvalidOperationException($"Leg amount {leg.Amount} exceeds {group.Key} precision of {precision} decimals.");
                }
            }

            var sum = group.Sum(l => l.Amount);
            if (sum != 0)
            {
                throw new InvalidOperationException($"Unbalanced journal '{journal.Type}': {group.Key} legs sum to {sum}.");
            }
        }

        if (journal.IdempotencyKey is not null &&
            await db.JournalEntries.AsNoTracking().AnyAsync(j => j.IdempotencyKey == journal.IdempotencyKey, ct))
        {
            throw new DuplicateJournalException(journal.IdempotencyKey);
        }

        var now = clock.GetUtcNow();

        // Net legs per account; a journal may touch the same account more than once.
        var deltas = journal.Legs
            .GroupBy(l => l.AccountKey)
            .Select(g => (Leg: g.First(), Delta: g.Sum(x => x.Amount)))
            .ToList();

        var accounts = await EnsureAccountsAsync(deltas.Select(d => d.Leg).ToList(), now, ct);

        foreach (var (leg, delta) in deltas.OrderBy(d => accounts[d.Leg.AccountKey].Id))
        {
            if (delta == 0)
            {
                continue;
            }

            var account = accounts[leg.AccountKey];
            var accountId = account.Id;
            var updated = await db.LedgerAccounts
                .Where(a => a.Id == accountId && (a.AllowNegative || a.Balance + delta >= 0))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(a => a.Balance, a => a.Balance + delta)
                        .SetProperty(a => a.UpdatedAt, now),
                    ct);

            if (updated != 1)
            {
                throw InsufficientBalance(leg);
            }
        }

        var entry = new JournalEntry
        {
            Id = Ids.New(),
            Type = journal.Type,
            IdempotencyKey = journal.IdempotencyKey,
            ReferenceType = journal.ReferenceType,
            ReferenceId = journal.ReferenceId,
            UserId = journal.UserId,
            Description = journal.Description,
            CreatedAt = now,
        };

        foreach (var leg in journal.Legs)
        {
            entry.Postings.Add(new Posting
            {
                JournalEntryId = entry.Id,
                AccountId = accounts[leg.AccountKey].Id,
                UserId = leg.UserId,
                Kind = leg.Kind,
                SystemCode = leg.SystemCode,
                Asset = leg.Asset,
                Amount = leg.Amount,
                CreatedAt = now,
            });
        }

        db.JournalEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (journal.IdempotencyKey is not null && TransactionExtensions.IsUniqueViolation(ex))
        {
            throw new DuplicateJournalException(journal.IdempotencyKey);
        }

        return entry;
    }

    public async Task<IReadOnlyList<BalanceView>> GetUserBalancesAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => new { a.Asset, a.Kind, a.Balance })
            .ToListAsync(ct);

        var all = await assets.GetAllAsync(ct);
        return all
            .Select(asset => new BalanceView(
                asset.Code,
                rows.Where(r => r.Asset == asset.Code && r.Kind == AccountKind.Available).Sum(r => r.Balance),
                rows.Where(r => r.Asset == asset.Code && r.Kind == AccountKind.WithdrawalHold).Sum(r => r.Balance),
                rows.Where(r => r.Asset == asset.Code && r.Kind == AccountKind.P2PReserve).Sum(r => r.Balance)))
            .ToList();
    }

    public async Task<decimal> GetUserBalanceAsync(Guid userId, string asset, AccountKind kind, CancellationToken ct = default)
    {
        var key = LedgerAccount.UserKey(userId, asset, kind);
        return await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.AccountKey == key)
            .Select(a => (decimal?)a.Balance)
            .FirstOrDefaultAsync(ct) ?? 0m;
    }

    public async Task<decimal> GetSystemBalanceAsync(string systemCode, string asset, CancellationToken ct = default)
    {
        var key = LedgerAccount.SystemKey(systemCode, asset);
        return await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.AccountKey == key)
            .Select(a => (decimal?)a.Balance)
            .FirstOrDefaultAsync(ct) ?? 0m;
    }

    private async Task<Dictionary<string, LedgerAccount>> EnsureAccountsAsync(IReadOnlyList<LedgerLeg> legs, DateTimeOffset now, CancellationToken ct)
    {
        var keys = legs.Select(l => l.AccountKey).Distinct().ToList();
        var existing = await LoadAccountsAsync(keys, ct);

        foreach (var leg in legs.Where(l => !existing.ContainsKey(l.AccountKey)))
        {
            var allowNegative = leg.OwnerType == AccountOwnerType.System && SystemAccounts.NegativeAllowed.Contains(leg.SystemCode);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO ledger_accounts (id, account_key, owner_type, user_id, system_code, asset, kind, balance, allow_negative, created_at, updated_at)
                VALUES ({Ids.New()}, {leg.AccountKey}, {leg.OwnerType.ToString()}, {leg.UserId}, {leg.SystemCode}, {leg.Asset}, {leg.Kind.ToString()}, {0m}, {allowNegative}, {now}, {now})
                ON CONFLICT (account_key) DO NOTHING
                """,
                ct);
        }

        if (existing.Count != keys.Count)
        {
            existing = await LoadAccountsAsync(keys, ct);
        }

        if (existing.Count != keys.Count)
        {
            throw new InvalidOperationException("Failed to create ledger accounts.");
        }

        return existing;
    }

    private async Task<Dictionary<string, LedgerAccount>> LoadAccountsAsync(List<string> keys, CancellationToken ct) =>
        await db.LedgerAccounts.AsNoTracking()
            .Where(a => keys.Contains(a.AccountKey))
            .ToDictionaryAsync(a => a.AccountKey, ct);

    private static AppException InsufficientBalance(LedgerLeg leg)
    {
        var details = new Dictionary<string, object?> { ["asset"] = leg.Asset };
        if (leg.OwnerType == AccountOwnerType.System)
        {
            return leg.SystemCode == SystemAccounts.Treasury
                ? new AppException(ErrorCodes.InsufficientLiquidity, $"Not enough {leg.Asset} liquidity is available right now. Try a smaller amount.", 409, details)
                : new AppException(ErrorCodes.InsufficientFunds, $"System account {leg.SystemCode} has insufficient {leg.Asset}.", 409, details);
        }

        return leg.Kind == AccountKind.Available
            ? new AppException(ErrorCodes.InsufficientFunds, $"Insufficient {leg.Asset} balance.", 409, details)
            : new AppException(ErrorCodes.InsufficientFunds, $"Insufficient {leg.Asset} in {leg.Kind}.", 409, details);
    }
}
