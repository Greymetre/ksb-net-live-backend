namespace Domain.Entities;

public sealed class Customer : BaseEntity
{
    public string Active { get; set; } = "Y";
    public string Name { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Mobile { get; set; }
    public string? ContactNumber { get; set; }
    public string? Email { get; set; }
    public string Password { get; set; } = string.Empty;
    public string NotificationId { get; set; } = string.Empty;
    public string? Latitude { get; set; }
    public string? Longitude { get; set; }
    public string DeviceType { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string ProfileImage { get; set; } = string.Empty;
    public string? ShopImage { get; set; }
    public string CustomerCode { get; set; } = string.Empty;
    public ulong? StatusId { get; set; }
    public ulong? CustomerType { get; set; }
    public ulong? RegionId { get; set; }
    public ulong? FirmType { get; set; }
    public ulong? CreatedBy { get; set; }
    public ulong? UpdatedBy { get; set; }
    public ulong? ExecutiveId { get; set; }

    /// <summary>
    /// Who this customer is assigned to, kept by the database rather than by us.
    ///
    /// The assignment lives in custom_fields as JSON, and the zone and branch filters
    /// used to find it with ten wildcard LIKEs per employee per row - on the live data
    /// that is 197 seconds of CPU to answer one filter. These are persisted computed
    /// columns over the same JSON, so they are indexed, and SQL Server recomputes them
    /// itself on every write: nothing here can drift out of step with custom_fields.
    ///
    /// Read-only. Assigning a customer still means writing custom_fields.
    /// </summary>
    public ulong? AssignedEmployeeId { get; private set; }
    public ulong? AssignedSalesExecutiveId { get; private set; }
    /// <summary>executive_id, but only when custom_fields names no employee at all -
    /// which is the fallback the old filter applied.</summary>
    public ulong? AssignedFallbackEmployeeId { get; private set; }
    public ulong? BeatScheduleId { get; set; }
    public string ManagerName { get; set; } = string.Empty;
    public string ManagerPhone { get; set; } = string.Empty;
    public string? Otp { get; set; }
    public string? CustomFields { get; set; }
    public bool? SameAddress { get; set; }
    public ulong? ParentId { get; set; }
    public string? SapCode { get; set; }
}
