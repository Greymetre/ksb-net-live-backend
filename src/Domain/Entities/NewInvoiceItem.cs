namespace Domain.Entities;

/// <summary>
/// One product line of a Quantity Slab invoice: the product, how many were bought and the
/// price the user typed. The quantity is what the scheme is read on; the price is recorded
/// only so it can be cross-checked against the uploaded invoice and earns nothing.
///
/// Items belong to their invoice. The parent new_invoices row has no soft delete, so on an
/// edit the items are replaced (deleted and re-inserted) rather than flagged.
/// </summary>
public sealed class NewInvoiceItem : BaseEntity
{
    public ulong NewInvoiceId { get; set; }
    public ulong ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public int SortOrder { get; set; }
}
