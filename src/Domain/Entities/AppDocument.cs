namespace Domain.Entities;

/// <summary>A document kept under Setting Management > App Document Settings: a name and one
/// PDF. Deleting one only stamps deleted_at, like every other module.</summary>
public sealed class AppDocument
{
    public long Id { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    /// <summary>The name the file had when it was uploaded, shown in the listing.</summary>
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    /// <summary>Meant for the SFA (FieldKonnect) app. Either, both or neither may be ticked.</summary>
    public bool ShowInSfa { get; set; }
    /// <summary>Meant for the VRiDDHi (Loyalty) app.</summary>
    public bool ShowInVriddhi { get; set; }
    public ulong? CreatedBy { get; set; }
    public ulong? UpdatedBy { get; set; }
    public ulong? DeletedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
