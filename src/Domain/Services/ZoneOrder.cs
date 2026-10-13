namespace Domain.Services;

/// <summary>
/// The order zones are read in across the business: North, East, West, South - NEWS, the
/// way a compass is read - and Head Office last, because it is not a sales territory.
///
/// Sorting on the name gives East, HO, North, South, West; sorting on the id gives North,
/// South, East, West. Neither is the order anyone here expects, so every screen, report and
/// export that shows zones ranks them through this type rather than deciding for itself.
///
/// A zone belongs to a direction by its FIRST LETTER - N, E, W, S - so a big direction split
/// into sub-zones still lands in the right place however it is written: "Nz 1" / "Nz 2",
/// "North 1" / "North 2", "East 1"... all count. Within a direction the trailing number
/// orders them (1 before 2 before 3); a sub-zone with no number sits after the numbered ones.
/// Head Office is always last, and any zone whose first letter is none of N/E/W/S sits after
/// the four directions and before HO, so a zone added later still appears instead of vanishing.
///
/// Rank returns a single composite key - direction * 1000 + the trailing number - so a plain
/// OrderBy(ZoneOrder.Rank) sorts correctly without each caller re-deriving the sub-order.
/// </summary>
public static class ZoneOrder
{
    private const int Unknown = 50;
    private const int HeadOffice = 99;

    // A sub-zone with no trailing number sorts after the numbered ones within its direction.
    // Real zone numbers are small, so 999 sits safely past them without overflowing into the
    // next direction's band (each direction owns a 1000-wide band).
    private const int NoNumber = 999;
    private const int Band = 1000;

    public static int Rank(string? zoneName)
    {
        var zone = zoneName?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(zone)) return HeadOffice * Band + NoNumber;

        // Head Office is checked by name before the first-letter rule, because "ho" /
        // "head office" start with H and must land last, not in the unknown band.
        if (zone is "ho" or "h.o" or "h.o." or "head office" or "headoffice")
            return HeadOffice * Band + NoNumber;

        var direction = zone[0] switch
        {
            'n' => 1, // North (incl. "Nz", "North", "norrth")
            'e' => 2, // East
            'w' => 3, // West
            's' => 4, // South
            _ => Unknown
        };

        return direction * Band + TrailingNumber(zone);
    }

    /// <summary>
    /// Whether a zone is a sales territory that belongs in a zone-wise report: its first
    /// letter is N, E, W or S (case-insensitive). Head Office and anything that starts with
    /// another letter are not - the reports drop them, so only the four directions (and their
    /// numbered sub-zones like "Nz 1") appear. Master-data and the user-assignment dropdowns
    /// do NOT use this - they still list every zone, so HO stays assignable.
    /// </summary>
    public static bool IsSalesZone(string? zoneName)
    {
        var zone = zoneName?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(zone)) return false;
        if (zone is "ho" or "h.o" or "h.o." or "head office" or "headoffice") return false;
        return zone[0] is 'n' or 'e' or 'w' or 's';
    }

    /// <summary>The number at the end of a zone name ("Nz 1" -> 1, "North 2" -> 2), or
    /// NoNumber when the name ends in no digits, so an unnumbered zone sorts after the
    /// numbered ones in its direction.</summary>
    private static int TrailingNumber(string zone)
    {
        var start = zone.Length;
        while (start > 0 && char.IsDigit(zone[start - 1])) start--;
        if (start == zone.Length) return NoNumber;
        return int.TryParse(zone[start..], out var number) && number < NoNumber ? number : NoNumber - 1;
    }

    /// <summary>Orders a sequence of zone names NEWS - direction by first letter, then the
    /// trailing number - keeping anything unrecognised after the four and before HO.</summary>
    public static IOrderedEnumerable<T> ByZone<T>(this IEnumerable<T> source, Func<T, string?> zoneName) =>
        source.OrderBy(item => Rank(zoneName(item))).ThenBy(item => zoneName(item) ?? string.Empty);
}
