namespace Application.DTOs.NewInvoices;

/// <summary>One product line the caller submits on a Quantity Slab invoice.</summary>
public sealed class InvoiceItemInput
{
    public ulong ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}

/// <summary>A product the dropdown offers for a scheme (resolved from its segment/family/product
/// lines), for the Quantity Slab invoice table.</summary>
public sealed class SchemeProductOptionDto
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
}

/// <summary>Ask what a set of quantities would earn on a scheme, for the live preview on the form.</summary>
public sealed class QuantityPointsPreviewRequest
{
    public ulong SchemeId { get; set; }
    public ulong CustomerId { get; set; }
    public DateTime? InvoiceDate { get; set; }
    /// <summary>When editing, the invoice being edited - so its own quantities are not counted
    /// twice in the period cumulative.</summary>
    public ulong? ExceptInvoiceId { get; set; }
    public List<InvoiceItemLineInput> Lines { get; set; } = [];
}

public sealed class InvoiceItemLineInput
{
    public ulong ProductId { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class QuantityPointsLineDto
{
    public ulong ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal CumulativeQuantity { get; set; }
    public decimal? Rate { get; set; }
    public decimal Points { get; set; }
    public string? TierName { get; set; }
    public string? Hint { get; set; }
}

public sealed class QuantityPointsPreviewDto
{
    public decimal TotalPoints { get; set; }
    public List<QuantityPointsLineDto> Lines { get; set; } = [];
}

/// <summary>A stored product line of an invoice, for reading it back on the form.</summary>
public sealed class InvoiceItemDto
{
    public ulong ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}
