using System.Globalization;
using System.Text;
using Crypton.Core.Common;
using Crypton.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Crypton.Core.Admin;

/// <summary>CSV exports for finance and compliance.</summary>
public sealed class ReportService(CryptonDbContext db)
{
    public static readonly string[] Types = ["trades", "crypto-deposits", "crypto-withdrawals", "fiat-deposits", "fiat-withdrawals", "p2p-orders", "ledger"];
    private const int MaxRows = 100_000;

    public async Task WriteCsvAsync(string type, DateTimeOffset from, DateTimeOffset to, Stream output, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        switch (type)
        {
            case "trades":
                await WriteAsync(writer, ["id", "created_at", "user_id", "kind", "from_asset", "from_amount", "to_asset", "to_amount", "fee", "fee_asset", "rate", "ngn_value"],
                    db.TradeOrders.AsNoTracking().Where(o => o.CreatedAt >= from && o.CreatedAt < to).OrderBy(o => o.CreatedAt).Take(MaxRows)
                        .Select(o => new object?[] { o.Id, o.CreatedAt, o.UserId, o.Kind, o.FromAsset, o.FromAmount, o.ToAsset, o.ToAmount, o.Fee, o.FeeAsset, o.Rate, o.NgnValue }).AsAsyncEnumerable(), ct);
                break;
            case "crypto-deposits":
                await WriteAsync(writer, ["id", "detected_at", "credited_at", "user_id", "asset", "network", "amount", "status", "tx_hash", "output_index", "address", "confirmations", "ngn_value"],
                    db.CryptoDeposits.AsNoTracking().Where(d => d.DetectedAt >= from && d.DetectedAt < to).OrderBy(d => d.DetectedAt).Take(MaxRows)
                        .Select(d => new object?[] { d.Id, d.DetectedAt, d.CreditedAt, d.UserId, d.Asset, d.Network, d.Amount, d.Status, d.TxHash, d.OutputIndex, d.Address, d.Confirmations, d.NgnValue }).AsAsyncEnumerable(), ct);
                break;
            case "crypto-withdrawals":
                await WriteAsync(writer, ["id", "created_at", "broadcast_at", "confirmed_at", "user_id", "asset", "amount", "fee", "network_fee", "status", "to_address", "tx_hash", "ngn_value", "risk_summary"],
                    db.CryptoWithdrawals.AsNoTracking().Where(w => w.CreatedAt >= from && w.CreatedAt < to).OrderBy(w => w.CreatedAt).Take(MaxRows)
                        .Select(w => new object?[] { w.Id, w.CreatedAt, w.BroadcastAt, w.ConfirmedAt, w.UserId, w.Asset, w.Amount, w.Fee, w.NetworkFee, w.Status, w.ToAddress, w.TxHash, w.NgnValue, w.RiskSummary }).AsAsyncEnumerable(), ct);
                break;
            case "fiat-deposits":
                await WriteAsync(writer, ["id", "created_at", "completed_at", "user_id", "provider", "reference", "amount", "fee", "provider_fee", "status"],
                    db.FiatDeposits.AsNoTracking().Where(d => d.CreatedAt >= from && d.CreatedAt < to).OrderBy(d => d.CreatedAt).Take(MaxRows)
                        .Select(d => new object?[] { d.Id, d.CreatedAt, d.CompletedAt, d.UserId, d.Provider, d.Reference, d.Amount, d.Fee, d.ProviderFee, d.Status }).AsAsyncEnumerable(), ct);
                break;
            case "fiat-withdrawals":
                await WriteAsync(writer, ["id", "created_at", "completed_at", "user_id", "provider", "reference", "amount", "fee", "status", "bank_name", "account_number", "account_name", "failure_reason"],
                    db.FiatWithdrawals.AsNoTracking().Where(w => w.CreatedAt >= from && w.CreatedAt < to).OrderBy(w => w.CreatedAt).Take(MaxRows)
                        .Select(w => new object?[] { w.Id, w.CreatedAt, w.CompletedAt, w.UserId, w.Provider, w.Reference, w.Amount, w.Fee, w.Status, w.BankName, w.AccountNumber, w.AccountName, w.FailureReason }).AsAsyncEnumerable(), ct);
                break;
            case "p2p-orders":
                await WriteAsync(writer, ["id", "order_number", "created_at", "completed_at", "status", "ad_side", "asset", "quantity", "price", "fiat_amount", "fee", "buyer_id", "seller_id"],
                    db.P2POrders.AsNoTracking().Where(o => o.CreatedAt >= from && o.CreatedAt < to).OrderBy(o => o.CreatedAt).Take(MaxRows)
                        .Select(o => new object?[] { o.Id, o.OrderNumber, o.CreatedAt, o.CompletedAt, o.Status, o.AdSide, o.Asset, o.Quantity, o.Price, o.FiatAmount, o.Fee, o.BuyerId, o.SellerId }).AsAsyncEnumerable(), ct);
                break;
            case "ledger":
                await WriteAsync(writer, ["posting_id", "created_at", "journal_entry_id", "journal_type", "account_id", "user_id", "system_code", "kind", "asset", "amount", "description"],
                    db.Postings.AsNoTracking()
                        .Where(p => p.CreatedAt >= from && p.CreatedAt < to)
                        .Join(db.JournalEntries.AsNoTracking(), p => p.JournalEntryId, j => j.Id, (p, j) => new { p, j })
                        .OrderBy(x => x.p.Id)
                        .Take(MaxRows)
                        .Select(x => new object?[] { x.p.Id, x.p.CreatedAt, x.p.JournalEntryId, x.j.Type, x.p.AccountId, x.p.UserId, x.p.SystemCode, x.p.Kind, x.p.Asset, x.p.Amount, x.j.Description })
                        .AsAsyncEnumerable(), ct);
                break;
            default:
                throw AppException.Validation($"Unknown report. Use one of: {string.Join(", ", Types)}.");
        }

        await writer.FlushAsync(ct);
    }

    private static async Task WriteAsync(StreamWriter writer, string[] header, IAsyncEnumerable<object?[]> rows, CancellationToken ct)
    {
        await writer.WriteLineAsync(string.Join(',', header));
        await foreach (var row in rows.WithCancellation(ct))
        {
            await writer.WriteLineAsync(string.Join(',', row.Select(Format)));
        }
    }

    internal static string Format(object? value)
    {
        var text = value switch
        {
            null => "",
            DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            decimal m => MoneyMath.ToPlainString(m),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        // Neutralise spreadsheet formula injection and quote when needed.
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0]) && value is string)
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
