namespace Crypton.Core.Domain;

public enum TradeKind
{
    Buy,
    Sell,
    Swap,
}

/// <summary>Which side of the trade the user fixed when requesting the quote.</summary>
public enum AmountSide
{
    From,
    To,
}

public class Quote
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public TradeKind Kind { get; set; }

    public string FromAsset { get; set; } = "";

    public string ToAsset { get; set; } = "";

    /// <summary>Total the user gives up, fee included when the fee is on the from side.</summary>
    public decimal FromAmount { get; set; }

    /// <summary>Net amount the user receives.</summary>
    public decimal ToAmount { get; set; }

    public decimal Fee { get; set; }

    public string FeeAsset { get; set; } = "";

    /// <summary>Execution price: units of the quote currency per unit of the traded asset (NGN per BTC, or To per From for swaps).</summary>
    public decimal Rate { get; set; }

    /// <summary>Gross amount moved against the treasury on the "to" side (before fee) or on the "from" side.</summary>
    public decimal TreasuryFromAmount { get; set; }

    public decimal TreasuryToAmount { get; set; }

    public AmountSide AmountSide { get; set; }

    /// <summary>NGN value of the trade at quote time, used for limits and analytics.</summary>
    public decimal NgnValue { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}

public class TradeOrder
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid QuoteId { get; set; }

    public string? ClientOrderId { get; set; }

    public TradeKind Kind { get; set; }

    public string FromAsset { get; set; } = "";

    public string ToAsset { get; set; } = "";

    public decimal FromAmount { get; set; }

    public decimal ToAmount { get; set; }

    public decimal Fee { get; set; }

    public string FeeAsset { get; set; } = "";

    public decimal Rate { get; set; }

    public decimal NgnValue { get; set; }

    public Guid JournalEntryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
