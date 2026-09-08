namespace Domain.Entities;

public sealed class LoyaltySchemeSlab : BaseEntity
{
    public ulong LoyaltySchemeId { get; set; }
    public string TierName { get; set; } = string.Empty;
    public decimal ValueFrom { get; set; }
    public decimal? ValueTo { get; set; }
    public decimal RewardValue { get; set; }

    /// <summary>Only a mixed scheme reads this. Blank means a flat amount, so an
    /// existing slab keeps behaving exactly as it did.</summary>
    public string? RewardType { get; set; }
    public int SortOrder { get; set; }
    public LoyaltyScheme? LoyaltyScheme { get; set; }
}
