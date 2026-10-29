using Domain.Entities;

namespace Domain.Services;

/// <summary>
/// Points for one product line of a Quantity Slab invoice.
///
/// The per-unit rate is the one of the quantity band the product's CUMULATIVE quantity in the
/// scheme period (everything bought before, plus this invoice) falls into. This invoice earns
/// that rate for the units on it:  points = this invoice's quantity x rate.
///
/// If the cumulative quantity has not yet reached the first band, nothing is earned and a hint
/// says how many more units are needed. When it sits inside a band, the hint points at the next
/// band's better rate, so the field knows what one more unit is worth.
/// </summary>
public static class QuantitySlabReward
{
    public readonly record struct LineResult(decimal Points, decimal? Rate, string? TierName, string? Hint);

    public static LineResult ForLine(decimal cumulativeBefore, decimal thisInvoiceQuantity, IEnumerable<LoyaltySchemeSlab> slabs)
    {
        var bands = slabs
            .Where(x => x.DeletedAt == null)
            .OrderBy(x => x.ValueFrom)
            .ThenBy(x => x.SortOrder)
            .ToList();
        if (bands.Count == 0) return new LineResult(0, null, null, null);

        var cumulativeAfter = cumulativeBefore + thisInvoiceQuantity;

        var achieved = bands.LastOrDefault(band =>
            cumulativeAfter >= band.ValueFrom && (!band.ValueTo.HasValue || cumulativeAfter <= band.ValueTo.Value));

        if (achieved is null)
        {
            // Below the first band - no reward yet.
            var first = bands[0];
            var needed = first.ValueFrom - cumulativeAfter;
            var hint = needed > 0
                ? $"Add {Trim(needed)} more unit(s) to start earning {Trim(first.RewardValue)}/unit."
                : null;
            return new LineResult(0, null, null, hint);
        }

        var points = decimal.Round(thisInvoiceQuantity * achieved.RewardValue, 2);

        // If there is a higher band, say how far away its better rate is.
        string? nextHint = null;
        var next = bands.FirstOrDefault(band => band.ValueFrom > cumulativeAfter);
        if (next is not null)
        {
            var toNext = next.ValueFrom - cumulativeAfter;
            if (toNext > 0) nextHint = $"Add {Trim(toNext)} more unit(s) to reach {Trim(next.RewardValue)}/unit.";
        }

        return new LineResult(points, achieved.RewardValue, achieved.TierName, nextHint);
    }

    private static string Trim(decimal value) => value % 1 == 0 ? ((long)value).ToString() : value.ToString("0.##");
}
