using System.Text.Json;
using Application.DTOs.Redemptions;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Json;

namespace Infrastructure.Repositories;

public sealed class RedemptionRepository : IRedemptionRepository
{
    private const int MaxRows = 50000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _dbContext;

    public RedemptionRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>The customer a dealer login belongs to, or null for an internal user.</summary>
    private async Task<ulong?> DealerCustomerIdAsync(ulong? actorUserId, CancellationToken cancellationToken)
    {
        if (!actorUserId.HasValue) return null;

        var isDealer = await ReportingVisibility.IsDistributorUserAsync(_dbContext, actorUserId, cancellationToken);
        if (!isDealer) return null;

        return await _dbContext.Users.AsNoTracking()
            .Where(user => user.Id == actorUserId.Value)
            .Select(user => user.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<RedemptionDto>> GetRedemptionsAsync(RedemptionFilterDto filter, CancellationToken cancellationToken)
    {
        var query =
            from redemption in _dbContext.LoyaltyRedemptions.AsNoTracking()
            where redemption.DeletedAt == null
            join customerRow in _dbContext.Customers.AsNoTracking() on redemption.CustomerId equals customerRow.Id into customers
            from customer in customers.DefaultIfEmpty()
            join userRow in _dbContext.Users.AsNoTracking() on redemption.CreatedBy equals userRow.Id into users
            from user in users.DefaultIfEmpty()
            orderby redemption.CreatedAt descending, redemption.Id descending
            select new { Redemption = redemption, Customer = customer, CreatedByName = user != null ? user.Name : null };

        // A dealer's CRM login is linked to its customer row. It sees its own redemptions
        // only - the listing used to return every customer's to anybody who could open it.
        var dealerCustomerId = await DealerCustomerIdAsync(filter.ActorUserId, cancellationToken);
        if (dealerCustomerId.HasValue) query = query.Where(x => x.Redemption.CustomerId == dealerCustomerId.Value);

        if (filter.CustomerId.HasValue) query = query.Where(x => x.Redemption.CustomerId == filter.CustomerId.Value);
        if (filter.Status.HasValue) query = query.Where(x => x.Redemption.Status == filter.Status.Value);
        if (!string.IsNullOrWhiteSpace(filter.RedeemMode))
        {
            var mode = filter.RedeemMode.Trim();
            query = query.Where(x => x.Redemption.RedeemMode == mode);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(x => x.Redemption.TransactionNo.Contains(search)
                || x.Redemption.Points.ToString().Contains(search)
                || (x.Customer != null && (x.Customer.Name.Contains(search)
                    || x.Customer.CustomerCode.Contains(search)
                    || (x.Customer.Mobile != null && x.Customer.Mobile.Contains(search))
                    || (x.Customer.CustomFields != null && x.Customer.CustomFields.Contains(search)))));
        }

        var rows = await query.Take(MaxRows).ToListAsync(cancellationToken);
        var customerList = rows.Select(x => x.Customer).Where(x => x is not null).Select(x => x!).ToList();
        var cities = await LoadCitiesAsync(rows.Select(x => CityId(x.Customer)), cancellationToken);
        var distributors = await LoadDistributorsAsync(customerList, cancellationToken);

        // Each customer's zone and branch come from the employee it is assigned to - custom_fields
        // employee_id, then sales_executive_id, then executive_id - the resolver the rest of the app uses.
        var employeeByCustomer = customerList
            .GroupBy(c => c.Id).Select(g => g.First())
            .ToDictionary(c => c.Id, c => FirstULong(ReadField(c, "employee_id")) ?? FirstULong(ReadField(c, "sales_executive_id")) ?? c.ExecutiveId);
        var employeeIds = employeeByCustomer.Values.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var employees = employeeIds.Length == 0
            ? new Dictionary<ulong, (ulong? DivisionId, ulong? PrimaryBranchId, string? BranchId)>()
            : (await _dbContext.Users.AsNoTracking().Where(x => employeeIds.Contains(x.Id))
                .Select(x => new { x.Id, x.DivisionId, x.PrimaryBranchId, x.BranchId }).ToListAsync(cancellationToken))
                .ToDictionary(x => x.Id, x => (x.DivisionId, x.PrimaryBranchId, x.BranchId));
        var divisionIds = employees.Values.Where(x => x.DivisionId.HasValue).Select(x => x.DivisionId!.Value).Distinct().ToArray();
        var divisions = divisionIds.Length == 0
            ? new Dictionary<ulong, string>()
            : await _dbContext.Divisions.AsNoTracking().Where(x => divisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DivisionName, cancellationToken);
        var branchIdSet = employees.Values.SelectMany(x => BranchIds(x.PrimaryBranchId, x.BranchId)).Distinct().ToArray();
        var branches = branchIdSet.Length == 0
            ? new Dictionary<ulong, string>()
            : await _dbContext.Branches.AsNoTracking().Where(x => branchIdSet.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);

        string ZoneOf(Customer? c) =>
            c is not null && employeeByCustomer.TryGetValue(c.Id, out var eid) && eid.HasValue
            && employees.TryGetValue(eid.Value, out var emp) && emp.DivisionId is { } d && divisions.TryGetValue(d, out var zn) ? zn : string.Empty;
        string BranchOf(Customer? c)
        {
            if (c is null || !employeeByCustomer.TryGetValue(c.Id, out var eid) || !eid.HasValue || !employees.TryGetValue(eid.Value, out var emp)) return string.Empty;
            foreach (var bid in BranchIds(emp.PrimaryBranchId, emp.BranchId))
                if (branches.TryGetValue(bid, out var bn)) return bn;
            return string.Empty;
        }

        return rows.Select(x => ToDto(
            x.Redemption,
            x.Customer,
            CityName(x.Customer, cities),
            x.Customer != null && distributors.TryGetValue(x.Customer.Id, out var dist) ? dist.Name : null,
            x.Customer != null && distributors.TryGetValue(x.Customer.Id, out var dc) ? dc.Code : null,
            ZoneOf(x.Customer),
            BranchOf(x.Customer),
            x.CreatedByName)).ToList();
    }

    public async Task<IReadOnlyCollection<LoyaltyRedemption>> GetCustomerRedemptionsAsync(ulong customerId, CancellationToken cancellationToken) =>
        await _dbContext.LoyaltyRedemptions.AsNoTracking()
            .Where(x => x.DeletedAt == null
                && x.CustomerId == customerId
                && x.Status != LoyaltyRedemption.StatusRejected)
            .ToListAsync(cancellationToken);

    public async Task<LoyaltyRedemption?> GetByIdAsync(ulong id, CancellationToken cancellationToken) =>
        await _dbContext.LoyaltyRedemptions.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, cancellationToken);

    public async Task<IReadOnlyDictionary<string, LoyaltyRedemption>> GetByTransactionNosAsync(IReadOnlyCollection<string> transactionNos, CancellationToken cancellationToken)
    {
        if (transactionNos.Count == 0) return new Dictionary<string, LoyaltyRedemption>();
        // Tracked (no AsNoTracking) so the import can mutate and save them.
        var rows = await _dbContext.LoyaltyRedemptions
            .Where(x => x.DeletedAt == null && transactionNos.Contains(x.TransactionNo))
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(x => x.TransactionNo)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _dbContext.SaveChangesAsync(cancellationToken);

    public async Task<LoyaltyRedemption> CreateAsync(LoyaltyRedemption redemption, CancellationToken cancellationToken)
    {
        await _dbContext.LoyaltyRedemptions.AddAsync(redemption, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return redemption;
    }

    public async Task UpdateAsync(LoyaltyRedemption redemption, CancellationToken cancellationToken)
    {
        _dbContext.LoyaltyRedemptions.Update(redemption);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> TransactionNoExistsAsync(string transactionNo, CancellationToken cancellationToken) =>
        _dbContext.LoyaltyRedemptions.AnyAsync(x => x.TransactionNo == transactionNo, cancellationToken);

    public async Task<IReadOnlyDictionary<string, (ulong Id, string Tag)>> GetSchemesByCodeAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        var wanted = codes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray();
        if (wanted.Length == 0) return new Dictionary<string, (ulong, string)>(StringComparer.OrdinalIgnoreCase);
        var schemes = await _dbContext.LoyaltySchemes.AsNoTracking()
            .Where(x => x.DeletedAt == null && wanted.Contains(x.SchemeCode))
            .Select(x => new { x.Id, x.SchemeCode, x.SchemeTag })
            .ToListAsync(cancellationToken);
        return schemes
            .GroupBy(x => x.SchemeCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().Id, g.First().SchemeTag ?? "Regular"), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<ulong, (string Name, bool RedemptionEnabled, bool RedemptionCrmEnabled)>> GetSchemeFlagsAsync(IReadOnlyCollection<ulong> schemeIds, CancellationToken cancellationToken)
    {
        var ids = schemeIds.Where(x => x > 0).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<ulong, (string, bool, bool)>();
        return (await _dbContext.LoyaltySchemes.AsNoTracking()
                .Where(x => ids.Contains(x.Id))
                .Select(x => new { x.Id, x.SchemeName, x.RedemptionEnabled, x.RedemptionCrmEnabled })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => (x.SchemeName ?? "Scheme", x.RedemptionEnabled, x.RedemptionCrmEnabled));
    }

    private async Task<Dictionary<ulong, string>> LoadCitiesAsync(IEnumerable<ulong?> cityIds, CancellationToken cancellationToken)
    {
        var ids = cityIds.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        return ids.Length == 0
            ? []
            : await _dbContext.Cities.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.CityName, cancellationToken);
    }

    private async Task<Dictionary<ulong, (string Name, string Code)>> LoadDistributorsAsync(IEnumerable<Customer> customers, CancellationToken cancellationToken)
    {
        var customerDistributorIds = customers
            .Select(customer => new { CustomerId = customer.Id, DistributorId = FirstULong(ReadField(customer, "distributor_name")) ?? FirstULong(ReadField(customer, "agri_distributor")) ?? customer.ParentId })
            .Where(x => x.DistributorId.HasValue)
            .GroupBy(x => x.CustomerId)
            .Select(x => x.First())
            .ToList();

        var distributorIds = customerDistributorIds.Select(x => x.DistributorId!.Value).Distinct().ToArray();
        if (distributorIds.Length == 0) return [];

        var byId = (await _dbContext.Customers.AsNoTracking()
            .Where(x => distributorIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.CustomerCode })
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => (Name: x.Name ?? string.Empty, Code: x.CustomerCode ?? string.Empty));

        return customerDistributorIds
            .Where(x => x.DistributorId.HasValue && byId.ContainsKey(x.DistributorId.Value))
            .ToDictionary(x => x.CustomerId, x => byId[x.DistributorId!.Value]);
    }

    // Branch ids a user carries: the primary first, then the comma-separated BranchId list.
    private static IEnumerable<ulong> BranchIds(ulong? primaryBranchId, string? branchId)
    {
        if (primaryBranchId.HasValue) yield return primaryBranchId.Value;
        foreach (var part in (branchId ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (ulong.TryParse(part, out var id)) yield return id;
    }

    private static RedemptionDto ToDto(LoyaltyRedemption redemption, Customer? customer, string? cityName, string? distributorName, string? distributorCode, string zone, string branch, string? createdByName) => new()
    {
        Id = redemption.Id,
        TransactionNo = redemption.TransactionNo,
        CustomerId = redemption.CustomerId,
        CustomerCode = CustomerCode(customer, redemption.CustomerId),
        CustomerName = customer is null ? string.Empty : OwnerName(customer),
        CustomerTypeName = CustomerTypeName(customer?.CustomerType),
        MobileNumber = customer is null ? null : MobileNumber(customer),
        CityName = cityName,
        DistributorName = distributorName,
        Zone = zone,
        Branch = branch,
        DealerCode = distributorCode ?? string.Empty,
        ShopName = customer is null ? string.Empty : (ReadField(customer, "shop_name") ?? customer.Name ?? string.Empty),
        // GST Treatment ON means the customer is NOT GST registered (stored "Yes"); then the GST No
        // reads "Unregistered" whatever number was typed - a wrong number may have been entered and
        // approved, so the toggle, not the field, decides.
        GstNo = customer is null ? string.Empty
            : string.Equals(ReadField(customer, "gst_treatment")?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase) ? "Unregistered"
            : (ReadField(customer, "gst_number") ?? ReadField(customer, "gstin_no") ?? string.Empty),
        AadhaarNumber = customer is null ? string.Empty : (ReadField(customer, "aadhar_no") ?? ReadField(customer, "aadhaar_no") ?? ReadField(customer, "aadhaar_number") ?? ReadField(customer, "aadhar_number") ?? string.Empty),
        PanNumber = customer is null ? string.Empty : (ReadField(customer, "pan_number") ?? ReadField(customer, "pan_no") ?? string.Empty),
        BankAccountType = customer is null ? string.Empty : (ReadField(customer, "bank_account_type") ?? string.Empty),
        ApprovedAt = redemption.ApprovedAt,
        LoyaltySchemeId = redemption.LoyaltySchemeId,
        SchemeName = redemption.SchemeName,
        WalletType = redemption.WalletType,
        RedeemMode = redemption.RedeemMode,
        Points = redemption.Points,
        AccountHolder = redemption.AccountHolder,
        AccountNumber = redemption.AccountNumber,
        BankName = redemption.BankName,
        IfscCode = redemption.IfscCode,
        Status = redemption.Status,
        StatusLabel = StatusLabel(redemption.Status),
        Remark = redemption.Remark,
        PayoutTransactionNo = redemption.PayoutTransactionNo,
        PayoutAmount = redemption.PayoutAmount,
        TdsPercent = redemption.TdsPercent,
        PaymentDate = redemption.PaymentDate,
        CreatedBy = redemption.CreatedBy,
        CreatedByName = createdByName,
        CreatedAt = redemption.CreatedAt
    };

    private static string StatusLabel(int status) => status switch
    {
        LoyaltyRedemption.StatusApproved => "Approved",
        LoyaltyRedemption.StatusRejected => "Rejected",
        LoyaltyRedemption.StatusHold => "Hold",
        LoyaltyRedemption.StatusSuccess => "Success",
        LoyaltyRedemption.StatusFailed => "Failed",
        _ => "Pending"
    };

    private static string CustomerTypeName(ulong? type) => type switch
    {
        1 => "Dealer",
        2 => "Retailer",
        3 => "Influencer",
        null => string.Empty,
        _ => $"Type {type}"
    };

    private static string CustomerCode(Customer? customer, ulong customerId)
    {
        if (!string.IsNullOrWhiteSpace(customer?.CustomerCode)) return customer.CustomerCode;
        return $"{CustomerPrefix(customer?.CustomerType)}-{customerId.ToString().PadLeft(4, '0')}";
    }

    private static string CustomerPrefix(ulong? customerType) => customerType switch
    {
        1 => "DIS",
        2 => "RET",
        3 => "INF",
        _ => "CUS"
    };

    private static string OwnerName(Customer customer) => ReadField(customer, "owner_name") ?? customer.Name;

    private static string MobileNumber(Customer customer) =>
        ReadField(customer, "mobile_numbers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
        ?? customer.Mobile
        ?? string.Empty;

    private static ulong? CityId(Customer? customer) =>
        customer is not null && ulong.TryParse(ReadField(customer, "city_id"), out var cityId) ? cityId : null;

    private static string? CityName(Customer? customer, IReadOnlyDictionary<ulong, string> cities)
    {
        var cityId = CityId(customer);
        return cityId.HasValue && cities.TryGetValue(cityId.Value, out var cityName) ? cityName : null;
    }


    private static string? ReadField(Customer customer, string key)
    {
        var fields = CustomFieldsJson.Read(customer.CustomFields);
        return fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
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
}
