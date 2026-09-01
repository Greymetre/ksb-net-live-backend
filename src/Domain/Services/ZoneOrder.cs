namespace Domain.Services;

/// <summary>
/// The order zones are read in across the business: North, East, West, South - NEWS, the
/// way a compass is read - and Head Office last, because it is not a sales territory.
///
/// Sorting on the name gives East, HO, North, South, West; sorting on the id gives North,
/// South, East, West. Neither is the order anyone here expects, so every screen, report and
/// export that shows zones ranks them through this type rather than deciding for itself.
/// A zone that is not one of the four sits after them and before HO, so a zone added later
/// still appears instead of disappearing.
/// </summary>
public static class ZoneOrder
{
    private const int Unknown = 50;
    private const int HeadOffice = 99;

    public static int Rank(string? zoneName)
    {
        var zone = zoneName?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(zone)) return HeadOffice;

        return zone switch
        {
            // "norrth" is a spelling that exists in live data.
            "north" or "norrth" => 1,
            "east" => 2,
            "west" => 3,
            "south" => 4,
            "ho" or "h.o" or "h.o." or "head office" or "headoffice" => HeadOffice,
            _ => Unknown
        };
    }

    /// <summary>Orders a sequence of zone names NEWS, keeping anything unrecognised in
    /// alphabetical order after the four and before HO.</summary>
    public static IOrderedEnumerable<T> ByZone<T>(this IEnumerable<T> source, Func<T, string?> zoneName) =>
        source.OrderBy(item => Rank(zoneName(item))).ThenBy(item => zoneName(item) ?? string.Empty);
}
