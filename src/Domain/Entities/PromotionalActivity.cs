namespace Domain.Entities;

public sealed class PromotionalActivity
{
    public long Id { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string ActivityType { get; set; } = "";
    public string ActivityName { get; set; } = "";
    public DateTime ActivityDate { get; set; }
    public long UserId { get; set; }
    public long CreatedById { get; set; }
    public long? BranchId { get; set; }
    public string? Zone { get; set; }
    public long? ReportingManagerId { get; set; }
    public long? DistributorId { get; set; }
    public string? DistributorName { get; set; }
    public string? DealerName { get; set; }
    public string? HotelName { get; set; }
    public decimal? LocationLat { get; set; }
    public decimal? LocationLng { get; set; }
    public string? LocationText { get; set; }
    public int GiftCount { get; set; }
    public decimal TotalExpense { get; set; }
    public decimal DealerShareAmount { get; set; }
    public string? Feedback { get; set; }
    public string Status { get; set; } = "draft";
    public ICollection<PromotionalActivityParticipant> Participants { get; set; } = new List<PromotionalActivityParticipant>();
    public ICollection<PromotionalActivityExpense> Expenses { get; set; } = new List<PromotionalActivityExpense>();
    public ICollection<PromotionalActivityPhoto> Photos { get; set; } = new List<PromotionalActivityPhoto>();
}

public sealed class PromotionalActivityParticipant
{
    public long Id { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public long ActivityId { get; set; }
    public string? Name { get; set; }
    public string? ShopName { get; set; }
    public string? ProprietorName { get; set; }
    public string? Profession { get; set; }
    public string? Mobile { get; set; }
    public string? GiftName { get; set; }
    public string? Remarks { get; set; }
    public bool IsInfluencer { get; set; }
    public string? SocialType { get; set; }
    public string? SocialLink { get; set; }
}

public sealed class PromotionalActivityExpense
{
    public long Id { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public long ActivityId { get; set; }
    public string ExpenseType { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public decimal DealerShareAmount { get; set; }
    public decimal DealerSharePct { get; set; }
    public string? Remarks { get; set; }
    public string? InvoiceUrl { get; set; }
}

public sealed class PromotionalActivityPhoto
{
    public long Id { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public long ActivityId { get; set; }
    public string PhotoUrl { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public DateTime? TakenAt { get; set; }
}
