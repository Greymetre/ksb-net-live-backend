using System.Data;
using System.Text.Json;
using Domain.Constants;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

internal static class ReportingVisibility
{
    private const string DistributorRoleName = "Distributor";
    private const int MaxRows = 50000;

    private static readonly HashSet<string> PrivilegedReportingRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "superadmin", "Admin", "ZONAL", "subAdmin", "GM.", "CRM", "HR_Admin", "HO_Account",
        "Sub_Support", "Accounts Order", "Service Admin", "All Customers", "Sub billing",
        "Sales Admin", "Marketing_Admin", "MIS_ADMIN", "Data_Crm"
    };

    public static IQueryable<User> InternalUsersQuery(AppDbContext db, IQueryable<User> query) =>
        query.Where(user =>
            !user.CustomerId.HasValue
            && !db.ModelHasRoles
                .Join(db.Roles, modelRole => modelRole.RoleId, role => role.Id, (modelRole, role) => new { modelRole, role })
                .Any(x => x.modelRole.ModelId == user.Id
                    && x.modelRole.ModelType == LaravelModelTypes.User
                    && (x.role.Name == DistributorRoleName || x.modelRole.RoleId == RoleIds.Customer)));

    public static Task<bool> IsDistributorUserAsync(AppDbContext db, ulong? userId, CancellationToken cancellationToken) =>
        userId.HasValue
            ? db.ModelHasRoles.AsNoTracking()
                .Where(modelRole => modelRole.ModelId == userId.Value && modelRole.ModelType == LaravelModelTypes.User)
                .Join(db.Roles.AsNoTracking(), modelRole => modelRole.RoleId, role => role.Id, (_, role) => role.Name)
                .AnyAsync(roleName => roleName == DistributorRoleName, cancellationToken)
            : Task.FromResult(false);

    public static async Task<IReadOnlyCollection<ulong>> GetVisibleUserIdsAsync(AppDbContext db, ulong? actorUserId, CancellationToken cancellationToken)
    {
        var internalUsers = await InternalUsersQuery(db, db.Users.AsNoTracking())
            .Where(x => x.Active == "Y" && !x.IsDeleted)
            .Select(x => new { x.Id, x.ReportingId, x.BranchId, x.DesignationId })
            .Take(MaxRows)
            .ToListAsync(cancellationToken);

        if (!actorUserId.HasValue)
        {
            return internalUsers.Select(x => x.Id).ToArray();
        }

        var actor = await db.Users.AsNoTracking()
            .Where(x => x.Id == actorUserId.Value)
            .Select(x => new { x.Id, x.BranchId, x.CustomerId, x.Mobile, x.Email })
            .FirstOrDefaultAsync(cancellationToken);

        if (actor is null)
        {
            return [];
        }

        var roles = await db.ModelHasRoles.AsNoTracking()
            .Where(modelRole => modelRole.ModelId == actorUserId.Value && modelRole.ModelType == LaravelModelTypes.User)
            .Join(db.Roles.AsNoTracking(), modelRole => modelRole.RoleId, role => role.Id, (modelRole, role) => new { modelRole.RoleId, role.Name })
            .ToListAsync(cancellationToken);

        // Distributor/dealer CRM users are linked to a customer. Their user-data
        // scope is the ASR/DSR users assigned to that dealer, not the reporting
        // descendants of the generated login user. Keep legacy linkage fallbacks
        // because older live records may pre-date users.customer_id.
        if (roles.Any(role => string.Equals(role.Name, DistributorRoleName, StringComparison.OrdinalIgnoreCase)))
        {
            var dealerCustomerId = actor.CustomerId ?? await ResolveLegacyDealerCustomerIdAsync(
                db,
                actor.Mobile,
                actor.Email,
                cancellationToken);

            if (!dealerCustomerId.HasValue)
            {
                return [];
            }

            var assignedIds = await GetDealerAssignedUserIdsAsync(db, dealerCustomerId.Value, cancellationToken);
            if (assignedIds.Count == 0)
            {
                return [];
            }

            var asrDsrDesignationIds = await db.Designations.AsNoTracking()
                .Where(x => x.Active == "Y"
                    && (x.DesignationName.Trim().ToUpper() == "ASR" || x.DesignationName.Trim().ToUpper() == "DSR"))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            return internalUsers
                .Where(user => assignedIds.Contains(user.Id)
                    && (user.DesignationId == 3
                        || user.DesignationId == 6
                        || user.DesignationId.HasValue && asrDsrDesignationIds.Contains(user.DesignationId.Value)))
                .Select(user => user.Id)
                .Distinct()
                .ToArray();
        }

        // Module permissions are enforced before this data-scope check. For any
        // permitted module, an admin-named role can see every internal user's data.
        if (roles.Any(role => IsAdminRole(role.Name)) || roles.Any(role => PrivilegedReportingRoles.Contains(role.Name)))
        {
            return internalUsers.Select(x => x.Id).ToArray();
        }

        if (roles.Any(role => role.RoleId == RoleIds.BranchManager))
        {
            var actorBranches = SplitCsv(actor.BranchId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (actorBranches.Count == 0) return [actor.Id];

            return internalUsers
                .Where(user => SplitCsv(user.BranchId).Any(actorBranches.Contains))
                .Select(user => user.Id)
                .Distinct()
                .ToArray();
        }

        var visible = new HashSet<ulong> { actor.Id };
        var frontier = new HashSet<ulong> { actor.Id };
        while (frontier.Count > 0)
        {
            var children = internalUsers
                .Where(user => user.ReportingId.HasValue && frontier.Contains(user.ReportingId.Value) && visible.Add(user.Id))
                .Select(user => user.Id)
                .ToArray();

            frontier = children.ToHashSet();
        }

        return visible.ToArray();
    }

    private static string[] SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsAdminRole(string? roleName) =>
        roleName?.Contains("admin", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<ulong?> ResolveLegacyDealerCustomerIdAsync(
        AppDbContext db,
        string? mobile,
        string? email,
        CancellationToken cancellationToken)
    {
        var normalizedMobile = NormalizeMobile(mobile);
        var normalizedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedMobile) && string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return null;
        }

        return await db.Customers.AsNoTracking()
            .Where(customer => customer.DeletedAt == null
                && ((!string.IsNullOrWhiteSpace(normalizedMobile) && customer.Mobile != null && customer.Mobile.EndsWith(normalizedMobile))
                    || (!string.IsNullOrWhiteSpace(normalizedEmail) && customer.Email != null && customer.Email == normalizedEmail)))
            .OrderByDescending(customer => customer.Id)
            .Select(customer => (ulong?)customer.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<HashSet<ulong>> GetDealerAssignedUserIdsAsync(
        AppDbContext db,
        ulong customerId,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<ulong>();
        var customer = await db.Customers.AsNoTracking()
            .Where(x => x.Id == customerId && x.DeletedAt == null)
            .Select(x => new { x.ExecutiveId, x.CustomFields })
            .FirstOrDefaultAsync(cancellationToken);

        if (customer?.ExecutiveId is > 0)
        {
            result.Add(customer.ExecutiveId.Value);
        }

        foreach (var userId in ReadLegacyAssignedUserIds(customer?.CustomFields))
        {
            result.Add(userId);
        }

        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"SELECT DISTINCT user_id
FROM employee_details
WHERE customer_id = @customer_id
  AND customer_id IS NOT NULL
  AND user_id IS NOT NULL
  AND deleted_at IS NULL
  AND (active = 'Y' OR active IS NULL)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@customer_id";
            parameter.Value = Convert.ToDecimal(customerId);
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var userId = Convert.ToUInt64(reader.GetValue(0));
                if (userId > 0) result.Add(userId);
            }
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }

        return result;
    }

    private static IEnumerable<ulong> ReadLegacyAssignedUserIds(string? customFields)
    {
        if (string.IsNullOrWhiteSpace(customFields)) yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(customFields);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) yield break;
            foreach (var key in new[] { "employee_id", "sales_executive_id" })
            {
                if (!document.RootElement.TryGetProperty(key, out var value)) continue;
                foreach (var userId in ReadIds(value)) yield return userId;
            }
        }
    }

    private static IEnumerable<ulong> ReadIds(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                foreach (var userId in ReadIds(item)) yield return userId;
            }
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var numericId) && numericId > 0)
        {
            yield return numericId;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.String) yield break;
        foreach (var part in (value.GetString() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ulong.TryParse(part, out var userId) && userId > 0) yield return userId;
        }
    }

    private static string? NormalizeMobile(string? mobile)
    {
        if (string.IsNullOrWhiteSpace(mobile)) return null;
        var digits = new string(mobile.Where(char.IsDigit).ToArray());
        if (digits.Length > 10) digits = digits[^10..];
        return digits;
    }
}
