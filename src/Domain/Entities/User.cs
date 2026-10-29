namespace Domain.Entities;

public sealed class User : BaseEntity
{
    public string Active { get; set; } = "Y";
    public string Name { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public DateTime? EmailVerifiedAt { get; set; }
    public string Password { get; set; } = string.Empty;
    public string? PasswordString { get; set; }
    public string? RememberToken { get; set; }
    // CRM two-factor (TOTP authenticator app): the on/off switch, the confirmed Base32 secret
    // the app shares with the server, and the pending secret during setup ("setup:{base32}").
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }
    public string? TwoFactorState { get; set; }
    public string NotificationId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string ProfileImage { get; set; } = string.Empty;
    public string Latitude { get; set; } = string.Empty;
    public string Longitude { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public ulong? ReportingId { get; set; }
    /// <summary>All reporting managers as a comma-separated list of user ids. ReportingId is
    /// the first (primary) of these; a user may report to more than one manager, and each of
    /// them sees the user reporting-wise.</summary>
    public string? ReportingIds { get; set; }
    public ulong? RegionId { get; set; }
    public string? EmployeeCodes { get; set; }
    public string? BranchId { get; set; }
    public ulong? PrimaryBranchId { get; set; }
    public string? BranchShow { get; set; }
    public ulong? DesignationId { get; set; }
    public ulong? DepartmentId { get; set; }
    public ulong? DivisionId { get; set; }
    public ulong? WarehouseId { get; set; }
    public string SalesType { get; set; } = string.Empty;
    public ulong? CreatedBy { get; set; }
    public string? Payroll { get; set; }
    public decimal LeaveBalance { get; set; }
    public decimal? CompbOff { get; set; }
    public string? Grade { get; set; }
    public string? BloodGroup { get; set; }
    public string? PersonalNumber { get; set; }
    public ulong? CustomerId { get; set; }
    public string? ShowAttandanceReport { get; set; }
    public decimal? EarnedLeaveBalance { get; set; }
    public decimal? CasualLeaveBalance { get; set; }
    public decimal? SickLeaveBalance { get; set; }
    public DateTime? DateOfJoining { get; set; }
    public DateTime? LastLeaveAccrualDate { get; set; }
    public DateTime? EarnedLeaveClaimActivatedAt { get; set; }
    public decimal? ClaimableEarnedLeaveBalance { get; set; }
    public bool IsDeleted { get; set; }
}
