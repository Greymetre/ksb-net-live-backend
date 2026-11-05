namespace Domain.Entities;

public sealed class NewInvoiceApprovalLog : BaseEntity
{
    public DateTime? LogDate { get; set; }
    public ulong? NewInvoiceId { get; set; }
    public ulong? CreatedBy { get; set; }
    public string? StatusType { get; set; }
    public int? FromStatus { get; set; }
    public int? ToStatus { get; set; }
    public decimal? ApprovedAmount { get; set; }
    /// <summary>For a Quantity Slab invoice the reviewer approves a total quantity, not an amount;
    /// this holds what was approved at this step so each stage's figure can be shown separately.
    /// For a GROUP scheme it carries the sum across groups; the per-group split is in
    /// <see cref="ApprovedQuantities"/>.</summary>
    public decimal? ApprovedQuantity { get; set; }
    /// <summary>For a GROUP Quantity Slab invoice, the per-group approved quantity as JSON
    /// { "&lt;group_no&gt;": &lt;qty&gt;, ... }. Each group runs its own ladder, so one total cannot
    /// carry the approval. Blank for amount schemes and single-ladder quantity schemes.</summary>
    public string? ApprovedQuantities { get; set; }
    public string? Remark { get; set; }
}
