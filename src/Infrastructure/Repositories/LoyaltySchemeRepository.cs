using System.Text.Json;
using Application.DTOs.LoyaltySchemes;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Domain.Services;
using Shared.Json;

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

        // A dealer login carries scheme.view, so it reaches this listing too. A scheme that
        // names that dealer in Excluded dealers must not appear here either - it is meant to
        // be invisible to them everywhere, and this is the one screen that was not going
        // through the audience check.
        var actorDealerId = await DealerCustomerIdAsync(filter.ActorUserId, cancellationToken);
        if (actorDealerId.HasValue)
        {
            schemes = schemes
                .Where(scheme => !SchemeEligibility.ReadExcludedDealerIds(scheme.ExcludedDealerIds).Contains(actorDealerId.Value))
                .ToList();
        }

        var creators = await LoadCreatorsAsync(schemes.SelectMany(SchemePeopleIds), cancellationToken);
        return schemes.Select(x => ToDto(x, creators)).ToList();
    }

    /// <summary>The customer a dealer login belongs to, or null for an internal user.</summary>
    private async Task<ulong?> DealerCustomerIdAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) return null;
        return await _dbContext.Users.AsNoTracking()
            .Where(x => x.Id == actorUserId.Value && x.CustomerId.HasValue)
            .Join(_dbContext.Customers.AsNoTracking(), x => x.CustomerId, x => x.Id, (_, customer) => customer)
            .Where(x => x.DeletedAt == null && x.CustomerType == SchemeEligibility.DealerCustomerType)
            .Select(x => (ulong?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
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
            ExcludedDealerIds = ReadExcludedDealerIds(scheme.ExcludedDealerIds),
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

    /// <summary>
    /// Every dealer, for the scheme form's picker. There are a few hundred of them, so the
    /// whole list goes in one response and the screen searches and narrows it without
    /// asking again - which is what keeps the box instant.
    ///
    /// Branch and zone come from the dealer's assigned employee, the same rule
    /// SchemeAudienceService applies when it decides who a scheme reaches; reading them off
    /// the dealer row instead would offer dealers the scheme would never actually reach.
    /// The assignment is read through the indexed columns rather than by searching the JSON.
    /// </summary>
    public async Task<IReadOnlyCollection<SchemeDealerOptionDto>> GetDealerOptionsAsync(CancellationToken cancellationToken)
    {
        const ulong distributorCustomerType = 1;

        var dealers = await _dbContext.Customers.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.CustomerType == distributorCustomerType && x.Active == "Y")
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.CustomerCode,
                x.Mobile,
                x.Email,
                x.CustomFields,
                EmployeeId = x.AssignedEmployeeId ?? x.AssignedSalesExecutiveId ?? x.AssignedFallbackEmployeeId
            })
            .ToListAsync(cancellationToken);
        if (dealers.Count == 0) return [];

        var employeeIds = dealers.Where(x => x.EmployeeId.HasValue).Select(x => (decimal)x.EmployeeId!.Value).Distinct().ToArray();
        var employees = employeeIds.Length == 0
            ? []
            : await _dbContext.Users.AsNoTracking()
                .Where(x => employeeIds.Contains(x.Id))
                .Select(x => new { x.Id, x.PrimaryBranchId, x.BranchId, x.DivisionId })
                .ToDictionaryAsync(x => x.Id, x => x, cancellationToken);

        var branchIds = employees.Values
            .Select(x => x.PrimaryBranchId ?? FirstAssignedId(x.BranchId))
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var branches = branchIds.Length == 0 ? [] : await _dbContext.Branches.AsNoTracking()
            .Where(x => branchIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);

        var divisionIds = employees.Values.Where(x => x.DivisionId.HasValue)
            .Select(x => x.DivisionId!.Value).Distinct().ToArray();
        var divisions = divisionIds.Length == 0 ? [] : await _dbContext.Divisions.AsNoTracking()
            .Where(x => divisionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DivisionName, cancellationToken);

        var stateIds = dealers.Select(x => ReadStateId(x.CustomFields))
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var states = stateIds.Length == 0 ? [] : await _dbContext.States.AsNoTracking()
            .Where(x => stateIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.StateName, cancellationToken);

        return dealers.Select(dealer =>
        {
            string? branch = null;
            string? zone = null;
            if (dealer.EmployeeId.HasValue && employees.TryGetValue((ulong)dealer.EmployeeId.Value, out var employee))
            {
                var branchId = employee.PrimaryBranchId ?? FirstAssignedId(employee.BranchId);
                if (branchId.HasValue) branch = branches.GetValueOrDefault(branchId.Value);
                if (employee.DivisionId.HasValue) zone = divisions.GetValueOrDefault(employee.DivisionId.Value);
            }

            var stateId = ReadStateId(dealer.CustomFields);
            return new SchemeDealerOptionDto
            {
                Id = dealer.Id,
                Name = dealer.Name,
                Code = dealer.CustomerCode,
                Mobile = dealer.Mobile,
                Email = dealer.Email,
                Branch = branch,
                Zone = zone,
                State = stateId.HasValue ? states.GetValueOrDefault(stateId.Value) : null
            };
        })
        .OrderBy(x => x.Name)
        .ToList();
    }

    private static ulong? ReadStateId(string? customFields)
    {
        var fields = CustomFieldsJson.Read(customFields);
        return FirstAssignedId(fields.GetValueOrDefault("state_id")) ?? FirstAssignedId(fields.GetValueOrDefault("billing_state"));
    }

    /// <summary>A user can carry several branches as a comma list; the first is the one that counts.</summary>
    private static ulong? FirstAssignedId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var first = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return ulong.TryParse(first, out var parsed) && parsed > 0 ? parsed : null;
    }

    private static ulong[] ReadExcludedDealerIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<ulong[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
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
