using System.Text.Json;
using Domain.Entities;
using Domain.Services;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Json;

namespace Api.Services;

/// <summary>
/// Who a dealer counts as when a loyalty scheme is matched.
///
/// A dealer sees a scheme aimed at its own account, and one aimed at any retailer
/// assigned to it - a retailer scheme is worth showing to the dealer who will be
/// raising those invoices. Each of those customers carries its own audience, and a
/// scheme is kept when any one of them qualifies.
///
/// Zone and branch come from the customer's assigned employee, not from anything
/// stored on the customer, because that assignment is what actually decides which
/// part of the country the account belongs to.
///
/// This lives in one place because it did not: the field app filtered its dealer
/// scheme list this way while the CRM dealer dashboard listed every published
/// scheme with no audience check at all, so a North dealer was shown a scheme
/// written for West-zone retailers.
/// </summary>
public sealed class SchemeAudienceService
{
    private readonly AppDbContext _db;

    public SchemeAudienceService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<SchemeAudience>> ForDealerAsync(
        Customer dealer,
        IReadOnlyCollection<Customer> assignedRetailers,
        CancellationToken cancellationToken)
    {
        var customers = new List<Customer> { dealer };
        customers.AddRange(assignedRetailers);

        var employeeIds = customers
            .Select(EmployeeIdFor)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        var employees = employeeIds.Length == 0
            ? []
            : await _db.Users.AsNoTracking()
                .Where(x => employeeIds.Contains(x.Id))
                .Select(x => new { x.Id, x.PrimaryBranchId, x.BranchId, x.DivisionId })
                .ToListAsync(cancellationToken);

        var branchIds = employees
            .Select(x => x.PrimaryBranchId ?? FirstAssignedId(x.BranchId))
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var divisionIds = employees.Where(x => x.DivisionId.HasValue)
            .Select(x => x.DivisionId!.Value).Distinct().ToArray();
        var stateIds = customers.Select(SchemeEligibility.ReadStateId)
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();

        var branches = branchIds.Length == 0 ? [] : await _db.Branches.AsNoTracking()
            .Where(x => branchIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
        var divisions = divisionIds.Length == 0 ? [] : await _db.Divisions.AsNoTracking()
            .Where(x => divisionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DivisionName, cancellationToken);
        var states = stateIds.Length == 0 ? [] : await _db.States.AsNoTracking()
            .Where(x => stateIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.StateName, cancellationToken);
        var employeeById = employees.ToDictionary(x => x.Id);

        return customers.Select(customer =>
        {
            string? branchName = null;
            string? zoneName = null;
            var employeeId = EmployeeIdFor(customer);
            if (employeeId.HasValue && employeeById.TryGetValue(employeeId.Value, out var employee))
            {
                var branchId = employee.PrimaryBranchId ?? FirstAssignedId(employee.BranchId);
                if (branchId.HasValue) branchName = branches.GetValueOrDefault(branchId.Value);
                if (employee.DivisionId.HasValue) zoneName = divisions.GetValueOrDefault(employee.DivisionId.Value);
            }

            var stateId = SchemeEligibility.ReadStateId(customer);
            var stateName = stateId.HasValue ? states.GetValueOrDefault(stateId.Value) : null;
            return new SchemeAudience(customer.CustomerType, customer.Name, customer.CustomerCode, branchName, zoneName, stateName);
        }).ToList();
    }

    private static ulong? EmployeeIdFor(Customer customer)
    {
        var fields = CustomFieldsJson.Read(customer.CustomFields);
        return FirstAssignedId(Value(fields, "employee_id"))
            ?? FirstAssignedId(Value(fields, "sales_executive_id"))
            ?? customer.ExecutiveId;
    }

    private static string? Value(IReadOnlyDictionary<string, string?> fields, string key) =>
        fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>The assignment columns hold a list - "42081,42095,42083" or a JSON
    /// array - and the first entry is the one that owns the account.</summary>
    private static ulong? FirstAssignedId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var first = value.Trim().Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return ulong.TryParse(first?.Trim('"'), out var parsed) && parsed > 0 ? parsed : null;
    }
}
