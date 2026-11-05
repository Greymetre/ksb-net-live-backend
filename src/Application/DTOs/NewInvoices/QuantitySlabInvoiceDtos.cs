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

/// <summary>One group's result on a Quantity Slab invoice: the group runs its own ladder on its own
/// products' quantity, so it has its own tier, rate, points and hint, plus the product rows in it.</summary>
public sealed class QuantityPointsGroupDto
{
    public int GroupNo { get; set; }
    /// <summary>This invoice's quantity of the group's products.</summary>
    public decimal Quantity { get; set; }
    /// <summary>That plus the customer's other non-rejected quantity of the group's products in the period.</summary>
    public decimal CumulativeQuantity { get; set; }
    public decimal? Rate { get; set; }
    public string? TierName { get; set; }
    public decimal Points { get; set; }
    public string? Hint { get; set; }
    public List<QuantityPointsLineDto> Lines { get; set; } = [];
}

public sealed class QuantityPointsPreviewDto
{
    public decimal TotalPoints { get; set; }
    /// <summary>The whole invoice's quantity (all covered product lines summed).</summary>
    public decimal TotalQuantity { get; set; }
    /// <summary>That total plus the customer's other non-rejected quantity in the scheme period.</summary>
    public decimal CumulativeQuantity { get; set; }
    /// <summary>Set only when the scheme has a single group, so the old single-ladder display keeps
    /// working; with several groups read <see cref="Groups"/> instead.</summary>
    public decimal? Rate { get; set; }
    public string? TierName { get; set; }
    public string? Hint { get; set; }
    /// <summary>The per-group breakdown - one entry per group that carries a ladder.</summary>
    public List<QuantityPointsGroupDto> Groups { get; set; } = [];
    public List<QuantityPointsLineDto> Lines { get; set; } = [];
}

/// <summary>One band of a group's ladder, for showing a Quantity Slab scheme's detail in the apps.</summary>
public sealed class SchemeGroupSlabDto
{
    public string TierName { get; set; } = string.Empty;
    public decimal ValueFrom { get; set; }
    public decimal? ValueTo { get; set; }
    public decimal RewardValue { get; set; }
}

/// <summary>One group of a Quantity Slab scheme, for the app scheme-detail screen: its own quantity
/// ladder and the goods it covers (each line shown by its product, family or segment names).</summary>
public sealed class SchemeGroupDisplayDto
{
    public int GroupNo { get; set; }
    public List<SchemeGroupSlabDto> Slabs { get; set; } = [];
    public List<string> Products { get; set; } = [];
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
