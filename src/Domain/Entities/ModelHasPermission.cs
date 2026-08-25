namespace Domain.Entities;

/// <summary>Legacy per-user permission grants. Unused: every permission this CRM checks
/// comes from the user's roles, and the table is kept empty.</summary>
public sealed class ModelHasPermission
{
    public ulong PermissionId { get; set; }
    public string ModelType { get; set; } = string.Empty;
    public ulong ModelId { get; set; }
    public Permission? Permission { get; set; }
}
