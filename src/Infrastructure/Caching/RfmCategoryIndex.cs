using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Caching;

/// <summary>
/// Every retailer's RFM category, held in memory.
///
/// The category is a position, not a stored value: a retailer is rated against the other
/// retailers of its own state, so working one out means scoring the whole state. Doing that
/// per request - on a listing the field app opens all day - is not affordable, so the whole
/// map is built once and kept.
///
/// How a retailer gets its category:
///   - It has never ordered: Bronze. Most of the book sits here, and that is the point -
///     these are the retailers nobody has opened yet.
///   - It has ordered: inside its own state, the retailers that have ordered are lined up
///     three times - least days since the last order, most orders, highest value - and each
///     line is cut into five equal groups of 20%. The leading fifth scores 5, the last 1.
///     The three are weighed R 20%, F 30%, M 50% into a total out of 5, and the percentage
///     of that total gives the category: 80+ Platinum, 60+ Diamond, 40+ Gold, 20+ Silver.
///
/// The same ladder and the same weights as the CRM's RFM reports, so a retailer cannot read
/// Platinum on the phone and Gold on the dashboard - the only difference is who it is being
/// compared against, which is its state here and the whole filter there.
/// </summary>
public sealed class RfmCategoryIndex
{
    /// <summary>How long a build stays usable. Orders arrive all day and a category only
    /// moves when a retailer crosses a 20% line, so a short window is wasted work.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public const string Platinum = "Platinum";
    public const string Diamond = "Diamond";
    public const string Gold = "Gold";
    public const string Silver = "Silver";
    public const string Bronze = "Bronze";

    /// <summary>The categories in order, best first - for a filter dropdown.</summary>
    public static readonly IReadOnlyList<string> All = [Platinum, Diamond, Gold, Silver, Bronze];

    private const decimal RecencyWeight = 0.20m, FrequencyWeight = 0.30m, MonetaryWeight = 0.50m;
    private const decimal MaxRating = 5m;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private volatile Snapshot? _snapshot;

    public RfmCategoryIndex(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public sealed record Snapshot(IReadOnlyDictionary<ulong, string> Categories, DateTime BuiltAt);

    public async Task<Snapshot> GetAsync(CancellationToken cancellationToken)
    {
        var current = _snapshot;
        if (current is not null && DateTime.UtcNow - current.BuiltAt < Lifetime) return current;

        await _buildLock.WaitAsync(cancellationToken);
        try
        {
            current = _snapshot;
            if (current is not null && DateTime.UtcNow - current.BuiltAt < Lifetime) return current;

            var built = new Snapshot(await BuildAsync(cancellationToken), DateTime.UtcNow);
            _snapshot = built;
            return built;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>The category to show for one retailer. A retailer the index has never seen -
    /// created since the last build - reads Bronze, which is where it would sit anyway until
    /// it places its first order.</summary>
    public static string Of(IReadOnlyDictionary<ulong, string> categories, ulong customerId) =>
        categories.TryGetValue(customerId, out var category) ? category : Bronze;

    public void Invalidate() => _snapshot = null;

    private async Task<IReadOnlyDictionary<ulong, string>> BuildAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // One pass over the orders, one over the retailers. Both are read in the database.
        var orders = (await db.Orders.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.BuyerId.HasValue && x.OrderDate.HasValue)
            .GroupBy(x => x.BuyerId!.Value)
            .Select(g => new { BuyerId = g.Key, Orders = g.Count(), LastOrder = g.Max(x => x.OrderDate!.Value), Value = g.Sum(x => x.GrandTotal) })
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.BuyerId);

        var retailers = await db.Customers.AsNoTracking()
            .Where(x => x.CustomerType == 2 && x.DeletedAt == null)
            .Select(x => new { x.Id, x.CustomFields })
            .ToListAsync(cancellationToken);

        var categories = new Dictionary<ulong, string>(retailers.Count);
        var today = DateTime.Today;
        var byState = new Dictionary<ulong, List<Scored>>();

        foreach (var retailer in retailers)
        {
            if (!orders.TryGetValue(retailer.Id, out var stats))
            {
                // Never ordered - Bronze, and no state ranking to take part in.
                categories[retailer.Id] = Bronze;
                continue;
            }
            var stateId = StateOf(retailer.CustomFields) ?? 0;
            if (!byState.TryGetValue(stateId, out var list)) byState[stateId] = list = [];
            list.Add(new Scored(retailer.Id,
                Math.Max(0, (int)(today - stats.LastOrder.Date).TotalDays), stats.Orders, stats.Value));
        }

        foreach (var state in byState.Values)
        {
            var recency = Quintiles(state.OrderBy(x => x.RecencyDays).ThenBy(x => x.Id).Select(x => x.Id).ToList());
            var frequency = Quintiles(state.OrderByDescending(x => x.Frequency).ThenBy(x => x.Id).Select(x => x.Id).ToList());
            var monetary = Quintiles(state.OrderByDescending(x => x.Monetary).ThenBy(x => x.Id).Select(x => x.Id).ToList());
            foreach (var row in state)
            {
                var total = recency[row.Id] * RecencyWeight + frequency[row.Id] * FrequencyWeight + monetary[row.Id] * MonetaryWeight;
                var percent = (int)Math.Round(total * 100m / MaxRating, MidpointRounding.AwayFromZero);
                categories[row.Id] = percent >= 80 ? Platinum : percent >= 60 ? Diamond : percent >= 40 ? Gold : Silver;
            }
        }

        return categories;
    }

    private sealed record Scored(ulong Id, int RecencyDays, int Frequency, decimal Monetary);

    /// <summary>1 to 5 over ids already ordered best first: the leading fifth scores 5.</summary>
    private static Dictionary<ulong, int> Quintiles(IReadOnlyList<ulong> ordered)
    {
        var scores = new Dictionary<ulong, int>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++) scores[ordered[i]] = 5 - (int)((long)i * 5 / ordered.Count);
        return scores;
    }

    /// <summary>The retailer's state, read the way the customer master reads it.</summary>
    private static ulong? StateOf(string? customFields)
    {
        if (string.IsNullOrWhiteSpace(customFields)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(customFields);
            foreach (var key in new[] { "state_id", "billing_state" })
                if (document.RootElement.TryGetProperty(key, out var value)
                    && ulong.TryParse(value.ToString(), out var id) && id > 0) return id;
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }
}
