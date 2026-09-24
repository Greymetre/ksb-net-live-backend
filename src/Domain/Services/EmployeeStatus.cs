namespace Domain.Services;

/// <summary>
/// Whether an employee is still switched on in the user master, as the reports show it.
///
/// A report that counts what people did keeps an employee who has since been switched off:
/// the visits, orders and invoices of those months happened, and dropping the person would
/// quietly change last month's totals. The row says which they are in its Employee Status
/// column - "Y" active, "N" inactive - and the Employee Status filter narrows to one or the
/// other, showing both when nothing is picked. Deleted users are not reports' business and
/// never appear. Counts and tiles ("team size", "total ASRs") are unaffected: they answer how
/// many people there are now, so they stay on the active ones.
/// </summary>
public static class EmployeeStatus
{
    public const string Active = "Y";
    public const string Inactive = "N";

    /// <summary>What a stored users.active reads as: anything that is not "N" counts as active,
    /// the way the rest of the system treats that column.</summary>
    public static string Of(string? active) =>
        string.Equals(active?.Trim(), Inactive, StringComparison.OrdinalIgnoreCase) ? Inactive : Active;

    /// <summary>The filter a screen sends: "Y", "N", or null for both. Anything else is read as
    /// no filter, so an unexpected value shows everything rather than nothing.</summary>
    public static string? Read(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Equals("y", StringComparison.OrdinalIgnoreCase) || text.Equals("active", StringComparison.OrdinalIgnoreCase)) return Active;
        if (text.Equals("n", StringComparison.OrdinalIgnoreCase) || text.Equals("inactive", StringComparison.OrdinalIgnoreCase)) return Inactive;
        return null;
    }

    /// <summary>Whether an employee belongs in a report asked for with this filter.</summary>
    public static bool Matches(string? filter, string? active) => filter is null || Of(active) == filter;
}
