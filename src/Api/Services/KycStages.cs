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

    // One id-to-stage map per index build, made on first use and dropped with the build.
    private static readonly ConditionalWeakTable<CustomerKycIndex.Snapshot, IReadOnlyDictionary<ulong, string>> Maps = new();

    public static bool IsStage(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

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
        var map = await StageMapAsync(index, cancellationToken);
        return map.Where(pair => pair.Value == stage).Select(pair => pair.Key).ToArray();
    }

    /// <summary>Counts for a set of customers, in the shape both apps read.</summary>
    public static object Summary(IEnumerable<string> stages)
    {
        var counts = All.ToDictionary(stage => stage, _ => 0);
        var total = 0;
        foreach (var stage in stages)
        {
            total++;
            counts[IsStage(stage) ? stage : None]++;
        }

        return new
        {
            total,
            approved = counts[Approved],
            complete_pending = counts[CompletePending],
            partial = counts[Partial],
            not_started = counts[None],
            stages = All.Select(stage => new { key = stage, label = Label(stage), count = counts[stage] })
        };
    }

    public static async Task<object> SummaryAsync(CustomerKycIndex index, IEnumerable<ulong> customerIds, CancellationToken cancellationToken)
    {
        var map = await StageMapAsync(index, cancellationToken);
        return Summary(customerIds.Distinct().Select(id => StageOf(map, id)));
    }
}
