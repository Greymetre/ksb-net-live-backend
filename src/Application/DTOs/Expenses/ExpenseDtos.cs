using Application.DTOs.Users;

namespace Application.DTOs.Expenses;

public sealed class ExpenseFilterDto
{
    public ulong? ExecutiveId { get; set; }
    public ulong? ExpensesType { get; set; }
    public ulong? BranchId { get; set; }
    public ulong? DivisionId { get; set; }
    public string? Payroll { get; set; }
    public int? Status { get; set; }
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public ulong? ExpenseId { get; set; }
    public string? Search { get; set; }

    /// <summary>Signed-in user. Rows are narrowed to whoever this user may report on.</summary>
    public ulong? ActorUserId { get; set; }
}

public sealed class ExpenseDto
{
    public ulong Id { get; set; }
    public ulong? ExpensesType { get; set; }
    public string? ExpenseTypeName { get; set; }
    public ulong? UserId { get; set; }
    public string? UserName { get; set; }
    public string? EmployeeCode { get; set; }
    public string? DesignationName { get; set; }
    public ulong? BranchId { get; set; }
    public string? BranchName { get; set; }
    public ulong? DivisionId { get; set; }
    public string? DivisionName { get; set; }
    public string? Payroll { get; set; }
    public string Date { get; set; } = string.Empty;
    public decimal ClaimAmount { get; set; }
    public decimal? ApproveAmount { get; set; }
    public string? StartKm { get; set; }
    public string? StopKm { get; set; }
    public string? TotalKm { get; set; }
    public string? Note { get; set; }
    public int CheckerStatus { get; set; }
    public string CheckerStatusName { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public ulong? ApproveRejectBy { get; set; }
    public string? ApproveRejectByName { get; set; }
    public DateTime? CreatedAt { get; set; }
    public IReadOnlyCollection<ExpenseAttachmentDto> Attachments { get; set; } = [];
}

public sealed class ExpenseAttachmentDto
{
    public ulong Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public ulong Size { get; set; }
    public string Url { get; set; } = string.Empty;
}

public sealed class ExpenseRequestDto
{
    public ulong? ExpensesType { get; set; }
    public ulong? UserId { get; set; }
    public string? Date { get; set; }
    public decimal? ClaimAmount { get; set; }
    public decimal? ApproveAmount { get; set; }
    public string? StartKm { get; set; }
    public string? StopKm { get; set; }
    public string? TotalKm { get; set; }
    public string? Note { get; set; }
    public string? Reason { get; set; }
}

public sealed class ExpenseUploadDto
{
    public string OriginalName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public ulong Size { get; set; }
}

public sealed class ExpenseStatusRequestDto
{
    public int? Status { get; set; }
    public decimal? ApproveAmount { get; set; }
    public string? Reason { get; set; }
}

/// <summary>A selectable employee plus the grade that decides their expense types.</summary>
public sealed class ExpenseUserOptionDto
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Payroll { get; set; }
}

/// <summary>The signed-in user, as the mobile form needs them: name and payroll grade.</summary>
public sealed class ExpenseActorDto
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Payroll { get; set; }
    public string PayrollName { get; set; } = string.Empty;
    public bool HasGrade { get; set; }
}

public sealed class ExpenseLogDto
{
    public ulong Id { get; set; }
    public ulong ExpenseId { get; set; }
    public string StatusType { get; set; } = string.Empty;
    public string LogDate { get; set; } = string.Empty;
    public ulong? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public sealed class ExpenseMonthSummaryDto
{
    public string Label { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Pending { get; set; }
    public decimal ClaimAmount { get; set; }
    public decimal ApproveAmount { get; set; }
}

public sealed class ExpenseSummaryDto
{
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Checked { get; set; }
    public int CheckedByReporting { get; set; }
    public int Approved { get; set; }
    public int Rejected { get; set; }
    public int Hold { get; set; }
    public decimal ClaimAmount { get; set; }
    public decimal ApproveAmount { get; set; }
    public ExpenseMonthSummaryDto Month { get; set; } = new();
}

public sealed class ExpenseOptionsDto
{
    public IReadOnlyCollection<ExpenseUserOptionDto> Users { get; set; } = [];

    /// <summary>Signed-in user plus the types their grade allows, so the mobile form
    /// needs a single call and never has to guess the grade rule.</summary>
    public ExpenseActorDto? Me { get; set; }
    public IReadOnlyCollection<ExpenseTypeDto> MyExpenseTypes { get; set; } = [];
    public IReadOnlyCollection<ExpenseTypeDto> ExpenseTypes { get; set; } = [];
    public IReadOnlyCollection<OptionDto> Branches { get; set; } = [];
    public IReadOnlyCollection<OptionDto> Divisions { get; set; } = [];
    public IReadOnlyCollection<OptionDto> Payrolls { get; set; } = [];
    public IReadOnlyCollection<OptionDto> Statuses { get; set; } = [];
}

public static class ExpenseStatusLookups
{
    public static readonly IReadOnlyDictionary<int, string> Statuses = new Dictionary<int, string>
    {
        [0] = "Pending",
        [1] = "Approved",
        [2] = "Rejected",
        [3] = "Checked",
        [4] = "Checked By Reporting",
        [5] = "Hold"
    };

    public static string StatusName(int status) =>
        Statuses.TryGetValue(status, out var name) ? name : "Pending";
}
