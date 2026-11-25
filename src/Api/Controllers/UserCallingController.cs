using System.Data;
using System.Globalization;
using Api.Filters;
using ClosedXML.Excel;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// The User listing "calling" action: a daily activity snapshot for one user on a chosen date
/// (defaults to yesterday), the submit that logs it to user_calling_report, and the Calling
/// Report / Issue Report exports.
/// </summary>
[ApiController]
[Authorize]
[Route("api")]
public sealed class UserCallingController : ControllerBase
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private readonly AppDbContext _dbContext;

    public UserCallingController(AppDbContext dbContext) => _dbContext = dbContext;

    // ---- Popup details --------------------------------------------------------
    [HttpGet("users/{id}/calling-details")]
    [RequirePermission("user.calling_details")]
    public async Task<IActionResult> GetCallingDetails(ulong id, [FromQuery] DateTime? date, CancellationToken cancellationToken)
    {
        var reportDate = ResolveDate(date);
        var detail = await ComputeCallingDetailAsync(id, reportDate, cancellationToken);
        if (detail is null) return NotFound(new { status = false, message = "User not found." });
        return Ok(new { status = true, data = ToResponse(detail) });
    }

    // ---- Submit (log to user_calling_report) ----------------------------------
    [HttpPost("users/{id}/calling-report")]
    [RequirePermission("user.calling_submit")]
    public async Task<IActionResult> SubmitCallingReport(ulong id, [FromBody] CallingReportRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CallingRemark))
            return UnprocessableEntity(new { status = false, message = new { calling_remark = new[] { "Calling remark is required." } } });

        var reportDate = ResolveDate(request.Date);
        var d = await ComputeCallingDetailAsync(id, reportDate, cancellationToken);
        if (d is null) return NotFound(new { status = false, message = "User not found." });

        await Execute(@"
INSERT INTO dbo.user_calling_report
    (user_id, report_date, zone, branch, employee_code, employee_name, designation, head_quarters, date_of_joining,
     punchin_time, punchout_time, total_working_time, working_type, first_customer_checkin_time, last_customer_checkout_time,
     total_visit, total_order, r30_count, r60_count, r90_count,
     total_retailer_registered, total_retailer_order_active, total_retailer_order_inactive, total_influencer_registered,
     calling_remark, issue, created_by, created_at)
VALUES
    (@uid, @d, @zone, @branch, @ecode, @ename, @desig, @hq, @doj,
     @pin, @pout, @wtime, @wtype, @fci, @lco,
     @tvisit, @torder, @r30, @r60, @r90,
     @trr, @troa, @troi, @tir,
     @remark, @issue, @by, SYSUTCDATETIME());",
            cancellationToken,
            ("@uid", (long)id), ("@d", reportDate),
            ("@zone", d.Zone), ("@branch", d.Branch), ("@ecode", d.EmployeeCode), ("@ename", d.EmployeeName),
            ("@desig", d.Designation), ("@hq", d.HeadQuarters), ("@doj", d.DateOfJoining),
            ("@pin", d.PunchinTime), ("@pout", d.PunchoutTime), ("@wtime", d.TotalWorkingTime), ("@wtype", d.WorkingType),
            ("@fci", d.FirstCheckinTime), ("@lco", d.LastCheckoutTime),
            ("@tvisit", d.TotalVisit), ("@torder", d.TotalOrder), ("@r30", d.R30), ("@r60", d.R60), ("@r90", d.R90),
            ("@trr", d.TotalRetailerRegistered), ("@troa", d.TotalRetailerOrderActive),
            ("@troi", d.TotalRetailerOrderInactive), ("@tir", d.TotalInfluencerRegistered),
            ("@remark", request.CallingRemark.Trim()),
            ("@issue", string.IsNullOrWhiteSpace(request.Issue) ? (object?)null : request.Issue.Trim()),
            ("@by", (long?)CurrentUserId()));

        return Ok(new { status = true, message = "Calling report submitted successfully." });
    }

    // ---- Calling Report export (details + remark, by report date) -------------
    [HttpGet("users/calling-report/export")]
    [RequirePermission("user.calling_report_export")]
    public async Task<IActionResult> ExportCallingReport([FromQuery(Name = "start_date")] DateTime? startDate, [FromQuery(Name = "end_date")] DateTime? endDate, CancellationToken cancellationToken)
    {
        var (start, end) = ResolveRange(startDate, endDate);
        var rows = await QueryRows(@"
SELECT CONVERT(varchar(10), report_date, 23) AS report_date, zone, branch, employee_code, employee_name, designation,
    head_quarters, date_of_joining, punchin_time, punchout_time, total_working_time, working_type,
    first_customer_checkin_time, last_customer_checkout_time, total_visit, total_order,
    r30_count, r60_count, r90_count, total_retailer_registered, total_retailer_order_active, total_retailer_order_inactive, total_influencer_registered,
    calling_remark
FROM dbo.user_calling_report
WHERE report_date BETWEEN @start AND @end
ORDER BY report_date DESC, id DESC;",
            cancellationToken, ("@start", start), ("@end", end));

        var headers = new[] { "Date", "Zone", "Branch", "Employee Code", "Employee Name", "Designation", "Head Quarters",
            "Date of Joining", "Punchin Time", "Punchout Time", "Total Working Time", "Working Type",
            "First Customer Checkin Time", "Last Customer Checkout Time", "Total Visit", "Total Order",
            "R-30 Count", "R-60 Count", "R-90+ Count", "Total Retailer Registered", "Total Retailer Order In Active", "Total Retailer Order In Inactive",
            "Total Influencer Registered", "Calling Remark" };
        var keys = new[] { "report_date", "zone", "branch", "employee_code", "employee_name", "designation", "head_quarters",
            "date_of_joining", "punchin_time", "punchout_time", "total_working_time", "working_type",
            "first_customer_checkin_time", "last_customer_checkout_time", "total_visit", "total_order",
            "r30_count", "r60_count", "r90_count", "total_retailer_registered", "total_retailer_order_active", "total_retailer_order_inactive",
            "total_influencer_registered", "calling_remark" };
        return BuildWorkbook("Calling Report", headers, keys, rows, "Calling_Report.xlsx");
    }

    // ---- Issue Report export (details + issue, by saved date; only rows with an issue) ----
    [HttpGet("users/issue-report/export")]
    [RequirePermission("user.issue_report_export")]
    public async Task<IActionResult> ExportIssueReport([FromQuery(Name = "start_date")] DateTime? startDate, [FromQuery(Name = "end_date")] DateTime? endDate, CancellationToken cancellationToken)
    {
        var (start, end) = ResolveRange(startDate, endDate);
        var rows = await QueryRows(@"
SELECT CONVERT(varchar(10), created_at, 23) AS saved_date, CONVERT(varchar(10), report_date, 23) AS report_date,
    zone, branch, employee_code, employee_name, designation, head_quarters, date_of_joining,
    punchin_time, punchout_time, total_working_time, working_type,
    first_customer_checkin_time, last_customer_checkout_time, total_visit, total_order,
    r30_count, r60_count, r90_count, total_retailer_registered, total_retailer_order_active, total_retailer_order_inactive, total_influencer_registered,
    issue
FROM dbo.user_calling_report
WHERE issue IS NOT NULL AND LTRIM(RTRIM(issue)) <> ''
  AND CAST(created_at AS date) BETWEEN @start AND @end
ORDER BY created_at DESC, id DESC;",
            cancellationToken, ("@start", start), ("@end", end));

        var headers = new[] { "Saved Date", "Report Date", "Zone", "Branch", "Employee Code", "Employee Name", "Designation",
            "Head Quarters", "Date of Joining", "Punchin Time", "Punchout Time", "Total Working Time", "Working Type",
            "First Customer Checkin Time", "Last Customer Checkout Time", "Total Visit", "Total Order",
            "R-30 Count", "R-60 Count", "R-90+ Count", "Total Retailer Registered", "Total Retailer Order In Active", "Total Retailer Order In Inactive",
            "Total Influencer Registered", "Issue" };
        var keys = new[] { "saved_date", "report_date", "zone", "branch", "employee_code", "employee_name", "designation",
            "head_quarters", "date_of_joining", "punchin_time", "punchout_time", "total_working_time", "working_type",
            "first_customer_checkin_time", "last_customer_checkout_time", "total_visit", "total_order",
            "r30_count", "r60_count", "r90_count", "total_retailer_registered", "total_retailer_order_active", "total_retailer_order_inactive",
            "total_influencer_registered", "issue" };
        return BuildWorkbook("Issue Report", headers, keys, rows, "Issue_Report.xlsx");
    }

    // ---- Shared compute -------------------------------------------------------
    private async Task<CallingDetail?> ComputeCallingDetailAsync(ulong id, string reportDate, CancellationToken cancellationToken)
    {
        var main = (await QueryRows(@"
SELECT
    dv.division_name AS zone,
    (SELECT TOP 1 b.branch_name FROM branches b
       WHERE b.deleted_at IS NULL
         AND ','+REPLACE(COALESCE(u.branch_id,''),' ','')+',' LIKE '%,'+CONVERT(varchar(30),b.id)+',%') AS branch,
    u.employee_codes AS employee_code,
    u.name AS employee_name,
    dg.designation_name AS designation,
    u.location AS head_quarters,
    CONVERT(varchar(10), u.date_of_joining, 23) AS date_of_joining,
    CONVERT(varchar(8), att.punchin_time, 108) AS punchin_time,
    CONVERT(varchar(8), att.punchout_time, 108) AS punchout_time,
    att.worked_time AS total_working_time,
    att.working_type AS working_type,
    (SELECT COUNT(*) FROM check_in ci WHERE ci.user_id=@uid AND ci.checkin_date=@d) AS total_visit,
    (SELECT CONVERT(varchar(8), MIN(ci.checkin_time), 108) FROM check_in ci WHERE ci.user_id=@uid AND ci.checkin_date=@d) AS first_checkin_time,
    (SELECT CONVERT(varchar(8), MAX(ci.checkout_time), 108) FROM check_in ci WHERE ci.user_id=@uid AND ci.checkin_date=@d AND ci.checkout_time IS NOT NULL) AS last_checkout_time,
    (SELECT COUNT(*) FROM orders o WHERE o.created_by=@uid AND o.deleted_at IS NULL AND CAST(COALESCE(o.order_date,o.created_at) AS date)=@d) AS total_order,
    (SELECT COUNT(*) FROM customers c WHERE c.customertype=3 AND c.deleted_at IS NULL
        AND (c.created_by=@uid OR c.executive_id=@uid
             OR EXISTS (SELECT 1 FROM employee_details ed WHERE ed.customer_id=c.id AND ed.user_id=@uid AND ed.deleted_at IS NULL AND (ed.active='Y' OR ed.active IS NULL)))) AS total_influencer_registered
FROM users u
LEFT JOIN divisions dv ON dv.id=u.division_id
LEFT JOIN designations dg ON dg.id=u.designation_id
OUTER APPLY (SELECT TOP 1 a.punchin_time, a.punchout_time, a.worked_time, a.working_type
             FROM attendances a WHERE a.user_id=@uid AND CAST(a.punchin_date AS date)=@d ORDER BY a.id DESC) att
WHERE u.id=@uid;",
            cancellationToken, ("@uid", (long)id), ("@d", reportDate))).FirstOrDefault();

        if (main is null) return null;

        // All retailer counts are over the user's retailers - created by OR assigned to the user
        // (unique per retailer, no customer active/inactive flag). "Order In Active" = has at least
        // one order; "Order In Inactive" = no order yet; the two sum to Total Retailer Registered.
        // R-30/60/90 are, among the retailers that HAVE ordered, how many have not ordered in the
        // last 30/60/90 days (nested: R-90 subset of R-60 subset of R-30).
        var ret = (await QueryRows(@"
SELECT
    COUNT(*) AS registered,
    SUM(has_order) AS order_active,
    SUM(1 - has_order) AS order_inactive,
    SUM(CASE WHEN has_order=1 AND no30=1 THEN 1 ELSE 0 END) AS r30,
    SUM(CASE WHEN has_order=1 AND no60=1 THEN 1 ELSE 0 END) AS r60,
    SUM(CASE WHEN has_order=1 AND no90=1 THEN 1 ELSE 0 END) AS r90
FROM (
    SELECT
        CASE WHEN EXISTS (SELECT 1 FROM orders o WHERE o.buyer_id=c.id AND o.deleted_at IS NULL
             AND CAST(COALESCE(o.order_date,o.created_at) AS date)<=@d) THEN 1 ELSE 0 END AS has_order,
        CASE WHEN NOT EXISTS (SELECT 1 FROM orders o WHERE o.buyer_id=c.id AND o.deleted_at IS NULL
             AND CAST(COALESCE(o.order_date,o.created_at) AS date) BETWEEN DATEADD(day,-30,@d) AND @d) THEN 1 ELSE 0 END AS no30,
        CASE WHEN NOT EXISTS (SELECT 1 FROM orders o WHERE o.buyer_id=c.id AND o.deleted_at IS NULL
             AND CAST(COALESCE(o.order_date,o.created_at) AS date) BETWEEN DATEADD(day,-60,@d) AND @d) THEN 1 ELSE 0 END AS no60,
        CASE WHEN NOT EXISTS (SELECT 1 FROM orders o WHERE o.buyer_id=c.id AND o.deleted_at IS NULL
             AND CAST(COALESCE(o.order_date,o.created_at) AS date) BETWEEN DATEADD(day,-90,@d) AND @d) THEN 1 ELSE 0 END AS no90
    FROM customers c
    WHERE c.customertype=2 AND c.deleted_at IS NULL
      AND (c.created_by=@uid OR c.executive_id=@uid
           OR EXISTS (SELECT 1 FROM employee_details ed
                      WHERE ed.customer_id=c.id AND ed.user_id=@uid AND ed.deleted_at IS NULL AND (ed.active='Y' OR ed.active IS NULL)))
) x;",
            cancellationToken, ("@uid", (long)id), ("@d", reportDate))).FirstOrDefault();

        return new CallingDetail(
            reportDate, Str(main, "zone"), Str(main, "branch"), Str(main, "employee_code"), Str(main, "employee_name"),
            Str(main, "designation"), Str(main, "head_quarters"), Str(main, "date_of_joining"),
            Str(main, "punchin_time"), Str(main, "punchout_time"), Str(main, "total_working_time"), Str(main, "working_type"),
            Str(main, "first_checkin_time"), Str(main, "last_checkout_time"),
            Int(main, "total_visit"), Int(main, "total_order"), Int(ret, "r30"), Int(ret, "r60"), Int(ret, "r90"),
            Int(ret, "registered"), Int(ret, "order_active"),
            Int(ret, "order_inactive"), Int(main, "total_influencer_registered"));
    }

    private static object ToResponse(CallingDetail d) => new
    {
        date = d.Date, zone = d.Zone, branch = d.Branch, employee_code = d.EmployeeCode, employee_name = d.EmployeeName,
        designation = d.Designation, head_quarters = d.HeadQuarters, date_of_joining = d.DateOfJoining,
        punchin_time = d.PunchinTime, punchout_time = d.PunchoutTime, total_working_time = d.TotalWorkingTime,
        working_type = d.WorkingType, first_customer_checkin_time = d.FirstCheckinTime,
        last_customer_checkout_time = d.LastCheckoutTime, total_visit = d.TotalVisit, total_order = d.TotalOrder,
        r30_count = d.R30, r60_count = d.R60, r90_count = d.R90, total_retailer_registered = d.TotalRetailerRegistered,
        total_retailer_order_active = d.TotalRetailerOrderActive,
        total_retailer_order_inactive = d.TotalRetailerOrderInactive,
        total_influencer_registered = d.TotalInfluencerRegistered
    };

    private sealed record CallingDetail(
        string Date, string Zone, string Branch, string EmployeeCode, string EmployeeName, string Designation,
        string HeadQuarters, string DateOfJoining, string PunchinTime, string PunchoutTime, string TotalWorkingTime,
        string WorkingType, string FirstCheckinTime, string LastCheckoutTime, long TotalVisit, long TotalOrder,
        long R30, long R60, long R90, long TotalRetailerRegistered, long TotalRetailerOrderActive,
        long TotalRetailerOrderInactive, long TotalInfluencerRegistered);

    public sealed class CallingReportRequest
    {
        public DateTime? Date { get; set; }
        public string? CallingRemark { get; set; }
        public string? Issue { get; set; }
    }

    // ---- Helpers --------------------------------------------------------------
    private static string ResolveDate(DateTime? date)
    {
        var istToday = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date;
        return (date?.Date ?? istToday.AddDays(-1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static (string Start, string End) ResolveRange(DateTime? startDate, DateTime? endDate)
    {
        var istToday = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date;
        var end = (endDate?.Date ?? istToday);
        var start = (startDate?.Date ?? end.AddMonths(-1));
        return (start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private FileContentResult BuildWorkbook(string sheetName, string[] headers, string[] keys, IReadOnlyList<Dictionary<string, object?>> rows, string fileName)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(sheetName);
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < keys.Length; c++) sheet.Cell(r, c + 1).Value = Str(row, keys[c]);
            r++;
        }
        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return File(stream.ToArray(), ExcelContentType, fileName);
    }

    private ulong? CurrentUserId()
    {
        var value = User.FindFirst("id")?.Value ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return ulong.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string Str(IReadOnlyDictionary<string, object?>? row, string key) =>
        row is not null && row.TryGetValue(key, out var v) && v is not null and not DBNull
            ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    private static long Int(IReadOnlyDictionary<string, object?>? row, string key) =>
        row is not null && row.TryGetValue(key, out var v) && v is not null and not DBNull
            ? Convert.ToInt64(v, CultureInfo.InvariantCulture)
            : 0;

    private async Task<IReadOnlyList<Dictionary<string, object?>>> QueryRows(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private async Task Execute(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(IDbCommand command, (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            command.Parameters.Add(p);
        }
    }
}
