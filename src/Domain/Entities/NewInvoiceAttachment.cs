namespace Domain.Entities;

/// <summary>One file on an invoice. An invoice used to carry a single path in its own
/// attachment column; that column still holds the first file so older readers keep
/// working, and every file - photograph or PDF - is listed here.</summary>
public sealed class NewInvoiceAttachment
{
    public long Id { get; set; }
    public ulong InvoiceId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? MimeType { get; set; }
    public long? FileSize { get; set; }
    public int SortOrder { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
