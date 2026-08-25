namespace Application.DTOs.Roles;

public sealed class PermissionDto
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GuardName { get; set; } = "users";

    /// <summary>Catalog metadata the role matrix is drawn from: what to call the permission,
    /// which module row it belongs to and which action column it ticks.</summary>
    public string Label { get; set; } = string.Empty;
    public string GroupKey { get; set; } = string.Empty;
    public string GroupLabel { get; set; } = string.Empty;
    public string ModuleKey { get; set; } = string.Empty;
    public string ModuleLabel { get; set; } = string.Empty;
    public string ActionKey { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public sealed class RoleDto
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GuardName { get; set; } = "users";
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int UserCount { get; set; }
    public IReadOnlyCollection<PermissionDto> Permissions { get; set; } = [];
}

public sealed class RoleRequestDto
{
    public string? Name { get; set; }
    public string? GuardName { get; set; }
    public IReadOnlyCollection<ulong>? Permissions { get; set; }
}

public sealed class SaveRolePermissionsRequestDto
{
    public IDictionary<string, IReadOnlyCollection<ulong>> Permissions { get; set; } = new Dictionary<string, IReadOnlyCollection<ulong>>();
}

public sealed class RolePermissionsRequestDto
{
    public IReadOnlyCollection<ulong> Permissions { get; set; } = [];
}
