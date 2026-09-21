using System.Runtime.CompilerServices;
using Infrastructure.Caching;

namespace Api.Services;

/// <summary>
/// The four KYC stages the CRM's KYC screen counts - Fully Approved, Awaiting Review, Partly
/// Submitted, Not Started - for the field app and the loyalty app.
///
/// Every number comes from <see cref="CustomerKycIndex"/>, the same in-memory index the CRM
/// screen reads, so a count on a phone and the count in the CRM are one number, not two
/// that happen to agree. Nothing here decides a stage; it only reads the one already built.
/// </summary>
public static class KycStages
{
    public const string Approved = CustomerKycEntry.StageApproved;
    public const string CompletePending = CustomerKycEntry.StageCompletePending;
    public const string Partial = CustomerKycEntry.StagePartial;
    public const string None = CustomerKycEntry.StageNone;

    public static readonly string[] All = [Approved, CompletePending, Partial, None];

    /// <summary>Not a stage: customers with at least one rejected document, whatever their stage -
    /// the CRM KYC screen's "Has a Rejection" filter, by the same rule.</summary>
    public const string Rejected = "rejected";

    // One id-to-stage map per index build, made on first use and dropped with the build.
    private static readonly ConditionalWeakTable<CustomerKycIndex.Snapshot, IReadOnlyDictionary<ulong, string>> Maps = new();

    public static bool IsStage(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>A value the lists accept as their kyc filter: one of the four stages, or Rejected.</summary>
    public static bool IsFilter(string? value) => IsStage(value) || value == Rejected;

    public static string Label(string? stage) => stage switch
    {
        Approved => "Fully Approved",
        CompletePending => "Awaiting Review",
        Partial => "Partly Submitted",
        _ => "Not Started"
    };

    public static async Task<IReadOnlyDictionary<ulong, string>> StageMapAsync(CustomerKycIndex index, CancellationToken cancellationToken)
    {
        var snapshot = await index.GetAsync(cancellationToken);
        return Maps.GetValue(snapshot, built => built.Entries.ToDictionary(entry => entry.Id, entry => entry.Stage));
    }

    public static string StageOf(IReadOnlyDictionary<ulong, string> map, ulong customerId) =>
        map.TryGetValue(customerId, out var stage) ? stage : None;

    public static async Task<ulong[]> IdsInStageAsync(CustomerKycIndex index, string stage, CancellationToken cancellationToken)
    {
        if (stage == Rejected) return await RejectedIdsAsync(index, cancellationToken);
        var map = await StageMapAsync(index, cancellationToken);
        return map.Where(pair => pair.Value == stage).Select(pair => pair.Key).ToArray();
    }

    /// <summary>Counts for a set of customers, in the shape both apps read.</summary>
    public static Dictionary<string, object> Summary(IEnumerable<string> stages)
    {
        var counts = All.ToDictionary(stage => stage, _ => 0);
        var total = 0;
        foreach (var stage in stages)
        {
            total++;
            counts[IsStage(stage) ? stage : None]++;
        }

        return new Dictionary<string, object>
        {
            ["total"] = total,
            ["approved"] = counts[Approved],
            ["complete_pending"] = counts[CompletePending],
            ["partial"] = counts[Partial],
            ["not_started"] = counts[None],
            ["stages"] = All.Select(stage => new { key = stage, label = Label(stage), count = counts[stage] }).ToList()
        };
    }

    /// <summary>The four stage counts plus <c>rejected</c> - how many of these customers have at
    /// least one rejected document. Rejected overlaps the stages, so it is not part of the total.</summary>
    public static async Task<object> SummaryAsync(CustomerKycIndex index, IEnumerable<ulong> customerIds, CancellationToken cancellationToken)
    {
        var map = await StageMapAsync(index, cancellationToken);
        var ids = customerIds.Distinct().ToList();
        var rejectedIds = (await RejectedIdsAsync(index, cancellationToken)).ToHashSet();
        var summary = Summary(ids.Select(id => StageOf(map, id)));
        summary["rejected"] = ids.Count(rejectedIds.Contains);
        return summary;
    }

    private static async Task<ulong[]> RejectedIdsAsync(CustomerKycIndex index, CancellationToken cancellationToken)
    {
        var snapshot = await index.GetAsync(cancellationToken);
        return snapshot.Entries.Where(entry => entry.RejectedCount > 0).Select(entry => entry.Id).ToArray();
    }
}
