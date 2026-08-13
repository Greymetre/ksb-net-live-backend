namespace Domain.Entities;

public sealed class Sale : BaseEntity
{
    public string Active { get; set; } = "Y";
    public ulong? BuyerId { get; set; }
    public ulong? SellerId { get; set; }
    public ulong? OrderId { get; set; }
    public long TotalQty { get; set; }
    public long ShippedQty { get; set; }
    public string OrderNo { get; set; } = string.Empty;
    public string FiscalYear { get; set; } = string.Empty;
    public string SalesNo { get; set; } = string.Empty;
    public string InvoiceNo { get; set; } = string.Empty;
    public DateTime? InvoiceDate { get; set; }
    public string? TransportDetails { get; set; }
    public decimal TotalGst { get; set; }
    public decimal? TotalDiscount { get; set; }
    public decimal? ExtraDiscount { get; set; }
    public decimal? ExtraDiscountAmount { get; set; }
    public decimal SubTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public string Description { get; set; } = string.Empty;
    public ulong? StatusId { get; set; }
    public ulong? CreatedBy { get; set; }
    public ulong? UpdatedBy { get; set; }
    public string? TransportName { get; set; }
    public string? LrNo { get; set; }
    public DateTime? DispatchDate { get; set; }
    public string? InvoiceAttachment { get; set; }
    public ulong? LoyaltySchemeId { get; set; }
}

public sealed class SaleDetail : BaseEntity
{
    public string? Active { get; set; }
    public ulong? SalesId { get; set; }
    public ulong? ProductId { get; set; }
    public ulong? ProductDetailId { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? ShippedQty { get; set; }
    public decimal? Price { get; set; }
    public decimal? Discount { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? LineTotal { get; set; }
    public ulong? StatusId { get; set; }
}
