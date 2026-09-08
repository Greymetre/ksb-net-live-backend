using System.Text.Json;
using Application.DTOs.LoyaltySchemes;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Domain.Services;

namespace Infrastructure.Repositories;

public sealed class LoyaltySchemeRepository : ILoyaltySchemeRepository
{
    private const int MaxRows = 50000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _dbContext;

    public LoyaltySchemeRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyCollection<LoyaltySchemeDto>> GetSchemesAsync(LoyaltySchemeFilterDto filter, CancellationToken cancellationToken)
    {
        var query = BaseQuery();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(x => x.SchemeName.Contains(search)
                || x.SchemeCode.Contains(search)
                || x.CustomerType.Contains(search)
                || x.SchemeTag.Contains(search)
                || x.AreaScope.Contains(search)
                || x.AreaValues.Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status.Trim();
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
            query = status switch
            {
                "Expired" => query.Where(x => x.EndDate < today),
                "Live" => query.Where(x => (x.Status == "Published" || x.Status == "Live") && x.StartDate <= today && x.EndDate >= today),
                "Approved" => query.Where(x => x.Status == "Approved" || ((x.Status == "Published" || x.Status == "Live") && x.StartDate > today)),
                _ => query.Where(x => x.Status == status && x.EndDate >= today)
            };
        }

        var schemes = await query
            .Include(x => x.Slabs)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(MaxRows)
            .ToListAsync(cancellationToken);

        var creators = await LoadCreatorsAsync(schemes.SelectMany(SchemePeopleIds), cancellationToken);
        return schemes.Select(x => ToDto(x, creators)).ToList();
    }

    public async Task<LoyaltySchemeDto?> GetSchemeAsync(ulong id, CancellationToken cancellationToken)
    {
        var scheme = await BaseQuery()
            .Include(x => x.Slabs)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (scheme is null) return null;
        var creators = await LoadCreatorsAsync(SchemePeopleIds(scheme), cancellationToken);
        return ToDto(scheme, creators);
    }

