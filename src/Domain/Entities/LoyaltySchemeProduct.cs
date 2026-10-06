namespace Domain.Entities;

/// <summary>
/// One line of a Product or Quantity scheme: which goods it covers, and what they earn.
///
/// A line names segments, families and products together. The narrowest choice wins -
/// products if any are named, otherwise every product of the named families, otherwise
/// every product of the named segments. That is how a scheme covering a whole family of a
/// hundred pumps is written in one line instead of a hundred.
///
/// Ids are stored comma separated, the way a beat stores its cities, because a line is
/// edited and imported as a whole and is never searched by one id.
/// </summary>
public sealed class LoyaltySchemeProduct : BaseEntity
{
    public ulong LoyaltySchemeId { get; set; }
    /// <summary>categories.id - "Segment" on every screen.</summary>
    public string SegmentIds { get; set; } = string.Empty;
    /// <summary>subcategories.id - "Family" on every screen.</summary>
    public string FamilyIds { get; set; } = string.Empty;
    public string ProductIds { get; set; } = string.Empty;
    public decimal RewardValue { get; set; }
    /// <summary>Only a mixed scheme reads this; blank means a flat amount, as on a slab.</summary>
    public string? RewardType { get; set; }
    public int SortOrder { get; set; }
    public LoyaltyScheme? LoyaltyScheme { get; set; }
}
