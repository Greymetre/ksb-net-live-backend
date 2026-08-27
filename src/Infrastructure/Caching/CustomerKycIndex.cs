using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Caching;

/// <summary>Where every customer stands on KYC, held in memory.
///
/// The answer the screen needs - has the file been uploaded, have the matching details been
/// typed in, has a reviewer signed it off - lives inside the custom_fields JSON, under field
/// names that live data spells several different ways. Asking SQL Server that question means
/// a LIKE pattern per spelling per document against an nvarchar(max) column: about thirty
/// passes over every customer, which takes seconds and gets worse as the table grows.
///
/// Reading the column once and answering in memory costs a little over a second for 14,000
/// customers, so it is done once and kept. Every listing, every count and every filter then
/// works off this, and only the ten customers actually on screen are read from the database
/// in full. It is rebuilt when a review is recorded or a customer is edited, and in any case
/// once the entry has gone stale.</summary>
public sealed class CustomerKycIndex
{
    /// <summary>How long a build stays usable when nothing has explicitly invalidated it.
    /// Anything this API changes invalidates it directly; the window only covers edits made
    /// somewhere else, such as straight against the database.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private volatile Snapshot? _snapshot;

    public CustomerKycIndex(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public sealed record Snapshot(IReadOnlyList<CustomerKycEntry> Entries, DateTime BuiltAt);

    public async Task<Snapshot> GetAsync(CancellationToken cancellationToken)
    {
        var current = _snapshot;
        if (current is not null && DateTime.UtcNow - current.BuiltAt < Lifetime) return current;

        await _buildLock.WaitAsync(cancellationToken);
        try
        {
            // Another request may have rebuilt it while this one waited.
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

    /// <summary>Drops the held answer. Called wherever a KYC status or a customer's details
    /// change, so the screen never reports a review that has already been recorded.</summary>
    public void Invalidate() => _snapshot = null;

    private async Task<IReadOnlyList<CustomerKycEntry>> BuildAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entries = new List<CustomerKycEntry>();
        var rows = db.Customers.AsNoTracking()
            .Where(x => x.DeletedAt == null)
            .OrderByDescending(x => x.Id)
            .Select(x => new { x.Id, x.Name, x.FirstName, x.LastName, x.Mobile, x.CustomerCode, x.CustomerType, x.CreatedBy, x.Active, x.CustomFields })
            .AsAsyncEnumerable();

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            var fields = ReadFields(row.CustomFields);
            entries.Add(CustomerKycEntry.From(
                row.Id, row.Name, row.FirstName, row.LastName, row.Mobile, row.CustomerCode,
                row.CustomerType, row.CreatedBy, row.Active, fields));
        }

        return entries;
    }

    private static Dictionary<string, string?> ReadFields(string? json)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return fields;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return fields;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                fields[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.ToString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => property.Value.ToString()
                };
            }
        }
        catch (JsonException)
        {
            // Legacy rows carry the odd unparseable document. They read as "nothing filled in",
            // which is what the screen would show for them anyway.
        }

        return fields;
    }
}
