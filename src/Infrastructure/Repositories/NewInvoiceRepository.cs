using System.Globalization;
using System.Data;
using System.Text.Json;
using Application.DTOs.NewInvoices;
using Application.Common;
using Application.Interfaces.Repositories;
using Domain.Constants;
using Domain.Entities;
using Domain.Services;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public sealed class NewInvoiceRepository : INewInvoiceRepository
{
    private const int MaxRows = 50000;
    private const ulong DistributorCustomerType = 1;
    private const ulong RetailerCustomerType = 2;
    private const ulong InfluencerCustomerType = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Where a retailer's distributor mapping lives inside the custom_fields JSON. Domestic
    // and agri are separate assignments; a retailer may carry either.
    private const string DistributorJsonPath = "$.distributor_name";
    private const string AgriDistributorJsonPath = "$.agri_distributor";
    private const string ShopNameJsonPath = "$.shop_name";
    private const string OwnerNameJsonPath = "$.owner_name";
    private const string AddressLineJsonPath = "$.address_line";
    private const string BeltAreaJsonPath = "$.belt_area_market_name";

    private const string SuperAdminRoleName = "SUPERADMIN";
    private const string AsrDesignationName = "ASR";
    private readonly AppDbContext _dbContext;

    public NewInvoiceRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PagedResult<NewInvoiceDto>> GetInvoicesAsync(NewInvoiceFilterDto filter, ulong? actorUserId, CancellationToken cancellationToken)
    {
        // A dealer login stays pinned to its own retailers; the dealer filter is for
        // internal users, who have no dealer scope of their own. Only the actor's own
        // dealer identity stands in for the reporting scope - a dealer picked in the
        // filter narrows the rows but must never widen them.
        var actorDistributorCustomerId = await GetDistributorCustomerIdAsync(actorUserId, cancellationToken);
        var distributorCustomerId = actorDistributorCustomerId ?? filter.DistributorCustomerId;
        var scoped = await ApplyReportingScopeAsync(BaseQuery(distributorCustomerId), actorUserId, actorDistributorCustomerId, cancellationToken);
        var query = ApplyFilters(scoped, filter);
        var page = Pagination.Page(filter.Page);
        var pageSize = Pagination.PageSize(filter.PageSize);
        var total = await query.LongCountAsync(cancellationToken);
        // Work queue order: whatever still needs action comes first (pending, then the
        // two half-approved stages), fully approved and rejected drop to the bottom.
        // Inside a stage the oldest invoice date leads so ageing invoices stay on top.
        var orderedQuery = query
            .OrderBy(x => x.Invoice.ApprovalStatus == NewInvoice.StatusPending ? 0
                : x.Invoice.ApprovalStatus == NewInvoice.StatusHold ? 1
                : x.Invoice.ApprovalStatus == NewInvoice.StatusApprovedSs ? 2
                : x.Invoice.ApprovalStatus == NewInvoice.StatusApprovedSales ? 3
                : x.Invoice.ApprovalStatus == NewInvoice.StatusApprovedHo ? 4
                : 5)
            .ThenBy(x => x.Invoice.InvoiceDate)
            .ThenBy(x => x.Invoice.Id);
        var rows = await (filter.Unpaged
                ? orderedQuery.Take(MaxRows)
                : orderedQuery.Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);

        var cities = await LoadCitiesAsync(rows.Select(x => CityId(x.Customer)), cancellationToken);
        var assignedZones = await LoadAssignedZoneNamesAsync(rows.Select(x => x.Customer), cancellationToken);
        var assignedBranches = await LoadAssignedBranchNamesAsync(rows.Select(x => x.Customer), cancellationToken);
        var assignedDistributors = await LoadDealerNamesAsync(rows, cancellationToken);
        var assignedEmployees = await LoadAssignedEmployeeNamesAsync(rows.Select(x => x.Customer), cancellationToken);
        var schemes = await LoadSchemesAsync(rows.Select(x => x.Invoice.InvoiceDate), cancellationToken);
        var schemeInvoices = await LoadSchemeInvoicesAsync(rows.Select(x => x.Customer.Id), schemes, cancellationToken);
        var approvals = await LoadApprovalStageSummariesAsync(rows.Select(x => x.Invoice.Id), cancellationToken);
        var items = rows.SelectMany(x => ToSchemeDtos(x.Invoice, x.Customer, CityName(x.Customer, cities), AssignedZoneName(x.Customer, assignedZones), AssignedBranchName(x.Customer, assignedBranches) ?? x.Branch?.BranchName, DealerName(x.Invoice, x.Customer, assignedDistributors), AssignedEmployeeName(x.Customer, assignedEmployees), AssignedEmployeeMobile(x.Customer, assignedEmployees), x.Creator, x.Branch, schemes, schemeInvoices, ApprovalSummary(x.Invoice.Id, approvals))).ToList();
        await ApplyCreatedByLabelsAsync(items, rows.Select(x => x.Creator), cancellationToken);
        await ApplyApproverNamesAsync(items, cancellationToken);
        await ApplyAttachmentsAsync(items, cancellationToken);
        return new PagedResult<NewInvoiceDto>(items, total, page, filter.Unpaged ? items.Count : pageSize);
    }

    public async Task<NewInvoiceSummaryDto> GetInvoiceSummaryAsync(NewInvoiceFilterDto filter, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var actorDistributorCustomerId = await GetDistributorCustomerIdAsync(actorUserId, cancellationToken);
        var distributorCustomerId = actorDistributorCustomerId ?? filter.DistributorCustomerId;
        var scoped = await ApplyReportingScopeAsync(BaseQuery(distributorCustomerId), actorUserId, actorDistributorCustomerId, cancellationToken);
        var rows = ApplyFilters(scoped, filter);
        return await rows.GroupBy(_ => 1).Select(group => new NewInvoiceSummaryDto
        {
            TotalInvoices = group.Count(),
            TotalRetailers = group.Select(x => x.Invoice.SecondaryCustomerId).Distinct().Count(),
            ApprovedSs = group.Count(x => x.Invoice.ApprovalStatus == NewInvoice.StatusApprovedSs),
            ApprovedSales = group.Count(x => x.Invoice.ApprovalStatus == NewInvoice.StatusApprovedSales),
            ApprovedHo = group.Count(x => x.Invoice.ApprovalStatus == NewInvoice.StatusApprovedHo),
            Pending = group.Count(x => x.Invoice.ApprovalStatus == NewInvoice.StatusPending),
            Hold = group.Count(x => x.Invoice.ApprovalStatus == NewInvoice.StatusHold),
            Rejected = group.Count(x => x.Invoice.ApprovalStatus == NewInvoice.StatusRejected),
            TotalPoints = group.Sum(x => x.Invoice.Points),
            TotalAmount = group.Sum(x => x.Invoice.Amount)
        }).FirstOrDefaultAsync(cancellationToken) ?? new NewInvoiceSummaryDto();
    }

    public async Task<NewInvoiceDto?> GetInvoiceAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var distributorCustomerId = await GetDistributorCustomerIdAsync(actorUserId, cancellationToken);
        var scoped = await ApplyReportingScopeAsync(BaseQuery(distributorCustomerId), actorUserId, distributorCustomerId, cancellationToken);
        var row = await scoped.Where(x => x.Invoice.Id == id).FirstOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        var cities = await LoadCitiesAsync([CityId(row.Customer)], cancellationToken);
        var assignedZones = await LoadAssignedZoneNamesAsync([row.Customer], cancellationToken);
        var assignedBranches = await LoadAssignedBranchNamesAsync([row.Customer], cancellationToken);
        var assignedDistributors = await LoadDealerNamesAsync([row], cancellationToken);
        var assignedEmployees = await LoadAssignedEmployeeNamesAsync([row.Customer], cancellationToken);
        var schemes = await LoadSchemesAsync([row.Invoice.InvoiceDate], cancellationToken);
        var schemeInvoices = await LoadSchemeInvoicesAsync([row.Customer.Id], schemes, cancellationToken);
        var approvals = await LoadApprovalStageSummariesAsync([row.Invoice.Id], cancellationToken);
        var dto = ToSchemeDtos(row.Invoice, row.Customer, CityName(row.Customer, cities), AssignedZoneName(row.Customer, assignedZones), AssignedBranchName(row.Customer, assignedBranches) ?? row.Branch?.BranchName, DealerName(row.Invoice, row.Customer, assignedDistributors), AssignedEmployeeName(row.Customer, assignedEmployees), AssignedEmployeeMobile(row.Customer, assignedEmployees), row.Creator, row.Branch, schemes, schemeInvoices, ApprovalSummary(row.Invoice.Id, approvals)).First();
        await ApplyCreatedByLabelsAsync([dto], [row.Creator], cancellationToken);
        await ApplyApproverNamesAsync([dto], cancellationToken);
        await ApplyAttachmentsAsync([dto], cancellationToken);
        dto.ApprovalLogs = await GetApprovalLogsAsync(id, cancellationToken);
        return dto;
    }

    public async Task<IReadOnlyCollection<RetailerOptionDto>> GetRetailerOptionsAsync(string? search, ulong? actorUserId, CancellationToken cancellationToken) =>
        (await GetRetailerOptionPageAsync(search, actorUserId, 1, MaxRows, cancellationToken)).Items;

    /// <summary>A page of the retailer picker.
    ///
    /// Unpaged, this answers with every retailer the actor can reach - about fourteen
    /// thousand rows and two megabytes for an admin. A phone cannot draw that, and a
    /// dropdown that has to be typed into anyway never needed it: the search runs in
    /// SQL and the caller asks for the next page only if it scrolls that far.</summary>
    public async Task<PagedResult<RetailerOptionDto>> GetRetailerOptionPageAsync(
        string? search,
        ulong? actorUserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var distributorCustomerId = await GetDistributorCustomerIdAsync(actorUserId, cancellationToken);
        var query = _dbContext.Customers.AsNoTracking()
            .Where(x => x.Active == "Y" && (x.CustomerType == RetailerCustomerType || x.CustomerType == InfluencerCustomerType));
        query = ApplyDistributorRetailerScope(query, distributorCustomerId);
        query = await ApplyRetailerReportingScopeAsync(query, actorUserId, distributorCustomerId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Shop name, owner name, address and market area live in the JSON, and they are
            // what people type. Matching the whole document instead meant reading every
            // row's entire JSON - and matching on things nobody searches by, so a term
            // could land on a Google Maps link, a GPS coordinate, or "APPROVED", which
            // returned every approved retailer.
            var term = search.Trim();
            var likeTerm = $"%{term}%";
            query = query.Where(x => x.Name.Contains(term)
                || (x.Mobile != null && x.Mobile.Contains(term))
                || x.CustomerCode.Contains(term)
                || (x.CustomFields != null
                    && (EF.Functions.Like(AppDbContext.JsonValue(x.CustomFields, ShopNameJsonPath), likeTerm)
                        || EF.Functions.Like(AppDbContext.JsonValue(x.CustomFields, OwnerNameJsonPath), likeTerm)
                        || EF.Functions.Like(AppDbContext.JsonValue(x.CustomFields, AddressLineJsonPath), likeTerm)
                        || EF.Functions.Like(AppDbContext.JsonValue(x.CustomFields, BeltAreaJsonPath), likeTerm))));
        }

        var currentPage = Pagination.Page(page);
        var size = Math.Clamp(pageSize, 1, MaxRows);

        // Counting the matches ran this same filter a second time and doubled what the
        // picker cost. Nothing displays a total - the callers only need to know whether
        // to offer another page - so one extra row answers that for free.
        var retailers = await query
            .OrderBy(x => x.Name)
            .Skip((currentPage - 1) * size)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var hasMore = retailers.Count > size;
        if (hasMore) retailers.RemoveAt(retailers.Count - 1);
        // "How many are known so far", not the true total. It is only ever read back
        // through has_more, which this satisfies exactly.
        var total = (long)(currentPage - 1) * size + retailers.Count + (hasMore ? 1 : 0);

        var cities = await LoadCitiesAsync(retailers.Select(CityId), cancellationToken);
        var items = retailers.Select(customer => new RetailerOptionDto
        {
            Id = customer.Id,
            OwnerName = OwnerName(customer),
            ShopName = ShopName(customer),
            MobileNumber = MobileNumber(customer),
            CityName = CityName(customer, cities),
            Address = Address(customer)
        }).ToList();

        return new PagedResult<RetailerOptionDto>(items, total, currentPage, size);
    }

    /// <summary>Dealer list for the listing filter. A dealer login gets only itself,
    /// so the dropdown can never be used to look at another dealer's invoices.</summary>
    public async Task<IReadOnlyCollection<DealerOptionDto>> GetDealerOptionsAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        var actorDistributorId = await GetDistributorCustomerIdAsync(actorUserId, cancellationToken);
        var query = _dbContext.Customers.AsNoTracking()
            .Where(x => x.Active == "Y" && x.CustomerType == DistributorCustomerType);

        if (actorDistributorId.HasValue) query = query.Where(x => x.Id == actorDistributorId.Value);

        return await query
            .OrderBy(x => x.Name)
            .Take(MaxRows)
            .Select(x => new DealerOptionDto { Id = x.Id, Name = x.Name })
            .ToListAsync(cancellationToken);
    }

    /// <summary>The schemes that apply to the retailers this user can reach.
    ///
    /// A scheme is targeted at a customer type and at one area - all of India, a branch, a
    /// zone, a state, or named customers - so it belongs on this screen when any one of the
    /// user's retailers would qualify for it. Branch and zone come from the retailer's
    /// assigned employee, the state from the retailer's own address, which is exactly how
    /// the CRM and the dealer app decide it.
    ///
    /// The audiences are collapsed before matching: for an area-targeted scheme only the
    /// (type, branch, zone, state) combination matters, and a few hundred retailers reduce
    /// to a handful of those. Named-customer schemes are the one case that needs the
    /// retailers themselves, so those are matched separately.</summary>
    public async Task<IReadOnlyCollection<FieldSchemeDto>> GetFieldSchemesAsync(ulong? actorUserId, DateOnly today, CancellationToken cancellationToken)
    {
        var schemes = await _dbContext.LoyaltySchemes.AsNoTracking()
            .Where(x => x.DeletedAt == null
                && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live")
                && x.SchemeType == "Invoice")
            .ToListAsync(cancellationToken);
        if (schemes.Count == 0) return [];

        var slabCounts = await _dbContext.LoyaltySchemeSlabs.AsNoTracking()
            .GroupBy(x => x.LoyaltySchemeId)
            .Select(group => new { SchemeId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.SchemeId, x => x.Count, cancellationToken);

        var audiences = await ResolveFieldAudiencesAsync(actorUserId, cancellationToken);

        return schemes
            .Where(scheme => audiences.Any(audience => SchemeEligibility.Matches(scheme, EffectiveSchemeDate(scheme, today), audience)))
            .Select(scheme => ToFieldScheme(scheme, today, slabCounts.GetValueOrDefault(scheme.Id)))
            .OrderByDescending(scheme => scheme.IsLive)
            .ThenByDescending(scheme => scheme.EndDate)
            .ToList();
    }

    public async Task<FieldSchemeDetailDto?> GetFieldSchemeAsync(ulong id, ulong? actorUserId, DateOnly today, CancellationToken cancellationToken)
    {
        var scheme = await _dbContext.LoyaltySchemes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live") && x.SchemeType == "Invoice", cancellationToken);
        if (scheme is null) return null;

        var audiences = await ResolveFieldAudiencesAsync(actorUserId, cancellationToken);
        if (!audiences.Any(audience => SchemeEligibility.Matches(scheme, EffectiveSchemeDate(scheme, today), audience))) return null;

        var slabs = await _dbContext.LoyaltySchemeSlabs.AsNoTracking()
            .Where(x => x.LoyaltySchemeId == scheme.Id)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.ValueFrom)
            .ToListAsync(cancellationToken);

        // What this user's own retailers have done under the scheme, through the same scope
        // the invoice list uses.
        var invoices = (await GetInvoicesAsync(new NewInvoiceFilterDto { SchemeId = scheme.Id, Unpaged = true }, actorUserId, cancellationToken)).Items;
        var distinct = invoices.GroupBy(x => x.Id).Select(group => group.First()).ToList();

        return new FieldSchemeDetailDto
        {
            Scheme = ToFieldScheme(scheme, today, slabs.Count),
            Slabs = slabs.Select(slab => new FieldSchemeSlabDto
            {
                FromAmount = slab.ValueFrom,
                ToAmount = slab.ValueTo ?? 0,
                Value = slab.RewardValue,
                ValueType = SchemeReward.TypeFor(scheme, slab),
                RewardLabel = SchemeReward.Label(scheme, slab)
            }).ToList(),
            InvoiceCount = distinct.Count,
            RetailerCount = distinct.Select(x => x.SecondaryCustomerId).Distinct().Count(),
            ApprovedAmount = distinct.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo)
                .Sum(x => x.HoApprovedAmount ?? x.Amount),
            PendingAmount = distinct
                .Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected)
                .Sum(x => x.SalesApprovedAmount ?? x.SsApprovedAmount ?? x.Amount),
            PointsEarned = invoices.Where(x => x.ApprovalStatus == NewInvoice.StatusApprovedHo).Sum(x => x.SchemePoints),
            PointsExpected = invoices
                .Where(x => x.ApprovalStatus is not NewInvoice.StatusApprovedHo and not NewInvoice.StatusRejected)
                .Sum(x => x.ExpectedSchemePoints)
        };
    }

    /// <summary>An expired scheme is still matched against its own period, so it can be shown
    /// as "expired" rather than disappearing on the date check.</summary>
    private static DateOnly EffectiveSchemeDate(LoyaltyScheme scheme, DateOnly today) =>
        today < scheme.StartDate ? scheme.StartDate : today > scheme.EndDate ? scheme.EndDate : today;

    private static FieldSchemeDto ToFieldScheme(LoyaltyScheme scheme, DateOnly today, int slabCount)
    {
        var expired = scheme.EndDate < today;
        var upcoming = scheme.StartDate > today;
        return new FieldSchemeDto
        {
            Id = scheme.Id,
            Name = scheme.SchemeName,
            Code = scheme.SchemeCode,
            SchemeNote = scheme.SchemeNote,
            BrochurePath = scheme.BrochurePath,
            Tag = scheme.SchemeTag,
            WalletType = scheme.SchemeTag?.Contains("booster", StringComparison.OrdinalIgnoreCase) == true ? "Booster" : "Regular",
            BasedOn = scheme.BasedOn,
            StartDate = scheme.StartDate,
            EndDate = scheme.EndDate,
            Status = expired ? "expired" : upcoming ? "upcoming" : "live",
            StatusLabel = expired ? "Expired" : upcoming ? "Upcoming" : "Live",
            IsLive = !expired && !upcoming,
            DaysRemaining = expired || upcoming ? 0 : scheme.EndDate.DayNumber - today.DayNumber,
            AreaScope = scheme.AreaScope,
            AreaValues = SchemeEligibility.ReadAreaValues(scheme.AreaValues).ToList(),
            CustomerType = scheme.CustomerType,
            SlabCount = slabCount
        };
    }

    /// <summary>Every audience the signed-in user's retailers add up to.</summary>
    private async Task<IReadOnlyList<SchemeAudience>> ResolveFieldAudiencesAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        var distributorCustomerId = await GetDistributorCustomerIdAsync(actorUserId, cancellationToken);
        var query = _dbContext.Customers.AsNoTracking()
            .Where(x => x.Active == "Y" && (x.CustomerType == RetailerCustomerType || x.CustomerType == InfluencerCustomerType));
        query = ApplyDistributorRetailerScope(query, distributorCustomerId);
        query = await ApplyRetailerReportingScopeAsync(query, actorUserId, distributorCustomerId, cancellationToken);

        var customers = await query.Take(MaxRows).ToListAsync(cancellationToken);
        if (customers.Count == 0) return [];

        var employeeIds = customers
            .Select(customer => FirstULong(ReadField(customer, "employee_id"))
                ?? FirstULong(ReadField(customer, "sales_executive_id"))
                ?? customer.ExecutiveId)
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();

        var employees = employeeIds.Length == 0
            ? []
            : await _dbContext.Users.AsNoTracking().Where(x => employeeIds.Contains(x.Id))
                .Select(x => new { x.Id, x.PrimaryBranchId, x.BranchId, x.DivisionId })
                .ToListAsync(cancellationToken);

        var branchIds = employees.Select(x => x.PrimaryBranchId ?? FirstULong(x.BranchId))
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var divisionIds = employees.Where(x => x.DivisionId.HasValue).Select(x => x.DivisionId!.Value).Distinct().ToArray();
        var stateIds = customers.Select(SchemeEligibility.ReadStateId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();

        var branches = branchIds.Length == 0 ? [] : await _dbContext.Branches.AsNoTracking()
            .Where(x => branchIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
        var divisions = divisionIds.Length == 0 ? [] : await _dbContext.Divisions.AsNoTracking()
            .Where(x => divisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DivisionName, cancellationToken);
        var states = stateIds.Length == 0 ? [] : await _dbContext.States.AsNoTracking()
            .Where(x => stateIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.StateName, cancellationToken);
        var employeeById = employees.ToDictionary(x => x.Id);

        var audiences = new HashSet<SchemeAudience>();
        foreach (var customer in customers)
        {
            var employeeId = FirstULong(ReadField(customer, "employee_id"))
                ?? FirstULong(ReadField(customer, "sales_executive_id"))
                ?? customer.ExecutiveId;

            string? branchName = null;
            string? zoneName = null;
            if (employeeId.HasValue && employeeById.TryGetValue(employeeId.Value, out var employee))
            {
                var branchId = employee.PrimaryBranchId ?? FirstULong(employee.BranchId);
                if (branchId.HasValue) branchName = branches.GetValueOrDefault(branchId.Value);
                if (employee.DivisionId.HasValue) zoneName = divisions.GetValueOrDefault(employee.DivisionId.Value);
            }

            var stateId = SchemeEligibility.ReadStateId(customer);
            var stateName = stateId.HasValue ? states.GetValueOrDefault(stateId.Value) : null;

            // The area part of a scheme only ever looks at one of these, so retailers sharing
            // a branch, zone and state collapse into a single audience.
            audiences.Add(new SchemeAudience(customer.CustomerType, null, null, branchName, zoneName, stateName));
            // A scheme aimed at named customers needs the retailer itself.
            audiences.Add(new SchemeAudience(customer.CustomerType, customer.Name, customer.CustomerCode, branchName, zoneName, stateName));
        }

        return audiences.ToList();
    }

    /// <summary>The dealers a retailer is mapped to. Live data keeps the mapping in two
    /// custom fields - distributor_name for the domestic dealer and agri_distributor for the
    /// agri one - and about two hundred retailers carry two different dealers between them.
    /// Those are the retailers the invoice form has to ask about; the rest it can simply tell.</summary>
    public async Task<IReadOnlyCollection<RetailerDealerOptionDto>> GetRetailerDealerOptionsAsync(ulong customerId, CancellationToken cancellationToken)
    {
        var retailer = await _dbContext.Customers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == customerId, cancellationToken);
        if (retailer is null) return [];

        var dealerIds = new List<ulong>();
        foreach (var field in new[] { "distributor_name", "agri_distributor" })
        {
            foreach (var dealerId in AllULongs(ReadField(retailer, field)))
            {
                if (!dealerIds.Contains(dealerId)) dealerIds.Add(dealerId);
            }
        }

        // Older retailers carry the dealer as the parent record rather than in custom fields.
        if (dealerIds.Count == 0 && retailer.ParentId is > 0) dealerIds.Add(retailer.ParentId.Value);
        if (dealerIds.Count == 0) return [];

        var dealers = await _dbContext.Customers.AsNoTracking()
            .Where(x => dealerIds.Contains(x.Id) && x.CustomerType == DistributorCustomerType)
            .ToListAsync(cancellationToken);

        // Kept in the order the retailer lists them, so the domestic dealer leads.
        return dealerIds
            .Select(dealerId => dealers.FirstOrDefault(dealer => dealer.Id == dealerId))
            .Where(dealer => dealer is not null)
            .Select(dealer => new RetailerDealerOptionDto
            {
                Id = dealer!.Id,
                Name = dealer.Name,
                FirmName = DealerFirmName(dealer),
                Code = string.IsNullOrWhiteSpace(dealer.CustomerCode) ? null : dealer.CustomerCode.Trim()
            })
            .ToList();
    }

    /// <summary>Every id in a field that may hold one or several, comma separated.</summary>
    private static IEnumerable<ulong> AllULongs(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ulong.TryParse(part, out var parsed) && parsed > 0) yield return parsed;
        }
    }

    public async Task<Customer?> GetRetailerAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var distributorCustomerId = await GetDistributorCustomerIdAsync(actorUserId, cancellationToken);
        var query = _dbContext.Customers.Where(x => x.Id == id && x.Active == "Y" && (x.CustomerType == RetailerCustomerType || x.CustomerType == InfluencerCustomerType));
        query = ApplyDistributorRetailerScope(query, distributorCustomerId);
        query = await ApplyRetailerReportingScopeAsync(query, actorUserId, distributorCustomerId, cancellationToken);
        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<InvoiceSchemeOptionDto>> GetEligibleSchemeOptionsAsync(ulong customerId, DateTime invoiceDate, CancellationToken cancellationToken)
    {
        var date = DateOnly.FromDateTime(invoiceDate.Date);
        var customer = await _dbContext.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId && x.Active == "Y", cancellationToken);
        if (customer is null) return [];

        // Scheme eligibility is based on the invoice's transaction date. A
        // published scheme may be expired today but is still valid for an old
        // invoice whose date falls within the scheme period.
        var schemes = await _dbContext.LoyaltySchemes.AsNoTracking()
            .Where(x => x.DeletedAt == null
                && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live")
                && x.SchemeType == "Invoice"
                && x.StartDate <= date
                && x.EndDate >= date)
            .OrderBy(x => x.SchemeName)
            .ToListAsync(cancellationToken);

        var employeeId = AssignedEmployeeId(customer);
        Branch? branch = null;
        if (employeeId.HasValue)
        {
            var employeeBranch = await _dbContext.Users.AsNoTracking().Where(x => x.Id == employeeId.Value)
                .Select(x => new { x.PrimaryBranchId, x.BranchId }).FirstOrDefaultAsync(cancellationToken);
            var branchId = employeeBranch?.PrimaryBranchId
                ?? (ulong.TryParse(employeeBranch?.BranchId, out var parsedBranchId) ? parsedBranchId : null);
            if (branchId.HasValue) branch = await _dbContext.Branches.AsNoTracking().FirstOrDefaultAsync(x => x.Id == branchId.Value, cancellationToken);
        }
        var zones = await LoadAssignedZoneNamesAsync([customer], cancellationToken);
        var zoneName = AssignedZoneName(customer, zones);
        var stateNames = await LoadStateNamesAsync([customer], cancellationToken);

        var audience = new SchemeAudience(
            customer.CustomerType,
            customer.Name,
            customer.CustomerCode,
            branch?.BranchName,
            zoneName,
            stateNames.GetValueOrDefault(customer.Id));

        return schemes
            .Where(x => SchemeMatches(x, date, audience))
            .Select(x => new InvoiceSchemeOptionDto { Id = x.Id, Name = x.SchemeName, Code = x.SchemeCode, StartDate = x.StartDate, EndDate = x.EndDate })
            .ToList();
    }

    public async Task<IReadOnlyCollection<InvoiceSchemeOptionDto>> GetInvoiceSchemeFilterOptionsAsync(CancellationToken cancellationToken) =>
        await _dbContext.LoyaltySchemes.AsNoTracking()
            .Where(scheme => scheme.DeletedAt == null
                && _dbContext.NewInvoices.Any(invoice => invoice.LoyaltySchemeId == scheme.Id))
            .OrderBy(scheme => scheme.SchemeName)
            .Select(scheme => new InvoiceSchemeOptionDto
            {
                Id = scheme.Id,
                Name = scheme.SchemeName,
                Code = scheme.SchemeCode,
                StartDate = scheme.StartDate,
                EndDate = scheme.EndDate
            })
            .ToListAsync(cancellationToken);

    /// <summary>Invoice numbers only have to be unique inside one dealer's own series -
    /// two dealers numbering their bills 1, 2, 3 is normal. The dealer is resolved from
    /// the retailer; when a retailer has no dealer mapped we fall back to that retailer
    /// alone so a straight resubmission is still caught. Rejected invoices are skipped:
    /// the number is free again so the dealer can re-enter a corrected invoice.</summary>
    public async Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, ulong secondaryCustomerId, ulong? dealerCustomerId, ulong? exceptId, CancellationToken cancellationToken)
    {
        var retailer = await _dbContext.Customers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == secondaryCustomerId, cancellationToken);
        var distributorCustomerId = dealerCustomerId ?? (retailer is null ? null : AssignedDistributorId(retailer));

        var query = distributorCustomerId.HasValue
            ? BaseQuery(distributorCustomerId)
            : BaseQuery(null).Where(x => x.Invoice.SecondaryCustomerId == secondaryCustomerId);

        return await query.AnyAsync(
            x => x.Invoice.InvoiceNumber == invoiceNumber
                && x.Invoice.ApprovalStatus != NewInvoice.StatusRejected
                && (!exceptId.HasValue || x.Invoice.Id != exceptId),
            cancellationToken);
    }

    public Task<int> CountAttachmentsAsync(ulong invoiceId, CancellationToken cancellationToken) =>
        _dbContext.NewInvoiceAttachments.CountAsync(x => x.InvoiceId == invoiceId, cancellationToken);

    public async Task<IReadOnlyCollection<string>> SaveAttachmentsAsync(
        ulong invoiceId,
        IReadOnlyList<InvoiceAttachmentInput> added,
        IReadOnlyList<long> removedIds,
        CancellationToken cancellationToken)
    {
        var removedPaths = new List<string>();

        if (removedIds.Count > 0)
        {
            var doomed = await _dbContext.NewInvoiceAttachments
                .Where(x => x.InvoiceId == invoiceId && removedIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            removedPaths.AddRange(doomed.Select(x => x.FilePath));
            _dbContext.NewInvoiceAttachments.RemoveRange(doomed);
        }

        if (added.Count > 0)
        {
            var nextOrder = await _dbContext.NewInvoiceAttachments
                .Where(x => x.InvoiceId == invoiceId)
                .Select(x => (int?)x.SortOrder)
                .MaxAsync(cancellationToken) ?? -1;

            var now = DateTime.UtcNow;
            foreach (var input in added)
            {
                await _dbContext.NewInvoiceAttachments.AddAsync(new NewInvoiceAttachment
                {
                    InvoiceId = invoiceId,
                    FilePath = input.FilePath,
                    FileName = input.FileName,
                    MimeType = input.MimeType,
                    FileSize = input.FileSize,
                    SortOrder = ++nextOrder,
                    CreatedAt = now,
                    UpdatedAt = now
                }, cancellationToken);
            }
        }

        if (removedIds.Count == 0 && added.Count == 0) return removedPaths;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // The invoice row keeps the first file, so anything still reading that column
        // shows a file that exists rather than one that was just deleted.
        var first = await _dbContext.NewInvoiceAttachments.AsNoTracking()
            .Where(x => x.InvoiceId == invoiceId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.FilePath)
            .FirstOrDefaultAsync(cancellationToken);

        var invoice = await _dbContext.NewInvoices.FirstOrDefaultAsync(x => x.Id == invoiceId, cancellationToken);
        if (invoice is not null && !string.Equals(invoice.Attachment, first, StringComparison.Ordinal))
        {
            invoice.Attachment = first;
            invoice.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return removedPaths;
    }

    public async Task<NewInvoiceDto> CreateInvoiceAsync(NewInvoice invoice, CancellationToken cancellationToken)
    {
        await _dbContext.NewInvoices.AddAsync(invoice, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetInvoiceAsync(invoice.Id, null, cancellationToken) ?? throw new InvalidOperationException("Created invoice could not be loaded.");
    }

    /// <summary>The tracked invoice an action is about to change, resolved only when it is one
    /// the actor is allowed to see. The listing and the detail screen are scoped already, so
    /// without the same check here an id typed straight into an approve, edit or delete call
    /// would still act on an invoice from outside the actor's hierarchy. Out of scope reads as
    /// "not found", the same answer the detail endpoint gives.</summary>
    public async Task<NewInvoice?> FindInvoiceEntityAsync(ulong id, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var distributorCustomerId = await GetDistributorCustomerIdAsync(actorUserId, cancellationToken);
        var scoped = await ApplyReportingScopeAsync(BaseQuery(distributorCustomerId), actorUserId, distributorCustomerId, cancellationToken);
        if (!await scoped.AnyAsync(x => x.Invoice.Id == id, cancellationToken)) return null;

        return await _dbContext.NewInvoices.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<NewInvoiceDto> SaveInvoiceAsync(NewInvoice invoice, string statusType, int? fromStatus, int toStatus, ulong actorUserId, string? remark, decimal? approvedAmount, CancellationToken cancellationToken)
    {
        invoice.UpdatedAt = DateTime.UtcNow;
        await _dbContext.NewInvoiceApprovalLogs.AddAsync(new NewInvoiceApprovalLog
        {
            LogDate = DateTime.UtcNow.Date,
            NewInvoiceId = invoice.Id,
            CreatedBy = actorUserId,
            StatusType = statusType,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ApprovedAmount = approvedAmount,
            Remark = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return await GetInvoiceAsync(invoice.Id, null, cancellationToken) ?? throw new InvalidOperationException("Invoice could not be loaded.");
    }

    public async Task<IReadOnlyCollection<string>> DeleteInvoiceAsync(NewInvoice invoice, CancellationToken cancellationToken)
    {
        var logs = _dbContext.NewInvoiceApprovalLogs.Where(x => x.NewInvoiceId == invoice.Id);
        _dbContext.NewInvoiceApprovalLogs.RemoveRange(logs);

        // Every file the invoice carries, so none is orphaned on disk.
        var ownAttachments = await _dbContext.NewInvoiceAttachments
            .Where(x => x.InvoiceId == invoice.Id)
            .ToListAsync(cancellationToken);
        _dbContext.NewInvoiceAttachments.RemoveRange(ownAttachments);

        // Attachment rows written by the legacy app. The current flow keeps the path
        // on the invoice itself, but older invoices still carry these.
        var attachments = await _dbContext.Media
            .Where(x => x.ModelType == LegacyInvoiceModelType && x.ModelId == invoice.Id)
            .ToListAsync(cancellationToken);
        _dbContext.Media.RemoveRange(attachments);

        _dbContext.NewInvoices.Remove(invoice);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // The retailer's points for this invoice live in the legacy wallet ledger,
        // which has no EF model. Leaving them behind would credit the retailer for an
        // invoice that no longer exists.
        if (!string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
        {
            await _dbContext.Database.ExecuteSqlRawAsync(
                "DELETE FROM wallets WHERE customer_id = {0} AND invoice_no = {1}",
                [Convert.ToDecimal(invoice.SecondaryCustomerId), invoice.InvoiceNumber],
                cancellationToken);
        }

        return attachments
            .Select(x => x.FileName)
            .Concat(ownAttachments.Select(x => x.FilePath))
            .Append(invoice.Attachment ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToArray();
    }

    private const string LegacyInvoiceModelType = "App\\Models\\NewInvoice";

    private IQueryable<InvoiceRow> ApplyFilters(IQueryable<InvoiceRow> query, NewInvoiceFilterDto filter)
    {
        if (filter.SecondaryCustomerIds is not null)
        {
            var customerIds = filter.SecondaryCustomerIds;
            query = customerIds.Count == 0
                ? query.Where(_ => false)
                : query.Where(x => customerIds.Contains(x.Invoice.SecondaryCustomerId));
        }
        if (filter.SchemeId.HasValue) query = query.Where(x => x.Invoice.LoyaltySchemeId == filter.SchemeId.Value);
        if (!string.IsNullOrWhiteSpace(filter.RetailerSearch))
        {
            var search = filter.RetailerSearch.Trim();
            var likeSearch = $"%{search}%";
            query = query.Where(x => x.Customer.Name.Contains(search)
                || (x.Customer.Mobile != null && x.Customer.Mobile.Contains(search))
                || x.Customer.CustomerCode.Contains(search)
                || (x.Customer.CustomFields != null && EF.Functions.Like(x.Customer.CustomFields, likeSearch)));
        }

        if (!string.IsNullOrWhiteSpace(filter.InvoiceNumber))
        {
            var invoiceNumber = filter.InvoiceNumber.Trim();
            query = query.Where(x => x.Invoice.InvoiceNumber.Contains(invoiceNumber));
        }

        if (filter.ApprovalStatus.HasValue) query = query.Where(x => x.Invoice.ApprovalStatus == filter.ApprovalStatus);
        if (filter.ApprovalStatuses is { Count: > 0 })
        {
            var statuses = filter.ApprovalStatuses.ToArray();
            query = query.Where(x => statuses.Contains(x.Invoice.ApprovalStatus));
        }
        if (filter.BranchId.HasValue)
        {
            var employeeIds = _dbContext.Users.AsNoTracking()
                .Where(x => x.PrimaryBranchId == filter.BranchId.Value
                    || (x.BranchId != null && (x.BranchId == filter.BranchId.Value.ToString()
                        || x.BranchId.StartsWith(filter.BranchId.Value + ",")
                        || x.BranchId.EndsWith("," + filter.BranchId.Value)
                        || x.BranchId.Contains("," + filter.BranchId.Value + ","))))
                .Select(x => x.Id);
            query = ApplyAssignedEmployeeFilter(query, employeeIds);
        }
        if (filter.DivisionId.HasValue)
        {
            var employeeIds = _dbContext.Users.AsNoTracking()
                .Where(x => x.DivisionId == filter.DivisionId.Value)
                .Select(x => x.Id);
            query = ApplyAssignedEmployeeFilter(query, employeeIds);
        }
        if (filter.FromDate.HasValue) query = query.Where(x => x.Invoice.InvoiceDate.Date >= filter.FromDate.Value.Date);
        if (filter.ToDate.HasValue) query = query.Where(x => x.Invoice.InvoiceDate.Date <= filter.ToDate.Value.Date);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            var likeSearch = $"%{search}%";
            query = query.Where(x => x.Invoice.InvoiceNumber.Contains(search)
                || x.Invoice.Amount.ToString().Contains(search)
                || x.Invoice.Points.ToString().Contains(search)
                || x.Customer.Name.Contains(search)
                || (x.Customer.Mobile != null && x.Customer.Mobile.Contains(search))
                || x.Customer.CustomerCode.Contains(search)
                || (x.Customer.CustomFields != null && EF.Functions.Like(x.Customer.CustomFields, likeSearch)));
        }

        return query;
    }

    /// <summary>
    /// Narrows the listing to invoices whose retailer is handled by one of these
    /// employees - which is how the zone and branch filters work, since neither is
    /// stored on the invoice.
    ///
    /// The assignment is in customers.custom_fields as JSON. This used to be read with
    /// ten leading-wildcard LIKEs per employee, evaluated per row: on the live data a
    /// single zone filter cost 197 seconds of CPU and the screen timed out at twenty.
    /// The same JSON is now exposed as indexed computed columns, so this is three index
    /// seeks. The result is the same set of customers - the rule is still "the first id
    /// in employee_id, or the first in sales_executive_id, or executive_id when the JSON
    /// names neither" - and it was checked zone by zone against the old predicate before
    /// being changed.
    /// </summary>
    private static IQueryable<InvoiceRow> ApplyAssignedEmployeeFilter(IQueryable<InvoiceRow> query, IQueryable<ulong> employeeIds) =>
        query.Where(x =>
            (x.Customer.AssignedEmployeeId.HasValue && employeeIds.Contains(x.Customer.AssignedEmployeeId.Value))
            || (x.Customer.AssignedSalesExecutiveId.HasValue && employeeIds.Contains(x.Customer.AssignedSalesExecutiveId.Value))
            || (x.Customer.AssignedFallbackEmployeeId.HasValue && employeeIds.Contains(x.Customer.AssignedFallbackEmployeeId.Value)));

    private IQueryable<InvoiceRow> BaseQuery(ulong? distributorCustomerId)
    {
        var query =
            from invoice in _dbContext.NewInvoices.AsNoTracking()
            join customer in _dbContext.Customers.AsNoTracking().Where(x => x.CustomerType == RetailerCustomerType || x.CustomerType == InfluencerCustomerType) on invoice.SecondaryCustomerId equals customer.Id
            join creatorRow in _dbContext.Users.AsNoTracking() on invoice.CreatedBy equals creatorRow.Id into creators
            from creator in creators.DefaultIfEmpty()
            join branchRow in _dbContext.Branches.AsNoTracking() on creator.PrimaryBranchId equals branchRow.Id into branches
            from branch in branches.DefaultIfEmpty()
            select new InvoiceRow
            {
                Invoice = invoice,
                Customer = customer,
                Creator = creator,
                Branch = branch
            };

        return ApplyDistributorInvoiceScope(query, distributorCustomerId);
    }

    /// <summary>Narrows the invoices to the ones the actor is allowed to see. A dealer login
    /// is already pinned to its own retailers by the distributor scope, and an admin-named or
    /// privileged role sees everything; everyone else sees the invoices of the retailers
    /// assigned to themselves and to their reporting descendants, to the end of the chain.</summary>
    private async Task<IQueryable<InvoiceRow>> ApplyReportingScopeAsync(
        IQueryable<InvoiceRow> query,
        ulong? actorUserId,
        ulong? actorDistributorCustomerId,
        CancellationToken cancellationToken)
    {
        if (actorDistributorCustomerId.HasValue) return query;
        if (await ReportingVisibility.HasUnrestrictedDataScopeAsync(_dbContext, actorUserId, cancellationToken)) return query;

        // One invoice page asks for the rows, the totals and the stage counts, so this
        // runs three times per request. The repository is scoped to the request, so the
        // set is resolved once and reused.
        if (_scopedCustomerIds is null || _scopedFor != actorUserId)
        {
            _scopedCustomerIds = await ResolveScopedCustomerIdsAsync(actorUserId, cancellationToken);
            _scopedFor = actorUserId;
        }

        if (_scopedCustomerIds.Count == 0) return query.Where(_ => false);

        var customerIds = _scopedCustomerIds;
        return query.Where(x => customerIds.Contains(x.Invoice.SecondaryCustomerId));
    }

    /// <summary>The same scope, applied to the retailers themselves rather than to their
    /// invoices, so the picker offers only retailers whose invoices the actor would go on to
    /// see - and so an invoice cannot be raised against a retailer outside the hierarchy.</summary>
    private async Task<IQueryable<Customer>> ApplyRetailerReportingScopeAsync(
        IQueryable<Customer> query,
        ulong? actorUserId,
        ulong? actorDistributorCustomerId,
        CancellationToken cancellationToken)
    {
        if (actorDistributorCustomerId.HasValue) return query;
        if (await ReportingVisibility.HasUnrestrictedDataScopeAsync(_dbContext, actorUserId, cancellationToken)) return query;

        if (_scopedCustomerIds is null || _scopedFor != actorUserId)
        {
            _scopedCustomerIds = await ResolveScopedCustomerIdsAsync(actorUserId, cancellationToken);
            _scopedFor = actorUserId;
        }

        if (_scopedCustomerIds.Count == 0) return query.Where(_ => false);

        var customerIds = _scopedCustomerIds;
        return query.Where(x => customerIds.Contains(x.Id));
    }

    private HashSet<ulong>? _scopedCustomerIds;
    private ulong? _scopedFor;

    private async Task<HashSet<ulong>> ResolveScopedCustomerIdsAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        var visibleUserIds = await ReportingVisibility.GetVisibleUserIdsAsync(_dbContext, actorUserId, cancellationToken);
        return visibleUserIds.Count == 0
            ? []
            : await ReportingVisibility.GetVisibleCustomerIdsAsync(_dbContext, visibleUserIds, cancellationToken);
    }

    /// <summary>The dealer a login belongs to, or null for an internal user. A login the CRM
    /// provisions carries the Distributor role, but dealer logins that pre-date that only have
    /// users.customer_id pointing at the dealer; both are dealer logins and both stay pinned to
    /// <summary>Raising an invoice from the field app belongs to the ASR who owns the
    /// retailer relationship. Everyone else - DSR, BM, BDM, dealer logins - can see the
    /// listing but not add to it. A superadmin is exempt, the way they are everywhere else.
    ///
    /// Both the role and the designation are matched on the id, but that id is read from
    /// the database by name rather than written into the code: local and live do not carry
    /// the same numbers, so a hard-coded id would quietly grant or deny the button on one
    /// of them. Every matching row is collected, so a duplicated or renamed-back row still
    /// counts.</summary>
    public async Task<bool> CanCreateFieldInvoiceAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) return false;

        var superAdminRoleIds = await _dbContext.Roles.AsNoTracking()
            .Where(x => x.Name != null && x.Name.Trim().ToUpper() == SuperAdminRoleName)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (superAdminRoleIds.Count > 0)
        {
            var isSuperAdmin = await _dbContext.ModelHasRoles.AsNoTracking()
                .AnyAsync(x => x.ModelId == actorUserId.Value
                    && x.ModelType == LaravelModelTypes.User
                    && superAdminRoleIds.Contains(x.RoleId), cancellationToken);

            if (isSuperAdmin) return true;
        }

        var designationId = await _dbContext.Users.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(x => x.Id == actorUserId.Value)
            .Select(x => x.DesignationId)
            .FirstOrDefaultAsync(cancellationToken);

        if (!designationId.HasValue) return false;

        var asrDesignationIds = await _dbContext.Designations.AsNoTracking()
            .Where(x => x.DesignationName != null && x.DesignationName.Trim().ToUpper() == AsrDesignationName)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        return asrDesignationIds.Contains(designationId.Value);
    }

    /// that dealer's own retailers rather than falling through to a reporting scope they have
    /// no place in.</summary>
    private async Task<ulong?> GetDistributorCustomerIdAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) return null;

        var isDistributor = await _dbContext.ModelHasRoles.AsNoTracking()
            .Where(x => x.ModelId == actorUserId.Value && x.ModelType == LaravelModelTypes.User)
            .Join(_dbContext.Roles.AsNoTracking(), modelRole => modelRole.RoleId, role => role.Id, (_, role) => role.Name)
            .AnyAsync(roleName => roleName == "Distributor", cancellationToken);

        var customerId = await _dbContext.Users.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(x => x.Id == actorUserId.Value)
            .Select(x => x.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (isDistributor) return customerId ?? 0;
        if (!customerId.HasValue) return null;

        var linkedToDealer = await _dbContext.Customers.AsNoTracking()
            .AnyAsync(x => x.Id == customerId.Value && x.CustomerType == DistributorCustomerType, cancellationToken);

        return linkedToDealer ? customerId : null;
    }

    /// <summary>The retailers mapped to one distributor.
    ///
    /// This used to be six leading-wildcard LIKE patterns over the custom_fields JSON -
    /// one for each way the id might have been written - which scans every row's whole
    /// document. On a distributor login that alone was most of a ten second wait for the
    /// retailer picker. JSON_VALUE reads the two fields that actually hold the answer and
    /// handles string and numeric values by itself, which is how the dashboard and the
    /// dealer app have always read it.</summary>
    private static IQueryable<Customer> ApplyDistributorRetailerScope(IQueryable<Customer> query, ulong? distributorCustomerId)
    {
        if (!distributorCustomerId.HasValue) return query;

        var dealerValue = distributorCustomerId.Value.ToString(CultureInfo.InvariantCulture);

        return query.Where(x => x.CustomFields != null
            && (AppDbContext.JsonValue(x.CustomFields, DistributorJsonPath) == dealerValue
                || AppDbContext.JsonValue(x.CustomFields, AgriDistributorJsonPath) == dealerValue));
    }

    /// <summary>The invoices that belong to one dealer.
    ///
    /// An invoice raised from the field app says which dealer it is for, and that answer is
    /// final - a retailer mapped to two dealers must not show the same invoice to both. Every
    /// invoice raised before that question existed carries no dealer, and those still follow
    /// the retailer's mapping, which is how the dealer app has always found them.</summary>
    private static IQueryable<InvoiceRow> ApplyDistributorInvoiceScope(IQueryable<InvoiceRow> query, ulong? distributorCustomerId)
    {
        if (!distributorCustomerId.HasValue) return query;

        var dealerId = distributorCustomerId.Value;
        var dealerValue = dealerId.ToString(CultureInfo.InvariantCulture);

        return query.Where(x => x.Invoice.DealerCustomerId == dealerId
            || (x.Invoice.DealerCustomerId == null
                && x.Customer.CustomFields != null
                && (AppDbContext.JsonValue(x.Customer.CustomFields, DistributorJsonPath) == dealerValue
                    || AppDbContext.JsonValue(x.Customer.CustomFields, AgriDistributorJsonPath) == dealerValue)));
    }


    private async Task<Dictionary<ulong, string>> LoadCitiesAsync(IEnumerable<ulong?> cityIds, CancellationToken cancellationToken)
    {
        var ids = cityIds.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        if (ids.Length == 0) return [];
        return await _dbContext.Cities.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.CityName, cancellationToken);
    }

    /// <summary>
    /// Resolves each customer's state name for State-scoped schemes. The id lives in
    /// the legacy custom_fields JSON; customers migrated before that field existed
    /// only carry it on the addresses row, so both are consulted.
    /// </summary>
    private async Task<Dictionary<ulong, string>> LoadStateNamesAsync(IEnumerable<Customer> customers, CancellationToken cancellationToken)
    {
        var list = customers.GroupBy(x => x.Id).Select(x => x.First()).ToList();
        if (list.Count == 0) return [];

        var stateIds = list.ToDictionary(x => x.Id, SchemeEligibility.ReadStateId);
        var missing = stateIds.Where(x => !x.Value.HasValue).Select(x => x.Key).ToArray();
        if (missing.Length > 0)
        {
            // There is no Address entity in the model, so the legacy addresses table
            // is read directly. Ids come from the database, never from user input.
            var connection = _dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $@"SELECT customer_id, state_id FROM addresses
WHERE deleted_at IS NULL AND state_id IS NOT NULL AND customer_id IN ({string.Join(',', missing)})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var customerId = Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (stateIds.ContainsKey(customerId)) stateIds[customerId] = Convert.ToUInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            }
        }

        var ids = stateIds.Values.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        if (ids.Length == 0) return [];

        var names = await _dbContext.States.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.StateName, cancellationToken);

        return stateIds
            .Where(x => x.Value.HasValue && names.ContainsKey(x.Value!.Value))
            .ToDictionary(x => x.Key, x => names[x.Value!.Value]);
    }

    private async Task<Dictionary<ulong, string>> LoadAssignedZoneNamesAsync(IEnumerable<Customer> customers, CancellationToken cancellationToken)
    {
        var customerEmployeeIds = customers
            .Select(customer => new { CustomerId = customer.Id, EmployeeId = AssignedEmployeeId(customer) })
            .Where(x => x.EmployeeId.HasValue)
            .GroupBy(x => x.CustomerId)
            .Select(x => x.First())
            .ToList();

        var employeeIds = customerEmployeeIds.Select(x => x.EmployeeId!.Value).Distinct().ToArray();
        if (employeeIds.Length == 0) return [];

        var employeeZones = await (from user in _dbContext.Users.AsNoTracking()
                                   where employeeIds.Contains(user.Id)
                                   join divisionRow in _dbContext.Divisions.AsNoTracking() on user.DivisionId equals divisionRow.Id into divisions
                                   from division in divisions.DefaultIfEmpty()
                                   select new { user.Id, ZoneName = division != null ? division.DivisionName : null })
            .Where(x => x.ZoneName != null)
            .ToDictionaryAsync(x => x.Id, x => x.ZoneName!, cancellationToken);

        return customerEmployeeIds
            .Where(x => x.EmployeeId.HasValue && employeeZones.ContainsKey(x.EmployeeId.Value))
            .ToDictionary(x => x.CustomerId, x => employeeZones[x.EmployeeId!.Value]);
    }

    private async Task<Dictionary<ulong, string>> LoadAssignedBranchNamesAsync(IEnumerable<Customer> customers, CancellationToken cancellationToken)
    {
        var customerEmployeeIds = customers
            .Select(customer => new { CustomerId = customer.Id, EmployeeId = AssignedEmployeeId(customer) })
            .Where(x => x.EmployeeId.HasValue)
            .GroupBy(x => x.CustomerId)
            .Select(x => x.First())
            .ToList();

        var employeeIds = customerEmployeeIds.Select(x => x.EmployeeId!.Value).Distinct().ToArray();
        if (employeeIds.Length == 0) return [];

        var employees = await _dbContext.Users.AsNoTracking()
            .Where(x => employeeIds.Contains(x.Id))
            .Select(x => new { x.Id, x.PrimaryBranchId, x.BranchId })
            .ToListAsync(cancellationToken);
        var employeeBranches = employees
            .Select(x => new { x.Id, BranchId = x.PrimaryBranchId ?? FirstULong(x.BranchId) })
            .Where(x => x.BranchId.HasValue)
            .ToList();
        var branchIds = employeeBranches.Select(x => x.BranchId!.Value).Distinct().ToArray();
        var branchNames = await _dbContext.Branches.AsNoTracking()
            .Where(x => branchIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
        var byEmployee = employeeBranches
            .Where(x => branchNames.ContainsKey(x.BranchId!.Value))
            .ToDictionary(x => x.Id, x => branchNames[x.BranchId!.Value]);

        return customerEmployeeIds
            .Where(x => x.EmployeeId.HasValue && byEmployee.ContainsKey(x.EmployeeId.Value))
            .ToDictionary(x => x.CustomerId, x => byEmployee[x.EmployeeId!.Value]);
    }

    private async Task<Dictionary<ulong, AssignedEmployee>> LoadAssignedEmployeeNamesAsync(IEnumerable<Customer> customers, CancellationToken cancellationToken)
    {
        var customerEmployeeIds = customers
            .Select(customer => new { CustomerId = customer.Id, EmployeeId = AssignedEmployeeId(customer) })
            .Where(x => x.EmployeeId.HasValue)
            .GroupBy(x => x.CustomerId)
            .Select(x => x.First())
            .ToList();

        var employeeIds = customerEmployeeIds.Select(x => x.EmployeeId!.Value).Distinct().ToArray();
        if (employeeIds.Length == 0) return [];

        var employees = await _dbContext.Users.AsNoTracking()
            .Where(x => employeeIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.Mobile })
            .ToDictionaryAsync(x => x.Id, x => new AssignedEmployee(x.Name, x.Mobile), cancellationToken);

        return customerEmployeeIds
            .Where(x => x.EmployeeId.HasValue && employees.ContainsKey(x.EmployeeId.Value))
            .ToDictionary(x => x.CustomerId, x => employees[x.EmployeeId!.Value]);
    }

    /// <summary>Turns the creating user id into something readable. A dealer login is
    /// labelled by the firm it belongs to with the owner in brackets, because that is
    /// how the business refers to them; everyone else is just their own name.</summary>
    private async Task<Dictionary<ulong, string>> LoadCreatorLabelsAsync(IEnumerable<User?> creators, CancellationToken cancellationToken)
    {
        var users = creators.Where(x => x is not null).Select(x => x!).GroupBy(x => x.Id).Select(x => x.First()).ToList();
        if (users.Count == 0) return [];

        var customerIds = users.Where(x => x.CustomerId.HasValue && x.CustomerId.Value > 0)
            .Select(x => x.CustomerId!.Value).Distinct().ToArray();
        var customers = customerIds.Length == 0
            ? []
            : await _dbContext.Customers.AsNoTracking()
                .IgnoreQueryFilters()
                .Where(x => customerIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x, cancellationToken);

        return users.ToDictionary(user => user.Id, user =>
        {
            if (user.CustomerId.HasValue && customers.TryGetValue(user.CustomerId.Value, out var customer))
            {
                var firm = customer.CustomerType == DistributorCustomerType ? DealerFirmName(customer) : ShopName(customer);
                var owner = customer.CustomerType == DistributorCustomerType ? DealerOwnerName(customer) : OwnerName(customer);
                return string.IsNullOrWhiteSpace(owner) || string.Equals(firm, owner, StringComparison.OrdinalIgnoreCase)
                    ? firm
                    : $"{firm} ({owner})";
            }

            return string.IsNullOrWhiteSpace(user.Name) ? $"User {user.Id}" : user.Name;
        });
    }

    /// <summary>Fills in every file on each invoice, in one query for the whole page
    /// rather than one per invoice. An invoice raised before attachments moved into
    /// their own table still has its single path on the invoice row, so that is used
    /// when the table holds nothing for it.</summary>
    private async Task ApplyAttachmentsAsync(IReadOnlyCollection<NewInvoiceDto> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        var ids = items.Select(x => x.Id).Distinct().ToArray();
        var rows = await _dbContext.NewInvoiceAttachments.AsNoTracking()
            .Where(x => ids.Contains(x.InvoiceId))
            .OrderBy(x => x.InvoiceId).ThenBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new { x.InvoiceId, x.Id, x.FilePath, x.FileName, x.MimeType, x.FileSize })
            .ToListAsync(cancellationToken);

        var byInvoice = rows.GroupBy(x => x.InvoiceId).ToDictionary(
            group => group.Key,
            group => group.Select(x => new InvoiceAttachmentDto
            {
                Id = x.Id,
                FilePath = x.FilePath,
                FileName = x.FileName,
                MimeType = x.MimeType,
                FileSize = x.FileSize
            }).ToList());

        foreach (var item in items)
        {
            if (byInvoice.TryGetValue(item.Id, out var attachments) && attachments.Count > 0)
            {
                item.Attachments = attachments;
                item.Attachment ??= attachments[0].FilePath;
                continue;
            }

            item.Attachments = string.IsNullOrWhiteSpace(item.Attachment)
                ? []
                : [new InvoiceAttachmentDto { Id = 0, FilePath = item.Attachment!, FileName = Path.GetFileName(item.Attachment!) }];
        }
    }

    /// <summary>
    /// Puts a name against each approval stage. The invoice stores only the user id, and
    /// the three stages are usually three different people, so they are collected across
    /// every row and looked up once rather than per invoice.
    /// </summary>
    private async Task ApplyApproverNamesAsync(IReadOnlyCollection<NewInvoiceDto> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        var ids = items
            .SelectMany(item => new[] { item.SsApprovedBy, item.SalesApprovedBy, item.HoApprovedBy })
            .Where(id => id.HasValue && id.Value > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (ids.Length == 0) return;

        var names = await _dbContext.Users.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        string? nameFor(ulong? id) => id.HasValue ? names.GetValueOrDefault(id.Value) : null;
        foreach (var item in items)
        {
            item.SsApprovedByName = nameFor(item.SsApprovedBy);
            item.SalesApprovedByName = nameFor(item.SalesApprovedBy);
            item.HoApprovedByName = nameFor(item.HoApprovedBy);
        }
    }

    private async Task ApplyCreatedByLabelsAsync(IReadOnlyCollection<NewInvoiceDto> items, IEnumerable<User?> creators, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        var labels = await LoadCreatorLabelsAsync(creators, cancellationToken);
        foreach (var item in items)
        {
            item.CreatedByLabel = labels.GetValueOrDefault(item.CreatedBy) ?? item.CreatedByName;
        }
    }

    private async Task<Dictionary<ulong, string>> LoadDealerNamesAsync(IEnumerable<InvoiceRow> rows, CancellationToken cancellationToken)
    {
        var dealerIds = rows
            .Select(row => InvoiceDealerId(row.Invoice, row.Customer))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (dealerIds.Length == 0) return [];

        return await _dbContext.Customers.AsNoTracking()
            .Where(x => dealerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
    }

    private async Task<IReadOnlyCollection<NewInvoiceApprovalLogDto>> GetApprovalLogsAsync(ulong invoiceId, CancellationToken cancellationToken) =>
        await (from log in _dbContext.NewInvoiceApprovalLogs.AsNoTracking()
              where log.NewInvoiceId == invoiceId
              join userRow in _dbContext.Users.AsNoTracking() on log.CreatedBy equals userRow.Id into users
              from user in users.DefaultIfEmpty()
              orderby log.CreatedAt descending, log.Id descending
              select new NewInvoiceApprovalLogDto
              {
                  Id = log.Id,
                  LogDate = log.LogDate,
                  CreatedBy = log.CreatedBy,
                  CreatedByName = user != null ? user.Name : null,
                  EmployeeCode = user != null ? user.EmployeeCodes : null,
                  StatusType = log.StatusType ?? string.Empty,
                  FromStatus = log.FromStatus,
                  ToStatus = log.ToStatus,
                  ApprovedAmount = log.ApprovedAmount,
                  Remark = log.Remark,
                  CreatedAt = log.CreatedAt
              }).ToListAsync(cancellationToken);

    private async Task<Dictionary<ulong, ApprovalStageSummary>> LoadApprovalStageSummariesAsync(IEnumerable<ulong> invoiceIds, CancellationToken cancellationToken)
    {
        var ids = invoiceIds.Distinct().ToArray();
        if (ids.Length == 0) return [];

        var logs = await _dbContext.NewInvoiceApprovalLogs.AsNoTracking()
            .Where(x => x.NewInvoiceId.HasValue
                && ids.Contains(x.NewInvoiceId.Value)
                && x.ToStatus.HasValue
                // A hold carries a remark and no amount, so it cannot be filtered on
                // ApprovedAmount the way the three approvals are.
                && ((x.ApprovedAmount.HasValue
                        && (x.ToStatus == NewInvoice.StatusApprovedSs
                            || x.ToStatus == NewInvoice.StatusApprovedSales
                            || x.ToStatus == NewInvoice.StatusApprovedHo))
                    || x.ToStatus == NewInvoice.StatusHold))
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new
            {
                InvoiceId = x.NewInvoiceId!.Value,
                ToStatus = x.ToStatus!.Value,
                x.ApprovedAmount,
                x.Remark
            })
            .ToListAsync(cancellationToken);

        var summaries = ids.ToDictionary(id => id, _ => new ApprovalStageSummary());
        foreach (var log in logs)
        {
            var summary = summaries[log.InvoiceId];
            if (log.ToStatus == NewInvoice.StatusApprovedSs && !summary.SsApprovedAmount.HasValue)
            {
                summary.SsApprovedAmount = log.ApprovedAmount;
                summary.SsApprovalRemark = log.Remark;
            }
            if (log.ToStatus == NewInvoice.StatusApprovedSales && !summary.SalesApprovedAmount.HasValue)
            {
                summary.SalesApprovedAmount = log.ApprovedAmount;
                summary.SalesApprovalRemark = log.Remark;
            }
            if (log.ToStatus == NewInvoice.StatusApprovedHo && !summary.HoApprovedAmount.HasValue)
            {
                summary.HoApprovedAmount = log.ApprovedAmount;
                summary.HoApprovalRemark = log.Remark;
            }
            // Blank counts as no remark, so a hold submitted without a reason does not
            // hide the reason given on an earlier one.
            if (log.ToStatus == NewInvoice.StatusHold && string.IsNullOrWhiteSpace(summary.HoldRemark))
            {
                summary.HoldRemark = string.IsNullOrWhiteSpace(log.Remark) ? null : log.Remark;
            }
        }

        return summaries;
    }

    private async Task<IReadOnlyCollection<LoyaltyScheme>> LoadSchemesAsync(IEnumerable<DateTime> invoiceDates, CancellationToken cancellationToken)
    {
        var dates = invoiceDates.Select(x => DateOnly.FromDateTime(x.Date)).ToArray();
        if (dates.Length == 0) return [];
        var minDate = dates.Min();
        var maxDate = dates.Max();

        return await _dbContext.LoyaltySchemes.AsNoTracking()
            .Include(x => x.Slabs)
            .Where(x => x.DeletedAt == null
                && x.Active == "Y"
                && (x.Status == "Published" || x.Status == "Live")
                && x.SchemeType == "Invoice"
                && x.StartDate <= maxDate
                && x.EndDate >= minDate)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyCollection<SchemeInvoiceAmount>> LoadSchemeInvoicesAsync(IEnumerable<ulong> customerIds, IReadOnlyCollection<LoyaltyScheme> schemes, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToArray();
        if (ids.Length == 0 || schemes.Count == 0) return [];

        var minDate = schemes.Min(x => x.StartDate).ToDateTime(TimeOnly.MinValue);
        var maxDate = schemes.Max(x => x.EndDate).ToDateTime(TimeOnly.MaxValue);

        // Everything that is not rejected is loaded. Earned points still count only
        // HO-approved turnover, but the expected preview has to see the other invoices
        // still in approval, otherwise each one lands on its own low slab and the
        // preview disagrees with what is actually paid once HO approves them together.
        var invoices = await _dbContext.NewInvoices.AsNoTracking()
            .Where(x => ids.Contains(x.SecondaryCustomerId)
                && x.ApprovalStatus != NewInvoice.StatusRejected
                && x.InvoiceDate >= minDate
                && x.InvoiceDate <= maxDate)
            .Select(x => new SchemeInvoiceAmount(x.Id, x.SecondaryCustomerId, x.LoyaltySchemeId, x.InvoiceDate, x.Amount,
                x.ApprovalStatus == NewInvoice.StatusApprovedHo))
            .ToListAsync(cancellationToken);

        var invoiceIds = invoices.Where(x => x.IsHoApproved).Select(x => x.InvoiceId).ToArray();
        var hoApprovalAmounts = await _dbContext.NewInvoiceApprovalLogs.AsNoTracking()
            .Where(x => x.NewInvoiceId.HasValue
                && invoiceIds.Contains(x.NewInvoiceId.Value)
                && x.ToStatus == NewInvoice.StatusApprovedHo)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new { InvoiceId = x.NewInvoiceId!.Value, x.ApprovedAmount })
            .ToListAsync(cancellationToken);
        var latestAmounts = hoApprovalAmounts
            .GroupBy(x => x.InvoiceId)
            .ToDictionary(x => x.Key, x => x.First().ApprovedAmount);

        return invoices
            .Select(x => x with { Amount = latestAmounts.GetValueOrDefault(x.InvoiceId) ?? x.Amount })
            .ToList();
    }

    private static ApprovalStageSummary ApprovalSummary(ulong invoiceId, IReadOnlyDictionary<ulong, ApprovalStageSummary> approvals) =>
        approvals.TryGetValue(invoiceId, out var summary) ? summary : new ApprovalStageSummary();

    private static IReadOnlyCollection<NewInvoiceDto> ToSchemeDtos(NewInvoice invoice, Customer customer, string? cityName, string? zoneName, string? branchName, string? assignedDistributorName, string? assignedEmployeeName, string? assignedEmployeeMobile, User? creator, Branch? branch, IReadOnlyCollection<LoyaltyScheme> schemes, IReadOnlyCollection<SchemeInvoiceAmount> schemeInvoices, ApprovalStageSummary approvalSummary)
    {
        var invoiceDate = DateOnly.FromDateTime(invoice.InvoiceDate.Date);
        var selectedScheme = invoice.LoyaltySchemeId.HasValue
            ? schemes.FirstOrDefault(scheme =>
                scheme.Id == invoice.LoyaltySchemeId.Value
                && invoiceDate >= scheme.StartDate
                && invoiceDate <= scheme.EndDate)
            : null;
        if (selectedScheme is null)
            return [ToDto(invoice, customer, cityName, zoneName, branchName, assignedDistributorName, assignedEmployeeName, assignedEmployeeMobile, creator, branch, null, null, approvalSummary)];

        // An HO-approved invoice is paid on finalized turnover only. An invoice still in
        // approval is previewed against every non-rejected invoice of the period, so the
        // preview shows the same slab the whole batch will settle on after HO approval.
        var awaitingApproval = invoice.ApprovalStatus != NewInvoice.StatusApprovedHo
            && invoice.ApprovalStatus != NewInvoice.StatusRejected;
        var periodAmount = PeriodAmount(invoice, selectedScheme, schemeInvoices, awaitingApproval);
        var rewardBaseAmount = invoice.ApprovalStatus == NewInvoice.StatusApprovedHo
            ? approvalSummary.HoApprovedAmount ?? invoice.Amount
            : invoice.Amount;
        return [ToDto(invoice, customer, cityName, zoneName, branchName, assignedDistributorName, assignedEmployeeName, assignedEmployeeMobile, creator, branch, selectedScheme, CalculateSchemeResult(rewardBaseAmount, periodAmount, selectedScheme), approvalSummary)];
    }

    private static NewInvoiceDto ToDto(NewInvoice invoice, Customer customer, string? cityName, string? zoneName, string? branchName, string? assignedDistributorName, string? assignedEmployeeName, string? assignedEmployeeMobile, User? creator, Branch? branch, LoyaltyScheme? scheme, SchemeResult? schemeResult, ApprovalStageSummary approvalSummary)
    {
        var pointsCreated = invoice.ApprovalStatus == NewInvoice.StatusApprovedHo;
        var schemePoints = pointsCreated ? schemeResult?.Points ?? 0 : 0;
        var isBooster = string.Equals(scheme?.SchemeTag, "Booster", StringComparison.OrdinalIgnoreCase);
        return new NewInvoiceDto
        {
            Id = invoice.Id,
            SecondaryCustomerId = invoice.SecondaryCustomerId,
            RetailerCode = $"RET-{invoice.SecondaryCustomerId.ToString().PadLeft(4, '0')}",
            CustomerName = OwnerName(customer),
            ShopName = ShopName(customer),
            MobileNumber = MobileNumber(customer),
            CityName = cityName,
            ZoneName = zoneName,
            BranchName = branchName,
            AssignedDistributorId = InvoiceDealerId(invoice, customer),
            AssignedDistributorName = assignedDistributorName,
            AssignedEmployeeName = assignedEmployeeName,
            AssignedEmployeeMobile = assignedEmployeeMobile,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            Amount = invoice.Amount,
            Points = schemePoints,
            SchemeId = scheme?.Id,
            SchemeName = scheme?.SchemeName,
            SchemeCode = scheme?.SchemeCode,
            SchemeNote = scheme?.SchemeNote,
            SchemeTag = scheme?.SchemeTag,
            SchemeBasedOn = scheme?.BasedOn,
            SchemeRewardValue = schemeResult?.RewardValue,
            SchemePoints = schemePoints,
            ExpectedSchemePoints = schemeResult?.Points ?? 0,
            TierName = schemeResult?.TierName,
            SchemeHintMessage = schemeResult?.HintMessage,
            RegularWalletPoints = scheme is not null && !isBooster ? schemePoints : 0,
            BoosterWalletPoints = isBooster ? schemePoints : 0,
            Attachment = invoice.Attachment,
            ApprovalStatus = invoice.ApprovalStatus,
            ApprovalStatusLabel = StatusLabel(invoice.ApprovalStatus),
            ApprovalRemark = invoice.ApprovalRemark,
            SsApprovedAmount = approvalSummary.SsApprovedAmount,
            SsApprovalRemark = approvalSummary.SsApprovalRemark,
            SsApprovedBy = invoice.ApprovedSsBy,
            SsApprovedAt = invoice.ApprovedSsAt,
            SalesApprovedAmount = approvalSummary.SalesApprovedAmount,
            SalesApprovalRemark = approvalSummary.SalesApprovalRemark,
            SalesApprovedBy = invoice.ApprovedSalesBy,
            SalesApprovedAt = invoice.ApprovedSalesAt,
            HoApprovedAmount = approvalSummary.HoApprovedAmount,
            HoApprovalRemark = approvalSummary.HoApprovalRemark,
            HoApprovedBy = invoice.ApprovedHoBy,
            HoApprovedAt = invoice.ApprovedHoAt,
            HoldRemark = approvalSummary.HoldRemark,
            CreatedBy = invoice.CreatedBy,
            CreatedByName = creator?.Name,
            CreatedAt = invoice.CreatedAt,
            UpdatedAt = invoice.UpdatedAt
        };
    }

    // Scheme targeting lives in Domain.Services.SchemeEligibility so that the web
    // invoice screen, the mobile apps and the customer point totals all decide
    // eligibility with exactly the same rules.
    private static bool SchemeMatches(LoyaltyScheme scheme, DateOnly invoiceDate, SchemeAudience audience) =>
        SchemeEligibility.Matches(scheme, invoiceDate, audience);

    private static decimal PeriodAmount(NewInvoice invoice, LoyaltyScheme scheme,
        IReadOnlyCollection<SchemeInvoiceAmount> schemeInvoices, bool includeAwaitingApproval)
    {
        var startDate = scheme.StartDate.ToDateTime(TimeOnly.MinValue);
        var endDate = scheme.EndDate.ToDateTime(TimeOnly.MaxValue);
        return schemeInvoices
            .Where(x => x.CustomerId == invoice.SecondaryCustomerId
                && x.SchemeId == scheme.Id
                && (includeAwaitingApproval || x.IsHoApproved)
                && x.InvoiceDate >= startDate
                && x.InvoiceDate <= endDate)
            .Sum(x => x.Amount);
    }

    private static SchemeResult CalculateSchemeResult(decimal invoiceAmount, decimal cumulativeAmount, LoyaltyScheme scheme)
    {
        var slabs = scheme.Slabs
            .Where(x => x.DeletedAt == null)
            .OrderBy(x => x.ValueFrom)
            .ThenBy(x => x.SortOrder)
            .ToList();

        var achieved = slabs.LastOrDefault(slab => cumulativeAmount >= slab.ValueFrom && (!slab.ValueTo.HasValue || cumulativeAmount <= slab.ValueTo.Value));
        if (achieved is not null)
        {
            var points = SchemeReward.PointsFor(invoiceAmount, cumulativeAmount, scheme, achieved);

            return new SchemeResult(points, achieved.RewardValue, achieved.TierName, null);
        }

        var next = slabs.FirstOrDefault(slab => cumulativeAmount < slab.ValueFrom);
        if (next is null) return new SchemeResult(0, null, null, null);

        var remaining = next.ValueFrom - cumulativeAmount;
        var rewardText = SchemeReward.Label(scheme, next);
        return new SchemeResult(0, null, null, $"Add Rs. {remaining:0.##} more to get {rewardText}");
    }

    private static IReadOnlyCollection<string> ReadSchemeAreaValues(string? json) => SchemeEligibility.ReadAreaValues(json);


    private static string OwnerName(Customer customer)
    {
        var personName = string.Join(" ", new[] { customer.FirstName, customer.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return ReadField(customer, "owner_name") ?? (!string.IsNullOrWhiteSpace(personName) ? personName : customer.Name);
    }

    /// <summary>A dealer's firm name. The CRM form captures Trade / Business Name and
    /// Dealer Legal Name; either may be left blank, so whichever is filled is used and
    /// the same value shows wherever the dealer appears.</summary>
    private static string DealerFirmName(Customer customer) =>
        ReadField(customer, "trade_name")
        ?? ReadField(customer, "legal_name")
        ?? ReadField(customer, "shop_name")
        ?? customer.Name;

    /// <summary>A dealer's person is the Primary Contact Person on that same form.</summary>
    private static string DealerOwnerName(Customer customer)
    {
        var personName = string.Join(" ", new[] { customer.FirstName, customer.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return ReadField(customer, "contact_person")
            ?? ReadField(customer, "owner_name")
            ?? (!string.IsNullOrWhiteSpace(personName) ? personName : customer.Name);
    }

    private static string ShopName(Customer customer) => ReadField(customer, "shop_name") ?? customer.Name;

    private static string? Address(Customer customer) =>
        ReadField(customer, "address_line")
        ?? ReadField(customer, "address1")
        ?? ReadField(customer, "billing_address")
        ?? ReadField(customer, "shipping_address");

    private static string MobileNumber(Customer customer)
    {
        var mobileNumbers = ReadField(customer, "mobile_numbers");
        var firstMobile = mobileNumbers?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return firstMobile ?? customer.Mobile ?? string.Empty;
    }

    private static ulong? CityId(Customer customer) =>
        ulong.TryParse(ReadField(customer, "city_id"), out var cityId) ? cityId : null;

    private static ulong? AssignedEmployeeId(Customer customer) =>
        FirstULong(ReadField(customer, "employee_id")) ?? FirstULong(ReadField(customer, "sales_executive_id")) ?? customer.ExecutiveId;

    private static ulong? AssignedDistributorId(Customer customer) =>
        FirstULong(ReadField(customer, "distributor_name")) ?? FirstULong(ReadField(customer, "agri_distributor")) ?? customer.ParentId;

    /// <summary>Whose invoice this is. A retailer can be mapped to more than one dealer, so
    /// whoever raises the invoice says which one and that answer wins. Invoices raised before
    /// the question existed carry no dealer and fall back to the retailer's mapping.</summary>
    private static ulong? InvoiceDealerId(NewInvoice invoice, Customer customer) =>
        invoice.DealerCustomerId ?? AssignedDistributorId(customer);

    private static string? AssignedZoneName(Customer customer, IReadOnlyDictionary<ulong, string> zones) =>
        zones.TryGetValue(customer.Id, out var zoneName) ? zoneName : null;

    private static string? AssignedBranchName(Customer customer, IReadOnlyDictionary<ulong, string> branches) =>
        branches.TryGetValue(customer.Id, out var branchName) ? branchName : null;

    /// <summary>The employee a customer is assigned to, with their mobile number, so
    /// whoever is reviewing the invoice can call them without leaving the screen.</summary>
    private sealed record AssignedEmployee(string? Name, string? Mobile);

    private static string? AssignedEmployeeName(Customer customer, IReadOnlyDictionary<ulong, AssignedEmployee> employees) =>
        employees.TryGetValue(customer.Id, out var employee) ? employee.Name : null;

    private static string? AssignedEmployeeMobile(Customer customer, IReadOnlyDictionary<ulong, AssignedEmployee> employees) =>
        employees.TryGetValue(customer.Id, out var employee) ? employee.Mobile : null;

    private static string? DealerName(NewInvoice invoice, Customer customer, IReadOnlyDictionary<ulong, string> dealerNames)
    {
        var dealerId = InvoiceDealerId(invoice, customer);
        return dealerId.HasValue && dealerNames.TryGetValue(dealerId.Value, out var name) ? name : null;
    }

    private static ulong? FirstULong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var first = value.Trim();
        if (first.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var values = JsonSerializer.Deserialize<List<ulong>>(first, JsonOptions);
                return values?.FirstOrDefault(x => x > 0);
            }
            catch
            {
                return null;
            }
        }

        first = first.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? first;
        return ulong.TryParse(first, out var parsed) ? parsed : null;
    }

    private static string? CityName(Customer customer, IReadOnlyDictionary<ulong, string> cities)
    {
        var cityId = CityId(customer);
        return cityId.HasValue && cities.TryGetValue(cityId.Value, out var cityName) ? cityName : null;
    }

    private static string? ReadField(Customer customer, string key)
    {
        if (string.IsNullOrWhiteSpace(customer.CustomFields)) return null;
        try
        {
            using var document = JsonDocument.Parse(customer.CustomFields);
            if (!document.RootElement.TryGetProperty(key, out var value)) return null;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static string StatusLabel(int status) => status switch
    {
        NewInvoice.StatusHold => "Hold",
        NewInvoice.StatusApprovedSs => "Approved By SS",
        NewInvoice.StatusApprovedSales => "Approved By Sales",
        NewInvoice.StatusApprovedHo => "Approved By HO",
        NewInvoice.StatusRejected => "Rejected",
        _ => "Pending"
    };

    private sealed class InvoiceRow
    {
        public NewInvoice Invoice { get; init; } = null!;
        public Customer Customer { get; init; } = null!;
        public User? Creator { get; init; }
        public Branch? Branch { get; init; }
    }

    private sealed record SchemeInvoiceAmount(ulong InvoiceId, ulong CustomerId, ulong? SchemeId, DateTime InvoiceDate, decimal Amount, bool IsHoApproved);

    private sealed record SchemeResult(decimal Points, decimal? RewardValue, string? TierName, string? HintMessage);

    private sealed class ApprovalStageSummary
    {
        public decimal? SsApprovedAmount { get; set; }
        public string? SsApprovalRemark { get; set; }
        public decimal? SalesApprovedAmount { get; set; }
        public string? SalesApprovalRemark { get; set; }
        public decimal? HoApprovedAmount { get; set; }
        public string? HoApprovalRemark { get; set; }
        public string? HoldRemark { get; set; }
    }
}
