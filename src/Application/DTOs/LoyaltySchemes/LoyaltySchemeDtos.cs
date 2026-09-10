namespace Application.DTOs.LoyaltySchemes;

public sealed class LoyaltySchemeDto
{
    public ulong Id { get; set; }
    public string Active { get; set; } = "Y";
    public string SchemeName { get; set; } = string.Empty;
    public string SchemeCode { get; set; } = string.Empty;
    public string? SchemeDescription { get; set; }
    public string? SchemeNote { get; set; }
    public string SchemeTag { get; set; } = "Regular";
    public string CustomerType { get; set; } = string.Empty;
    public string AreaScope { get; set; } = "All";
    public string[] AreaValues { get; set; } = [];
    public string AreaDisplay { get; set; } = "All India";
    /// <summary>Customer ids of the dealers excluded from this scheme.</summary>
    public ulong[] ExcludedDealerIds { get; set; } = [];
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string SchemeType { get; set; } = "Invoice";
    public string BasedOn { get; set; } = "Value";
    public bool RedemptionEnabled { get; set; }
    public string Status { get; set; } = "Draft";
    public string WorkflowStatus { get; set; } = "Draft";
    public string? BrochurePath { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public ulong? SubmittedBy { get; set; }
    public string? SubmittedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public ulong? ApprovedBy { get; set; }
    public string? ApprovedByName { get; set; }
    public string? ApprovalRemark { get; set; }
    public DateTime? RejectedAt { get; set; }
    public ulong? RejectedBy { get; set; }
    public string? RejectedByName { get; set; }
    public string? RejectionRemark { get; set; }
    /// <summary>Set when the scheme was published. Blank on anything published before
    /// the column existed - that moment was never recorded and is not guessed at here.</summary>
    public DateTime? PublishedAt { get; set; }
    public ulong? PublishedBy { get; set; }
    public string? PublishedByName { get; set; }
    public ulong? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime? CreatedAt { get; set; }
    public List<LoyaltySchemeSlabDto> Slabs { get; set; } = [];
}

/// <summary>
/// One dealer for the scheme form's dealer picker. Carries the area it belongs to so the
/// screen can narrow the list to the chosen zone, branch or state without asking the
/// server again, and the code, mobile and email so the box can be searched by any of them.
/// </summary>
public sealed class SchemeDealerOptionDto
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    /// <summary>Branch and zone come from the dealer's assigned employee, which is the same
    /// rule the scheme audience uses - anything else would offer dealers the scheme would
    /// not actually reach.</summary>
    public string? Branch { get; set; }
    public string? Zone { get; set; }
    public string? State { get; set; }
}

public sealed class LoyaltySchemeSlabDto
{
    public ulong Id { get; set; }
    public string TierName { get; set; } = string.Empty;
    public decimal ValueFrom { get; set; }
    public decimal? ValueTo { get; set; }
    public decimal RewardValue { get; set; }
    /// <summary>Only meaningful on a mixed scheme; blank elsewhere.</summary>
    public string? RewardType { get; set; }
    public int SortOrder { get; set; }
}

public sealed class LoyaltySchemeRequestDto
{
    public string? Active { get; set; }
    public string? SchemeName { get; set; }
    public string? SchemeCode { get; set; }
    public string? SchemeDescription { get; set; }
    public string? SchemeNote { get; set; }
    public string? SchemeTag { get; set; }
    public string? CustomerType { get; set; }
    public string? AreaScope { get; set; }
    public string[]? AreaValues { get; set; }
    public ulong[]? ExcludedDealerIds { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? SchemeType { get; set; }
    public string? BasedOn { get; set; }
    public bool RedemptionEnabled { get; set; }
    public string? Status { get; set; }
    public List<LoyaltySchemeSlabRequestDto> Slabs { get; set; } = [];
}

public sealed class LoyaltySchemeSlabRequestDto
{
    public string? TierName { get; set; }
    public decimal? ValueFrom { get; set; }
    public decimal? ValueTo { get; set; }
    public decimal? RewardValue { get; set; }
    /// <summary>Read only when the scheme itself is mixed.</summary>
    public string? RewardType { get; set; }
}

public sealed class LoyaltySchemeFilterDto
{
    public string? Search { get; set; }
    public string? Status { get; set; }
    /// <summary>Who is asking. A dealer login holds scheme.view, so it reaches this listing
    /// too - and must not be shown a scheme that names it in Excluded dealers.</summary>
    public ulong? ActorUserId { get; set; }
}

public sealed class LoyaltySchemeDecisionDto
{
    public string? Remark { get; set; }
}

public sealed class LoyaltySchemeOptionsDto
{
    public List<LoyaltySchemeOptionDto> Branches { get; set; } = [];
    public List<LoyaltySchemeOptionDto> Zones { get; set; } = [];
    public List<LoyaltySchemeOptionDto> States { get; set; } = [];
    public List<LoyaltySchemeOptionDto> Customers { get; set; } = [];
}

public sealed class LoyaltySchemeOptionDto
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
