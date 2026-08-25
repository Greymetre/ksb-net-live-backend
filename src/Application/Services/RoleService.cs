using Application.Common;
using Application.DTOs.Roles;
using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using Shared.Exceptions;
using Shared.Responses;

namespace Application.Services;

public sealed class RoleService : IRoleService
{
    private readonly IRoleRepository _repository;

    public RoleService(IRoleRepository repository)
    {
        _repository = repository;
    }

    public async Task<LaravelApiResponse> GetRolesAsync(string? search, bool includePermissions, ulong? actorUserId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (rows, total) = await _repository.GetRolesAsync(search, includePermissions, actorUserId, page, pageSize, cancellationToken);
        var response = LaravelApiResponse.Success("roles", rows);
        response.Extra["total"] = total;
        response.Extra["page"] = page;
        response.Extra["page_size"] = pageSize;
        return response;
    }

    public async Task<LaravelApiResponse> GetRoleAsync(ulong id, CancellationToken cancellationToken)
    {
        var role = await _repository.GetRoleAsync(id, cancellationToken);
        return LaravelApiResponse.Success("role", role ?? throw NotFound("Role not found"));
    }

    public async Task<LaravelApiResponse> CreateRoleAsync(RoleRequestDto request, CancellationToken cancellationToken)
    {
        var guardName = NormalizeGuard(request.GuardName);
        RequireName(request.Name);
        if (IsSuperAdmin(request.Name)) throw SuperAdminIsFixed();
        if (await _repository.RoleNameExistsAsync(request.Name!.Trim(), guardName, null, cancellationToken))
        {
            throw new LaravelHttpException(LaravelStatusCodes.BadRequest, "The role name has already been taken.");
        }

        request.GuardName = guardName;
        var role = await _repository.CreateRoleAsync(request, cancellationToken);
        return LaravelApiResponse.Success("role", role, "Role created successfully");
    }

    public async Task<LaravelApiResponse> UpdateRoleAsync(ulong id, RoleRequestDto request, CancellationToken cancellationToken)
    {
        var current = await _repository.GetRoleAsync(id, cancellationToken);
        if (current is null) throw NotFound("Role not found");
        if (IsSuperAdmin(current.Name)) throw SuperAdminIsFixed();

        var guardName = NormalizeGuard(request.GuardName ?? current.GuardName);
        if (!string.IsNullOrWhiteSpace(request.Name)
            && await _repository.RoleNameExistsAsync(request.Name.Trim(), guardName, id, cancellationToken))
        {
            throw new LaravelHttpException(LaravelStatusCodes.BadRequest, "The role name has already been taken.");
        }

        request.GuardName = guardName;
        var role = await _repository.UpdateRoleAsync(id, request, cancellationToken);
        return LaravelApiResponse.Success("role", role ?? throw NotFound("Role not found"), "Role updated successfully");
    }

    public async Task<LaravelApiResponse> DeleteRoleAsync(ulong id, CancellationToken cancellationToken)
    {
        var role = await _repository.GetRoleAsync(id, cancellationToken) ?? throw NotFound("Role not found");

        // superadmin is the role the permission check falls back on; deleting it locks
        // everyone out of the screens it is the only holder of.
        if (string.Equals(role.Name, "superadmin", StringComparison.OrdinalIgnoreCase))
        {
            throw new LaravelHttpException(LaravelStatusCodes.BadRequest, "The superadmin role cannot be deleted.");
        }

        // Deleting a role used to remove its user assignments silently, so everybody on it
        // lost their access with nothing on screen to say why.
        var userCount = await _repository.RoleUserCountAsync(id, cancellationToken);
        if (userCount > 0)
        {
            throw new LaravelHttpException(
                LaravelStatusCodes.BadRequest,
                $"{userCount} user{(userCount == 1 ? " is" : "s are")} assigned to this role. Move them to another role first.");
        }

        if (!await _repository.DeleteRoleAsync(id, cancellationToken)) throw NotFound("Role not found");
        return LaravelApiResponse.MessageOnly("success", "Role deleted successfully!");
    }

    public async Task<LaravelApiResponse> SyncRolePermissionsAsync(ulong id, IReadOnlyCollection<ulong> permissionIds, CancellationToken cancellationToken)
    {
        var role = await _repository.GetRoleAsync(id, cancellationToken) ?? throw NotFound("Role not found");
        if (IsSuperAdmin(role.Name)) throw SuperAdminIsFixed();
        await _repository.SyncRolePermissionsAsync(id, permissionIds, cancellationToken);
        return LaravelApiResponse.Success("role", await _repository.GetRoleAsync(id, cancellationToken), "Permissions updated successfully");
    }

    public async Task<LaravelApiResponse> SaveRolePermissionsAsync(SaveRolePermissionsRequestDto request, CancellationToken cancellationToken)
    {
        var permissionsByRole = new Dictionary<ulong, IReadOnlyCollection<ulong>>();
        foreach (var item in request.Permissions)
        {
            if (!ulong.TryParse(item.Key, out var roleId)) continue;

            var role = await _repository.GetRoleAsync(roleId, cancellationToken);
            if (role is null || IsSuperAdmin(role.Name)) continue;

            permissionsByRole[roleId] = item.Value;
        }

        await _repository.SaveRolePermissionsAsync(permissionsByRole, cancellationToken);
        return LaravelApiResponse.MessageOnly("success", "Permissions updated successfully");
    }

    public async Task<LaravelApiResponse> GetPermissionsAsync(string? search, CancellationToken cancellationToken) =>
        LaravelApiResponse.Success("permissions", await _repository.GetPermissionsAsync(search, cancellationToken));

    /// <summary>superadmin is the role every permission check falls back on: the API filter
    /// and the CRM both let it through without reading its permissions, and the catalog
    /// seeder grants it everything on start. Editing it therefore changes nothing, so it is
    /// refused rather than accepted and silently ignored.</summary>
    private static bool IsSuperAdmin(string? roleName) =>
        string.Equals(roleName?.Trim(), "superadmin", StringComparison.OrdinalIgnoreCase);

    private static LaravelHttpException SuperAdminIsFixed() =>
        new(LaravelStatusCodes.BadRequest,
            "The superadmin role always has every permission and cannot be edited.");

    private static void RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new LaravelHttpException(LaravelStatusCodes.BadRequest, "Role name is required.");
        }
    }

    private static string NormalizeGuard(string? guardName) =>
        string.IsNullOrWhiteSpace(guardName) ? "users" : guardName.Trim();

    private static LaravelHttpException NotFound(string message) =>
        new(LaravelStatusCodes.NotFound, message);
}
