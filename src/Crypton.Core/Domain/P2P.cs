namespace Crypton.Core.Domain;

/// <summary>Side from the maker's point of view.</summary>
public enum P2PAdSide
{
    /// <summary>Maker sells crypto for fiat.</summary>
    Sell,

    /// <summary>Maker buys crypto with fiat.</summary>
    Buy,
}

public enum P2PPriceType
{
    Fixed,

    /// <summary>Market price adjusted by a margin in basis points.</summary>
    Floating,
}

public enum P2PAdStatus
{
    Active,
    Paused,
    Closed,
}

public enum P2POrderStatus
{
    PendingPayment,
    Paid,
    Completed,
    Cancelled,
    Expired,
    Disputed,
    ResolvedToBuyer,
    ResolvedToSeller,
}

public class P2PAd
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public P2PAdSide Side { get; set; }

    public string Asset { get; set; } = "";

    public string FiatCurrency { get; set; } = AssetCodes.NGN;

    public P2PPriceType PriceType { get; set; }

    public decimal? FixedPrice { get; set; }

    public int FloatingMarginBps { get; set; }

    public decimal TotalQuantity { get; set; }

    /// <summary>Quantity still available to new orders.</summary>
    public decimal RemainingQuantity { get; set; }

    /// <summary>Sell ads only: crypto still held in the maker's P2P reserve (remaining quantity + fee reserve).</summary>
    public decimal ReservedAmount { get; set; }

    public decimal MinOrderFiat { get; set; }

    public decimal MaxOrderFiat { get; set; }

    public int PaymentWindowMinutes { get; set; }

    /// <summary>Sell ads: maker bank accounts the buyer can pay into.</summary>
    public Guid[] PaymentMethodIds { get; set; } = [];

    public string? Terms { get; set; }

    public P2PAdStatus Status { get; set; }

    public bool SuspendedByAdmin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class P2POrder
{
    public Guid Id { get; set; }

    /// <summary>Human-friendly order number shown in the UI.</summary>
    public string OrderNumber { get; set; } = "";

    public Guid AdId { get; set; }

    public P2PAdSide AdSide { get; set; }

    public Guid MakerId { get; set; }

    public Guid TakerId { get; set; }

    public Guid BuyerId { get; set; }

    public Guid SellerId { get; set; }

    public string Asset { get; set; } = "";

    public string FiatCurrency { get; set; } = AssetCodes.NGN;

    public decimal Quantity { get; set; }

    public decimal Price { get; set; }

    public decimal FiatAmount { get; set; }

    /// <summary>Crypto fee paid by the maker.</summary>
    public decimal Fee { get; set; }

    /// <summary>Total crypto locked in escrow for this order.</summary>
    public decimal EscrowAmount { get; set; }

    public P2POrderStatus Status { get; set; }

    public DateTimeOffset PaymentDeadline { get; set; }

    /// <summary>JSON snapshot of the seller's payment details shown to the buyer.</summary>
    public string PaymentDetails { get; set; } = "[]";

    public string? BuyerPaymentReference { get; set; }

    public string? CancelReason { get; set; }

    public Guid? CancelledBy { get; set; }

    public decimal NgnValue { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
}

public enum P2PDisputeStatus
{
    Open,
    ResolvedToBuyer,
    ResolvedToSeller,
}

public class P2PDispute
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid OpenedBy { get; set; }

    public string Reason { get; set; } = "";

    public P2PDisputeStatus Status { get; set; }

    public Guid? ResolvedBy { get; set; }

    public string? ResolutionNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public List<P2PDisputeEvidence> Evidence { get; set; } = [];
}

public class P2PDisputeEvidence
{
    public Guid Id { get; set; }

    public Guid DisputeId { get; set; }

    public Guid UserId { get; set; }

    public string? Text { get; set; }

    public string? StorageKey { get; set; }

    public string? FileName { get; set; }

    public string? ContentType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public class P2PFeedback
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public Guid FromUserId { get; set; }

    public Guid ToUserId { get; set; }

    public bool Positive { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
