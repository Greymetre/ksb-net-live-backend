namespace Domain.Entities;

public sealed class LoyaltySchemeSlab : BaseEntity
{
    public ulong LoyaltySchemeId { get; set; }

    /// <summary>Which group's ladder this band belongs to. A Quantity Slab scheme holds several
    /// groups, each with its own product set and its own ladder; this band is one rung of group
    /// <see cref="GroupNo"/>'s ladder. Invoice schemes (and every row saved before groups existed)
    /// use the single implicit group 1.</summary>
    public int GroupNo { get; set; } = 1;

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
