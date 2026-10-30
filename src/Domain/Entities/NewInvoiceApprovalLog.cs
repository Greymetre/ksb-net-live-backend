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
    /// this holds what was approved at this step so each stage's figure can be shown separately.</summary>
    public decimal? ApprovedQuantity { get; set; }
    public string? Remark { get; set; }
}
