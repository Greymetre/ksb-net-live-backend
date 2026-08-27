namespace Application.DTOs.NewInvoices;

/// <summary>One scheme card in the field app.</summary>
public sealed class FieldSchemeDto
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Tag { get; set; }
    /// <summary>Regular or Booster, which is the wallet the points land in.</summary>
    public string WalletType { get; set; } = "Regular";
    public string? BasedOn { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    /// <summary>live, upcoming or expired.</summary>
    public string Status { get; set; } = "live";
    public string StatusLabel { get; set; } = "Live";
    public bool IsLive { get; set; }
    public int DaysRemaining { get; set; }
    /// <summary>How the scheme was targeted - All, Branch, Zone, State or Customer - and the
    /// values it was targeted at, so the card can say why it is being shown.</summary>
    public string? AreaScope { get; set; }
    public IReadOnlyList<string> AreaValues { get; set; } = [];
    public string? CustomerType { get; set; }
    public int SlabCount { get; set; }
}

public sealed class FieldSchemeSlabDto
{
    public decimal FromAmount { get; set; }
    public decimal ToAmount { get; set; }
    public decimal Value { get; set; }
    public string? ValueType { get; set; }
}

/// <summary>A scheme opened from the card, with what this user's retailers have done under it.</summary>
public sealed class FieldSchemeDetailDto
{
    public FieldSchemeDto Scheme { get; set; } = new();
    public IReadOnlyList<FieldSchemeSlabDto> Slabs { get; set; } = [];
    public int InvoiceCount { get; set; }
    public int RetailerCount { get; set; }
    public decimal ApprovedAmount { get; set; }
    public decimal PendingAmount { get; set; }
    public decimal PointsEarned { get; set; }
    public decimal PointsExpected { get; set; }
}
