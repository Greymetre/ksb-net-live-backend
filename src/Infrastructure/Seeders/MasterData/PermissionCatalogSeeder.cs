using Domain.Constants;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Seeders.MasterData;

/// <summary>Brings the permissions table in line with <see cref="PermissionCatalog"/>.
///
/// The table carried 725 rows inherited from the legacy system, of which only a third
/// gated anything and many covered several actions at once. This seeder renames the ones
/// that survive - keeping their id, so every role keeps what it was granted - fills in the
/// catalog metadata the role matrix renders from, grants the newly split actions to the
/// roles that could already perform them, and deletes everything the CRM does not enforce.
///
/// It is idempotent: a second run finds every row already in place and changes nothing.</summary>
public static class PermissionCatalogSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var rows = await db.Permissions.ToListAsync(cancellationToken);

        // What each role can do today, by permission name, before anything is renamed.
        var grantedNamesByRole = await GrantedNamesByRoleAsync(db, cancellationToken);

        var rowsByName = new Dictionary<string, Permission>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) rowsByName.TryAdd(row.Name, row);

        var keepers = new Dictionary<string, Permission>(StringComparer.OrdinalIgnoreCase);
        var mergedAway = new List<(Permission Row, Permission Keeper)>();
        // Permissions this run creates for the first time. They are the only ones the
        // inheritance below back-fills, so an admin who later unticks one keeps it unticked.
        var createdNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in PermissionCatalog.All)
        {
            var candidates = new List<Permission>();
            if (rowsByName.TryGetValue(definition.Name, out var current)) candidates.Add(current);
            foreach (var legacy in definition.LegacyNames)
            {
                if (rowsByName.TryGetValue(legacy, out var legacyRow) && !candidates.Contains(legacyRow))
                {
                    candidates.Add(legacyRow);
                }
            }

            if (candidates.Count == 0)
            {
                var created = new Permission { Name = definition.Name, GuardName = "users", CreatedAt = now };
                await db.Permissions.AddAsync(created, cancellationToken);
                keepers[definition.Name] = created;
                createdNames.Add(definition.Name);
                continue;
            }

            var keeper = candidates[0];
            keepers[definition.Name] = keeper;
            for (var index = 1; index < candidates.Count; index++) mergedAway.Add((candidates[index], keeper));
        }

        // Rows the CRM no longer enforces, plus the extra copies of a merged permission.
        var keptIds = keepers.Values.Where(x => x.Id > 0).Select(x => x.Id).ToHashSet();
        var removableIds = rows.Where(row => !keptIds.Contains(row.Id)).Select(row => row.Id).ToHashSet();

        // A merged permission's role assignments move to the row that survives, so a role
        // that held only the extra copy keeps the ability.
        foreach (var (row, keeper) in mergedAway.Where(pair => pair.Keeper.Id > 0))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO role_has_permissions (permission_id, role_id)
                SELECT {0}, source.role_id FROM role_has_permissions source
                WHERE source.permission_id = {1}
                  AND NOT EXISTS (SELECT 1 FROM role_has_permissions target
                                  WHERE target.permission_id = {0} AND target.role_id = source.role_id)
                """,
                [keeper.Id, row.Id], cancellationToken);
        }

        // Permissions are granted through roles only. Any per-user grant left by the legacy
        // system is removed, so what a user may do is always what their roles allow.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM model_has_permissions", cancellationToken);

        if (removableIds.Count > 0)
        {
            var ids = removableIds.ToArray();
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM role_has_permissions WHERE permission_id IN (SELECT value FROM STRING_SPLIT({0}, ','))",
                [string.Join(',', ids)], cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM permissions WHERE id IN (SELECT value FROM STRING_SPLIT({0}, ','))",
                [string.Join(',', ids)], cancellationToken);

            foreach (var row in rows.Where(row => removableIds.Contains(row.Id)).ToList())
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }

        // Renaming happens only after the duplicates are gone, so the unique index on
        // (name, guard_name) never sees two rows claiming the same name.
        foreach (var definition in PermissionCatalog.All)
        {
            var row = keepers[definition.Name];
            row.Name = definition.Name;
            row.GuardName = "users";
            row.Label = definition.Label;
            row.GroupKey = definition.GroupKey;
            row.GroupLabel = definition.GroupLabel;
            row.ModuleKey = definition.ModuleKey;
            row.ModuleLabel = definition.ModuleLabel;
            row.ActionKey = definition.ActionKey;
            row.SortOrder = definition.SortOrder;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        await GrantSplitActionsAsync(db, keepers, grantedNamesByRole, createdNames, cancellationToken);
        await GrantEverythingToSuperAdminAsync(db, keepers, cancellationToken);

        // Roles were split across the 'web' and 'users' guards while the permissions were
        // all on 'users'. Nothing reads the guard, and two roles of the same name could
        // exist on different guards, so they are brought onto one.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE roles SET guard_name = 'users' WHERE guard_name <> 'users'", cancellationToken);
    }

    private static async Task<Dictionary<ulong, HashSet<string>>> GrantedNamesByRoleAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var pairs = await db.RoleHasPermissions.AsNoTracking()
            .Join(db.Permissions.AsNoTracking(),
                rolePermission => rolePermission.PermissionId,
                permission => permission.Id,
                (rolePermission, permission) => new { rolePermission.RoleId, permission.Name })
            .ToListAsync(cancellationToken);

        return pairs
            .GroupBy(pair => pair.RoleId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>An action that used to share one permission with the rest of its module - every
    /// holiday action sat behind holiday_access - now has its own. Roles that held the shared
    /// permission receive the new ones, so the split takes nothing away.</summary>
    private static async Task GrantSplitActionsAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, Permission> keepers,
        IReadOnlyDictionary<ulong, HashSet<string>> grantedNamesByRole,
        IReadOnlySet<string> createdNames,
        CancellationToken cancellationToken)
    {
        var existing = await db.RoleHasPermissions.AsNoTracking()
            .Select(x => new { x.RoleId, x.PermissionId })
            .ToListAsync(cancellationToken);
        var existingPairs = existing.Select(x => (x.RoleId, x.PermissionId)).ToHashSet();

        var additions = new List<RoleHasPermission>();
        foreach (var definition in PermissionCatalog.All)
        {
            // Back-fill only where this run created the permission, or where a legacy name it
            // replaces is still on a role - both happen once, on the run that introduces it.
            var replacesLegacy = definition.LegacyNames
                .Any(legacy => grantedNamesByRole.Values.Any(names => names.Contains(legacy)));
            if (!createdNames.Contains(definition.Name) && !replacesLegacy) continue;

            var sources = definition.LegacyNames.Concat(definition.InheritFrom).ToArray();
            if (sources.Length == 0) continue;

            var permissionId = keepers[definition.Name].Id;
            foreach (var (roleId, names) in grantedNamesByRole)
            {
                if (!sources.Any(names.Contains)) continue;
                if (!existingPairs.Add((roleId, permissionId))) continue;
                additions.Add(new RoleHasPermission { RoleId = roleId, PermissionId = permissionId });
            }
        }

        if (additions.Count == 0) return;
        await db.RoleHasPermissions.AddRangeAsync(additions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task GrantEverythingToSuperAdminAsync(
        AppDbContext db,
        IReadOnlyDictionary<string, Permission> keepers,
        CancellationToken cancellationToken)
    {
        var superAdminIds = await db.Roles.AsNoTracking()
            .Where(role => role.Name == "superadmin")
            .Select(role => role.Id)
            .ToListAsync(cancellationToken);
        if (superAdminIds.Count == 0) return;

        var existing = await db.RoleHasPermissions.AsNoTracking()
            .Where(x => superAdminIds.Contains(x.RoleId))
            .Select(x => new { x.RoleId, x.PermissionId })
            .ToListAsync(cancellationToken);
        var existingPairs = existing.Select(x => (x.RoleId, x.PermissionId)).ToHashSet();

        var additions = (from roleId in superAdminIds
                         from permission in keepers.Values
                         where existingPairs.Add((roleId, permission.Id))
                         select new RoleHasPermission { RoleId = roleId, PermissionId = permission.Id }).ToList();

        if (additions.Count == 0) return;
        await db.RoleHasPermissions.AddRangeAsync(additions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
