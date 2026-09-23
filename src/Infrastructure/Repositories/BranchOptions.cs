using Application.DTOs.Users;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

/// <summary>
/// The branch dropdown every screen builds for itself, with each branch's zone on it.
/// A screen that filters on a zone shows only that zone's branches, so the two filters
/// agree; a branch that has no zone yet (see MasterDataRepository.BranchZoneId) carries
/// none and only shows while no zone is picked.
/// </summary>
public static class BranchOptions
{
    /// <summary>Active branches, by name, each with its zone.</summary>
    public static async Task<List<OptionDto>> BranchOptionsAsync(this AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var branches = await dbContext.Branches.AsNoTracking()
            .Where(x => x.DeletedAt == null)
            .OrderBy(x => x.BranchName)
            .Select(x => new OptionDto { Id = x.Id, Name = x.BranchName })
            .ToListAsync(cancellationToken);
        await dbContext.AttachBranchZonesAsync(branches, cancellationToken);
        return branches;
    }

    /// <summary>Fills in the zone on a branch list a screen has already built.</summary>
    public static async Task AttachBranchZonesAsync(this AppDbContext dbContext, IReadOnlyCollection<OptionDto> branches, CancellationToken cancellationToken)
    {
        if (branches.Count == 0) return;
        var zones = await MasterDataRepository.BranchZonesAsync(dbContext, cancellationToken);
        foreach (var branch in branches)
            branch.ZoneId = zones.TryGetValue(branch.Id, out var zoneId) ? zoneId : null;
    }

    /// <summary>The zone of each branch id, for the option lists that are anonymous types.</summary>
    public static Task<IReadOnlyDictionary<ulong, ulong>> BranchZoneMapAsync(this AppDbContext dbContext, CancellationToken cancellationToken) =>
        MasterDataRepository.BranchZonesAsync(dbContext, cancellationToken);
}
