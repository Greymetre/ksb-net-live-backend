using Api.Controllers;
using Application.Interfaces.Repositories;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Whether a field-app (SFA) user may open one retailer - the same rule the field app's retailer
/// listing applies, so any retailer the KYC tab lists can be opened and nothing else can:
/// admin-named roles (and HR_Admin / HO_Account) see every retailer, a BM the retailers of its
/// branch's users, everyone else the retailers assigned to their reporting downline to the end level.
/// </summary>
public sealed class SfaRetailerScope
{
    private static readonly string[] AllAccessRoles = ["superadmin", "Admin", "Sub_Admin", "subAdmin", "HR_Admin", "HO_Account"];

    private readonly AppDbContext _db;
    private readonly IHrRepository _hrRepository;

    public SfaRetailerScope(AppDbContext db, IHrRepository hrRepository)
    {
        _db = db;
        _hrRepository = hrRepository;
    }

    public async Task<bool> CanAccessAsync(ulong userId, ulong retailerId, CancellationToken cancellationToken)
    {
        var roleNames = await _db.ModelHasRoles.AsNoTracking()
            .Where(x => x.ModelId == userId)
            .Join(_db.Roles.AsNoTracking(), x => x.RoleId, role => role.Id, (_, role) => role.Name)
            .ToListAsync(cancellationToken);

        // A Distributor login is always assignment-scoped, even with an admin-named role beside it.
        var isDistributor = roleNames.Any(name => string.Equals(name, "Distributor", StringComparison.OrdinalIgnoreCase));
        var allAccess = !isDistributor && roleNames.Any(name =>
            name.Contains("admin", StringComparison.OrdinalIgnoreCase)
            || AllAccessRoles.Contains(name, StringComparer.OrdinalIgnoreCase));

        var scope = "1 = 1";
        if (!allAccess)
        {
            var visible = await _hrRepository.GetVisibleUserIdsAsync(userId, cancellationToken);
            scope = FieldKonnectCustomerRestController.AssignedCustomerPredicate(visible);
        }

        var count = await _db.Database.SqlQueryRaw<int>($@"SELECT COUNT(*) AS Value
FROM customers c
LEFT JOIN customer_types ctype ON ctype.id = c.customertype AND ctype.deleted_at IS NULL
WHERE c.id = {{0}}
AND c.deleted_at IS NULL
AND c.active = 'Y'
AND {FieldKonnectCustomerRestController.RetailerTypeSql}
AND {scope}", retailerId).FirstAsync(cancellationToken);
        return count > 0;
    }
}