    public async Task<LoyaltyScheme?> FindSchemeEntityAsync(ulong id, CancellationToken cancellationToken) =>
        await _dbContext.LoyaltySchemes
            .Include(x => x.Slabs)
            .FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, cancellationToken);

    public async Task<bool> SchemeCodeExistsAsync(string code, ulong? exceptId, CancellationToken cancellationToken) =>
        await _dbContext.LoyaltySchemes.AnyAsync(x => x.DeletedAt == null
            && x.SchemeCode == code
            && (!exceptId.HasValue || x.Id != exceptId.Value), cancellationToken);

    public async Task<string?> GetLastSchemeCodeAsync(string prefix, CancellationToken cancellationToken)
    {
        var likePrefix = prefix + "-%";
        var codes = await _dbContext.LoyaltySchemes.AsNoTracking()
            .Where(x => x.DeletedAt == null
                && x.SchemeType == "Invoice"
                && EF.Functions.Like(x.SchemeCode, likePrefix))
            .Select(x => x.SchemeCode)
            .ToListAsync(cancellationToken);

        return codes
            .Select(code => new { Code = code, Sequence = ReadSequence(code, prefix) })
            .Where(x => x.Sequence.HasValue)
            .OrderByDescending(x => x.Sequence!.Value)
            .Select(x => x.Code)
            .FirstOrDefault();
    }

    public async Task<LoyaltySchemeDto> CreateSchemeAsync(LoyaltyScheme scheme, CancellationToken cancellationToken)
    {
        await _dbContext.LoyaltySchemes.AddAsync(scheme, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetSchemeAsync(scheme.Id, cancellationToken) ?? throw new InvalidOperationException("Created scheme could not be loaded.");
    }

    public async Task<LoyaltySchemeDto> SaveSchemeAsync(LoyaltyScheme scheme, CancellationToken cancellationToken)
    {
        scheme.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetSchemeAsync(scheme.Id, cancellationToken) ?? throw new InvalidOperationException("Scheme could not be loaded.");
    }

    public async Task<bool> DeleteSchemeAsync(LoyaltyScheme scheme, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        scheme.DeletedAt = now;
        scheme.UpdatedAt = now;
        foreach (var slab in scheme.Slabs)
        {
            slab.DeletedAt = now;
            slab.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<LoyaltySchemeOptionsDto> GetOptionsAsync(CancellationToken cancellationToken)
    {
        var branches = await _dbContext.Branches.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.Active == "Y")
            .OrderBy(x => x.BranchName)
            .Select(x => new LoyaltySchemeOptionDto { Id = x.Id, Name = x.BranchName })
            .ToListAsync(cancellationToken);

        var zones = (await _dbContext.Divisions.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.Active == "Y")
            .Select(x => new LoyaltySchemeOptionDto { Id = x.Id, Name = x.DivisionName })
            .ToListAsync(cancellationToken)).ByZone(x => x.Name).ToList();

        var states = await _dbContext.States.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.Active == "Y")
            .OrderBy(x => x.StateName)
            .Select(x => new LoyaltySchemeOptionDto { Id = x.Id, Name = x.StateName })
            .ToListAsync(cancellationToken);

        var customers = await _dbContext.Customers.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.Active == "Y")
            .OrderBy(x => x.Name)
            .Take(MaxRows)
            .Select(x => new LoyaltySchemeOptionDto
            {
                Id = x.Id,
                Name = x.CustomerCode == string.Empty ? x.Name : x.CustomerCode + " - " + x.Name
            })
            .ToListAsync(cancellationToken);

        return new LoyaltySchemeOptionsDto
        {
            Branches = branches,
            Zones = zones,
            States = states,
            Customers = customers
        };
    }

    private IQueryable<LoyaltyScheme> BaseQuery() =>
        _dbContext.LoyaltySchemes.AsNoTracking().Where(x => x.DeletedAt == null);

    /// <summary>Everyone a scheme records: who wrote it, who sent it on, who approved or
    /// rejected it, and who published it. One lookup covers all of them.</summary>
    private static IEnumerable<ulong?> SchemePeopleIds(LoyaltyScheme scheme) =>
        [scheme.CreatedBy, scheme.SubmittedBy, scheme.ApprovedBy, scheme.RejectedBy, scheme.PublishedBy];

    private async Task<Dictionary<ulong, string>> LoadCreatorsAsync(IEnumerable<ulong?> ids, CancellationToken cancellationToken)
    {
        var userIds = ids.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        if (userIds.Length == 0) return [];

        // A user who has since been removed still has to be named here: this is the
        // record of who did what, and a blank would read as nobody having done it.
        return await _dbContext.Users.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(x => userIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
    }

    private static string? PersonName(ulong? id, IReadOnlyDictionary<ulong, string> people) =>
        id.HasValue && people.TryGetValue(id.Value, out var name) ? name : null;

    private static LoyaltySchemeDto ToDto(LoyaltyScheme scheme, IReadOnlyDictionary<ulong, string> creators)
    {
        var areaValues = ReadAreaValues(scheme.AreaValues);
        return new LoyaltySchemeDto
        {
            Id = scheme.Id,
            Active = scheme.Active,
            SchemeName = scheme.SchemeName,
            SchemeCode = scheme.SchemeCode,
            SchemeDescription = scheme.SchemeDescription,
            SchemeNote = scheme.SchemeNote,
            SchemeTag = scheme.SchemeTag,
            CustomerType = scheme.CustomerType,
            AreaScope = scheme.AreaScope,
            AreaValues = areaValues,
            AreaDisplay = AreaDisplay(scheme.AreaScope, areaValues),
            StartDate = scheme.StartDate,
            EndDate = scheme.EndDate,
            SchemeType = scheme.SchemeType,
            BasedOn = scheme.BasedOn,
            RedemptionEnabled = scheme.RedemptionEnabled,
            Status = DisplayStatus(scheme),
            WorkflowStatus = scheme.Status,
            BrochurePath = scheme.BrochurePath,
            SubmittedAt = scheme.SubmittedAt,
            SubmittedBy = scheme.SubmittedBy,
            SubmittedByName = PersonName(scheme.SubmittedBy, creators),
            ApprovedAt = scheme.ApprovedAt,
            ApprovedBy = scheme.ApprovedBy,
            ApprovedByName = PersonName(scheme.ApprovedBy, creators),
            ApprovalRemark = scheme.ApprovalRemark,
            RejectedAt = scheme.RejectedAt,
            RejectedBy = scheme.RejectedBy,
            RejectedByName = PersonName(scheme.RejectedBy, creators),
            RejectionRemark = scheme.RejectionRemark,
            PublishedAt = scheme.PublishedAt,
            PublishedBy = scheme.PublishedBy,
            PublishedByName = PersonName(scheme.PublishedBy, creators),
            CreatedBy = scheme.CreatedBy,
            CreatedByName = PersonName(scheme.CreatedBy, creators),
            CreatedAt = scheme.CreatedAt,
            Slabs = scheme.Slabs
                .Where(x => x.DeletedAt == null)
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .Select(x => new LoyaltySchemeSlabDto
                {
                    Id = x.Id,
                    TierName = x.TierName,
                    ValueFrom = x.ValueFrom,
                    ValueTo = x.ValueTo,
                    RewardValue = x.RewardValue,
                    RewardType = x.RewardType,
                    SortOrder = x.SortOrder
                })
                .ToList()
        };
    }

    private static string[] ReadAreaValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string AreaDisplay(string scope, IReadOnlyCollection<string> values) =>
        string.Equals(scope, "All", StringComparison.OrdinalIgnoreCase) || values.Count == 0
            ? "All India"
            : string.Join(", ", values);

    private static string DisplayStatus(LoyaltyScheme scheme)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        if (scheme.EndDate < today)
        {
            return "Expired";
        }
        if (string.Equals(scheme.Status, "Published", StringComparison.OrdinalIgnoreCase))
            return today >= scheme.StartDate ? "Live" : "Approved";
        return scheme.Status;
    }

    private static int? ReadSequence(string code, string prefix)
    {
        if (!code.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) return null;
        var suffix = code[(prefix.Length + 1)..];
        return int.TryParse(suffix, out var sequence) ? sequence : null;
    }
}
