namespace Domain.Entities;

public sealed class Permission
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string GuardName { get; set; } = "users";

    /// <summary>Screen-facing name, e.g. "Approve by SS". Filled from the permission catalog.</summary>
    public string? Label { get; set; }

    /// <summary>Menu section the module sits under, e.g. "loyalty".</summary>
    public string? GroupKey { get; set; }
    public string? GroupLabel { get; set; }

    /// <summary>Module the permission belongs to, e.g. "invoice_transaction".</summary>
    public string? ModuleKey { get; set; }
    public string? ModuleLabel { get; set; }

    /// <summary>Action within the module, e.g. "view", "create", "export". Shared across modules
    /// so the role matrix can tick one action down every column at once.</summary>
    public string? ActionKey { get; set; }

    /// <summary>Position in the role matrix.</summary>
    public int SortOrder { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
