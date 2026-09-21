using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// The Promotional Activity score in the rating reports (CRM and field app) counts activities
/// recorded in the Promotional Activities module - Retailer, Nukkad, Farmer and Influencer meets.
/// Only submitted activities count (a draft is not finished) and never a deleted one; each is
/// placed by its own activity date and counted for the user who ran it.
///
/// It used to be read from the work types picked at punch-in, which only knew three names and
/// counted a meet that was never recorded.
/// </summary>
public static class PromotionalActivityCounts
{
    public sealed record Entry(ulong UserId, DateTime Date);

    public static async Task<IReadOnlyList<Entry>> LoadAsync(AppDbContext db, IReadOnlyCollection<ulong> userIds,
        DateTime from, DateTime toExclusive, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return [];
        var ids = userIds.Select(x => (long)x).ToArray();
        var rows = await db.PromotionalActivities.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.Status == "submitted" && ids.Contains(x.UserId)
                && x.ActivityDate >= from && x.ActivityDate < toExclusive)
            .Select(x => new { x.UserId, x.ActivityDate })
            .ToListAsync(cancellationToken);
        return rows.Select(x => new Entry((ulong)x.UserId, x.ActivityDate.Date)).ToList();
    }

    public static int Count(IEnumerable<Entry> entries, ulong userId, DateTime from, DateTime toExclusive) =>
        entries.Count(x => x.UserId == userId && x.Date >= from && x.Date < toExclusive);
}
