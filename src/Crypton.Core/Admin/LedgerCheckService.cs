using System.Data.Common;
using Crypton.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Crypton.Core.Admin;

public sealed record LedgerCheck(string Name, bool Ok, IReadOnlyList<string> Problems);

public sealed record LedgerCheckReport(bool Ok, DateTimeOffset CheckedAt, IReadOnlyList<LedgerCheck> Checks);

/// <summary>Verifies ledger invariants directly in SQL. Every check must hold at all times.</summary>
public sealed class LedgerCheckService(CryptonDbContext db, TimeProvider clock)
{
    private static readonly (string Name, string Sql)[] Checks =
    [
        ("Every asset nets to zero",
            "SELECT asset || ' sums to ' || SUM(balance) FROM ledger_accounts GROUP BY asset HAVING SUM(balance) <> 0"),
        ("Account balances equal their postings",
            """
            SELECT a.account_key || ': balance ' || a.balance || ' vs postings ' || COALESCE(p.total, 0)
            FROM ledger_accounts a
            LEFT JOIN (SELECT account_id, SUM(amount) AS total FROM postings GROUP BY account_id) p ON p.account_id = a.id
            WHERE a.balance <> COALESCE(p.total, 0)
            LIMIT 50
            """),
        ("Every journal entry balances per asset",
            "SELECT journal_entry_id || ' ' || asset || ' sums to ' || SUM(amount) FROM postings GROUP BY journal_entry_id, asset HAVING SUM(amount) <> 0 LIMIT 50"),
        ("No forbidden negative balances",
            "SELECT account_key || ' is ' || balance FROM ledger_accounts WHERE NOT allow_negative AND balance < 0 LIMIT 50"),
        ("Withdrawal holds match open withdrawals",
            """
            WITH expected AS (
                SELECT user_id, asset, SUM(amount + fee) AS total FROM crypto_withdrawals
                WHERE status IN ('PendingReview','Approved','Broadcasting') OR (status = 'NeedsAttention' AND broadcast_at IS NULL)
                GROUP BY user_id, asset
                UNION ALL
                SELECT user_id, currency, SUM(amount + fee) FROM fiat_withdrawals
                WHERE status IN ('PendingReview','Approved','Processing','NeedsAttention')
                GROUP BY user_id, currency
            ), exp AS (SELECT user_id, asset, SUM(total) AS total FROM expected GROUP BY user_id, asset),
            actual AS (SELECT user_id, asset, balance FROM ledger_accounts WHERE kind = 'WithdrawalHold')
            SELECT COALESCE(e.user_id, a.user_id) || ' ' || COALESCE(e.asset, a.asset) || ': hold ' || COALESCE(a.balance, 0) || ' vs open ' || COALESCE(e.total, 0)
            FROM exp e FULL OUTER JOIN actual a ON a.user_id = e.user_id AND a.asset = e.asset
            WHERE COALESCE(a.balance, 0) <> COALESCE(e.total, 0)
            LIMIT 50
            """),
        ("P2P reserves match open sell ads",
            """
            WITH exp AS (SELECT user_id, asset, SUM(reserved_amount) AS total FROM p2p_ads WHERE side = 'Sell' AND status <> 'Closed' GROUP BY user_id, asset),
            actual AS (SELECT user_id, asset, balance FROM ledger_accounts WHERE kind = 'P2PReserve')
            SELECT COALESCE(e.user_id, a.user_id) || ' ' || COALESCE(e.asset, a.asset) || ': reserve ' || COALESCE(a.balance, 0) || ' vs ads ' || COALESCE(e.total, 0)
            FROM exp e FULL OUTER JOIN actual a ON a.user_id = e.user_id AND a.asset = e.asset
            WHERE COALESCE(a.balance, 0) <> COALESCE(e.total, 0)
            LIMIT 50
            """),
        ("P2P escrow matches open orders",
            """
            WITH exp AS (SELECT asset, SUM(escrow_amount) AS total FROM p2p_orders WHERE status IN ('PendingPayment','Paid','Disputed') GROUP BY asset),
            actual AS (SELECT asset, balance FROM ledger_accounts WHERE system_code = 'p2p_escrow')
            SELECT COALESCE(e.asset, a.asset) || ': escrow ' || COALESCE(a.balance, 0) || ' vs orders ' || COALESCE(e.total, 0)
            FROM exp e FULL OUTER JOIN actual a ON a.asset = e.asset
            WHERE COALESCE(a.balance, 0) <> COALESCE(e.total, 0)
            """),
    ];

    public async Task<LedgerCheckReport> RunAsync(CancellationToken ct = default)
    {
        var results = new List<LedgerCheck>();
        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            opened = true;
        }

        try
        {
            foreach (var (name, sql) in Checks)
            {
                var problems = new List<string>();
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = sql;
                if (db.Database.CurrentTransaction is { } tx)
                {
                    command.Transaction = tx.GetDbTransaction();
                }

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    problems.Add(reader.IsDBNull(0) ? "(null)" : reader.GetString(0));
                }

                results.Add(new LedgerCheck(name, problems.Count == 0, problems));
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }

        return new LedgerCheckReport(results.All(r => r.Ok), clock.GetUtcNow(), results);
    }
}
