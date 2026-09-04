using Application.Common;

namespace Application.DTOs.Customers;

/// <summary>What the KYC screen asks for. It is the customer list narrowed to the
/// people whose paperwork is being chased, so it takes the same filters and adds
/// one of its own: where the customer stands in the KYC review.</summary>
public sealed class CustomerKycFilterDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    public bool Unpaged { get; set; }
    public ulong? CustomerType { get; set; }
    public string? Search { get; set; }
    public string? Active { get; set; }
    /// <summary>One of the stages the tiles count - approved, complete_pending, partial,
    /// none - or rejected, which cuts across them. Null lists everyone.</summary>
    public string? KycStatus { get; set; }
    /// <summary>Limits the list to the retailers assigned to this dealer. The tiles are
    /// counted over the same narrowed set, so they always describe what is on screen.</summary>
    public ulong? DealerCustomerId { get; set; }
    public ulong? ActorUserId { get; set; }
}

/// <summary>One KYC document for one customer. The screen answers three questions per
/// document - has the file been uploaded, have the matching details been filled in, and
/// has a reviewer signed it off - so each is carried separately rather than collapsed
/// into a single status.</summary>
public sealed class CustomerKycDocumentStateDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Uploaded { get; set; }
    /// <summary>Where the uploaded file lives, as stored. The screen resolves it to a URL.</summary>
    public string? AttachmentPath { get; set; }
    public bool DetailsFilled { get; set; }
    /// <summary>The detail itself - the GST number, the account number - so the screen can
    /// show what was entered without a second call.</summary>
    public string? DetailSummary { get; set; }
    /// <summary>Every field this document is meant to carry, filled or not, for the popup.</summary>
    public IReadOnlyList<CustomerKycDetailDto> Details { get; set; } = [];
    /// <summary>approved, rejected, or pending.</summary>
    public string Status { get; set; } = "pending";
    public string? Remark { get; set; }
    public string? ActionByName { get; set; }
    public DateTime? ActionAt { get; set; }
}

public sealed class CustomerKycDetailDto
{
    public string Label { get; set; } = string.Empty;
    public string? Value { get; set; }
    /// <summary>Which stored field an edit to this row should write to.</summary>
    public string Field { get; set; } = string.Empty;
}

public sealed class CustomerKycListItemDto
{
    public ulong Id { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public string FirmName { get; set; } = string.Empty;
    public string? Mobile { get; set; }
    public string? CustomerCode { get; set; }
    /// <summary>The dealer this customer is mapped to, named rather than numbered - the
    /// listing shows it beside the customer.</summary>
    public string? DealerName { get; set; }
    public string CustomerTypeName { get; set; } = string.Empty;
    public string Active { get; set; } = "Y";
    public IReadOnlyList<CustomerKycDocumentStateDto> Documents { get; set; } = [];
    public int DocumentCount { get; set; }
    public int UploadedCount { get; set; }
    public int DetailsCount { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }
    /// <summary>Which tile this customer is counted under: approved, complete_pending,
    /// partial or none.</summary>
    public string Stage { get; set; } = "none";
    /// <summary>approved when every document is signed off, rejected when any one was
    /// turned down, pending for everything in between.</summary>
    public string OverallStatus { get; set; } = "pending";
    public DateTime? LastActionAt { get; set; }
}

/// <summary>The tiles. They count the filtered set, so narrowing to one dealer narrows
/// the numbers with it.</summary>
public sealed class CustomerKycSummaryDto
{
    public int TotalCustomers { get; set; }
    /// <summary>Everything submitted and every document signed off.</summary>
    public int Approved { get; set; }
    /// <summary>Everything submitted, still waiting on a reviewer.</summary>
    public int CompletePending { get; set; }
    /// <summary>Something submitted, but not the whole set.</summary>
    public int Partial { get; set; }
    /// <summary>Nothing submitted at all.</summary>
    public int NotStarted { get; set; }
    /// <summary>Cuts across the others: at least one document was turned down.</summary>
    public int Rejected { get; set; }
}

public sealed record CustomerKycListResultDto(
    PagedResult<CustomerKycListItemDto> Page,
    CustomerKycSummaryDto Summary);

public sealed class CustomerKycDealerOptionDto
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
