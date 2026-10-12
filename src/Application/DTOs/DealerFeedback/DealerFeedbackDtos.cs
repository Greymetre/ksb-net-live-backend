namespace Application.DTOs.DealerFeedback;

/// <summary>What a dealer sends when leaving feedback.</summary>
public sealed class SubmitDealerFeedbackRequest
{
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int Rating { get; set; } = 3;
}

/// <summary>One feedback row, as the dealer's own list and the internal listing both read it.</summary>
public sealed class DealerFeedbackRow
{
    public ulong Id { get; set; }
    public ulong CustomerId { get; set; }
    public string DealerName { get; set; } = string.Empty;
    public string? DealerMobile { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int Rating { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>The filters and paging the internal listing is read with. Rows are scoped to
/// <see cref="ActorUserId"/>'s reporting view - admin all, a BM their branch, others their
/// downline - through the dealer's customer id.</summary>
public sealed class DealerFeedbackListFilter
{
    public ulong? ActorUserId { get; set; }
    public string? Category { get; set; }
    public string? Search { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    /// <summary>Export reads every scoped row in one go, so paging is skipped.</summary>
    public bool All { get; set; }
}
