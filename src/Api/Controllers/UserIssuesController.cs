using System.Data;
using System.Globalization;
using System.Security.Claims;
using Api.Filters;
using Application.Interfaces.Repositories;
using ClosedXML.Excel;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// User Issues - the issues raised from the User Calling popup (user_calling_report rows that carry
/// a non-empty "issue"), with their resolution lifecycle. The listing/export are scoped to the users
/// the caller may see (admin = all); the resolve action records who solved/handled it and a remark,
/// and once an issue is Resolved only a super admin may change it again.
/// </summary>
[ApiController]
[Authorize]
[Route("api/user-issues")]
public sealed class UserIssuesController : ControllerBase
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly string[] AllowedStatuses = ["In Progress", "Resolved"];

    private readonly AppDbContext _dbContext;
    private readonly IHrRepository _hr;

    public UserIssuesController(AppDbContext dbContext, IHrRepository hr)
    {
        _dbContext = dbContext;
        _hr = hr;
    }

    // ---- Listing --------------------------------------------------------------
    [HttpGet]
    [RequirePermission("user_issue.view")]
    public async Task<IActionResult> List(
        [FromQuery] int page,
        [FromQuery(Name = "page_size")] int pageSize,
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery(Name = "start_date")] DateTime? startDate,
        [FromQuery(Name = "end_date")] DateTime? endDate,
        CancellationToken cancellationToken)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 25;

        var all = await LoadIssuesAsync(search, status, startDate, endDate, cancellationToken);
        var total = all.Count;
        var rows = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Ok(new { status = true, data = rows, total, page, page_size = pageSize });
    }

    // ---- Export ---------------------------------------------------------------
    [HttpGet("export")]
    [RequirePermission("user_issue.export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery(Name = "start_date")] DateTime? startDate,
        [FromQuery(Name = "end_date")] DateTime? endDate,
        CancellationToken cancellationToken)
    {
        var rows = await LoadIssuesAsync(search, status, startDate, endDate, cancellationToken);

        var headers = new[] { "Zone", "Branch", "E Code", "Name", "Designation", "Issue Date", "Issue Description",
            "Status", "Issue Create By", "Solve By", "Solve Remark" };
        var keys = new[] { "zone", "branch", "employee_code", "employee_name", "designation", "issue_date", "issue_description",
            "status", "issue_created_by", "solved_by_name", "solve_remark" };

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("User Issues");
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < keys.Length; c++)
                sheet.Cell(r, c + 1).Value = row.TryGetValue(keys[c], out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
            r++;
        }
        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return File(stream.ToArray(), ExcelContentType, "User_Issues.xlsx");
    }

    // ---- Create a new issue (pick a user + issue text) ------------------------
    [HttpPost]
    [RequirePermission("user_issue.create")]
    public async Task<IActionResult> Create([FromBody] CreateRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.UserId is null or <= 0)
            return UnprocessableEntity(new { status = false, message = new { user_id = new[] { "Please select a user." } } });
        if (string.IsNullOrWhiteSpace(request.Issue))
            return UnprocessableEntity(new { status = false, message = new { issue = new[] { "Issue is required." } } });

        var u = (await QueryRows(@"
SELECT
    dv.division_name AS zone,
    (SELECT TOP 1 b.branch_name FROM branches b
       WHERE b.deleted_at IS NULL
         AND ','+REPLACE(COALESCE(u.branch_id,''),' ','')+',' LIKE '%,'+CONVERT(varchar(30),b.id)+',%') AS branch,
    u.employee_codes AS employee_code,
    u.name AS employee_name,
    dg.designation_name AS designation,
    u.location AS head_quarters,
    CONVERT(varchar(10), u.date_of_joining, 23) AS date_of_joining
FROM users u
LEFT JOIN divisions dv ON dv.id = u.division_id
LEFT JOIN designations dg ON dg.id = u.designation_id
WHERE u.id = @uid AND u.active = 'Y' AND ISNULL(u.isDeleted,0) = 0 AND u.deleted_at IS NULL AND u.customerid IS NULL;",
            cancellationToken, ("@uid", request.UserId.Value))).FirstOrDefault();
        if (u is null)
            return UnprocessableEntity(new { status = false, message = new { user_id = new[] { "Selected user is not a valid active user." } } });

        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await Execute(@"
INSERT INTO dbo.user_calling_report
    (user_id, report_date, zone, branch, employee_code, employee_name, designation, head_quarters, date_of_joining,
     calling_remark, issue, status, created_by, created_at)
VALUES
    (@uid, @d, @zone, @branch, @ecode, @ename, @desig, @hq, @doj,
     N'', @issue, N'Pending', @by, SYSUTCDATETIME());",
            cancellationToken,
            ("@uid", request.UserId.Value), ("@d", today),
            ("@zone", Str(u, "zone")), ("@branch", Str(u, "branch")), ("@ecode", Str(u, "employee_code")),
            ("@ename", Str(u, "employee_name")), ("@desig", Str(u, "designation")),
            ("@hq", Str(u, "head_quarters")), ("@doj", Str(u, "date_of_joining")),
            ("@issue", request.Issue.Trim()), ("@by", (long?)CurrentUserId()));

        return Ok(new { status = true, message = "Issue created successfully." });
    }

    // ---- Solver options (all active internal users) ---------------------------
    [HttpGet("solver-options")]
    [RequirePermission("user_issue.view")]
    public async Task<IActionResult> SolverOptions(CancellationToken cancellationToken)
    {
        var rows = await QueryRows(@"
SELECT id, name, employee_codes
FROM users
WHERE active = 'Y' AND ISNULL(isDeleted, 0) = 0 AND deleted_at IS NULL AND customerid IS NULL
ORDER BY name;", cancellationToken);

        var options = rows.Select(x => new
        {
            id = Int(x, "id"),
            name = Str(x, "name"),
            employee_code = Str(x, "employee_codes")
        });
        return Ok(new { status = true, data = options });
    }

    // ---- Resolve / update -----------------------------------------------------
    [HttpPost("{id}/resolve")]
    [RequirePermission("user_issue.resolve")]
    public async Task<IActionResult> Resolve(long id, [FromBody] ResolveRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
            return UnprocessableEntity(new { status = false, message = "Invalid request." });

        var newStatus = (request.Status ?? string.Empty).Trim();
        if (!AllowedStatuses.Contains(newStatus, StringComparer.OrdinalIgnoreCase))
            return UnprocessableEntity(new { status = false, message = new { status = new[] { "Status must be In Progress or Resolved." } } });
        newStatus = AllowedStatuses.First(s => string.Equals(s, newStatus, StringComparison.OrdinalIgnoreCase));
        var isResolved = string.Equals(newStatus, "Resolved", StringComparison.OrdinalIgnoreCase);

        // Solve By + Solve Remark only apply when the issue is being Resolved. For "In Progress"
        // nothing is "solved" yet, so those fields are cleared and not required.
        if (isResolved)
        {
            if (string.IsNullOrWhiteSpace(request.SolveRemark))
                return UnprocessableEntity(new { status = false, message = new { solve_remark = new[] { "Solve remark is required." } } });

            if (request.SolvedBy is null or <= 0)
                return UnprocessableEntity(new { status = false, message = new { solved_by = new[] { "Please select who solved the issue." } } });

            var solverOk = (await QueryRows(
                "SELECT id FROM users WHERE id = @sid AND active = 'Y' AND ISNULL(isDeleted,0) = 0 AND deleted_at IS NULL;",
                cancellationToken, ("@sid", request.SolvedBy.Value))).Count > 0;
            if (!solverOk)
                return UnprocessableEntity(new { status = false, message = new { solved_by = new[] { "Selected user is not a valid active user." } } });
        }

        var existing = (await QueryRows(
            "SELECT status, issue FROM dbo.user_calling_report WHERE id = @id;",
            cancellationToken, ("@id", id))).FirstOrDefault();
        if (existing is null || string.IsNullOrWhiteSpace(Str(existing, "issue")))
            return NotFound(new { status = false, message = "Issue not found." });

        // Once Resolved the issue is final and can no longer be changed by anyone.
        if (string.Equals(Str(existing, "status"), "Resolved", StringComparison.OrdinalIgnoreCase))
            return StatusCode(403, new { status = false, message = "This issue is already resolved and can no longer be changed." });

        await Execute(@"
UPDATE dbo.user_calling_report
SET status = @status, solved_by = @solved_by, solve_remark = @remark,
    solved_at = CASE WHEN @is_resolved = 1 THEN SYSUTCDATETIME() ELSE NULL END,
    updated_at = SYSUTCDATETIME()
WHERE id = @id;",
            cancellationToken,
            ("@status", newStatus),
            ("@solved_by", isResolved ? request.SolvedBy!.Value : (object?)null),
            ("@remark", isResolved ? request.SolveRemark!.Trim() : (object?)null),
            ("@is_resolved", isResolved ? 1 : 0),
            ("@id", id));

        return Ok(new { status = true, message = "Issue updated successfully." });
    }

    // ---- Shared load ----------------------------------------------------------
    private async Task<List<Dictionary<string, object?>>> LoadIssuesAsync(
        string? search, string? status, DateTime? startDate, DateTime? endDate, CancellationToken cancellationToken)
    {
        var (start, end) = ResolveRange(startDate, endDate);
        var parameters = new List<(string, object?)> { ("@start", start), ("@end", end) };

        var where = @"WHERE r.issue IS NOT NULL AND LTRIM(RTRIM(r.issue)) <> ''
  AND CAST(r.created_at AS date) BETWEEN @start AND @end";

        if (!string.IsNullOrWhiteSpace(status))
        {
            where += " AND r.status = @status";
            parameters.Add(("@status", status.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            where += " AND (r.employee_name LIKE @q OR r.employee_code LIKE @q OR r.zone LIKE @q OR r.branch LIKE @q OR r.issue LIKE @q)";
            parameters.Add(("@q", "%" + search.Trim() + "%"));
        }

        var rows = await QueryRows($@"
SELECT r.id, r.user_id,
    r.zone, r.branch, r.employee_code, r.employee_name, r.designation,
    CONVERT(varchar(10), r.created_at, 23) AS issue_date,
    r.issue AS issue_description,
    ISNULL(r.status, N'Pending') AS status,
    cb.name AS issue_created_by,
    r.solved_by,
    sb.name AS solved_by_name,
    r.solve_remark
FROM dbo.user_calling_report r
LEFT JOIN users cb ON cb.id = r.created_by
LEFT JOIN users sb ON sb.id = r.solved_by
{where}
ORDER BY r.created_at DESC, r.id DESC;",
            cancellationToken, parameters.ToArray());

        // Scope: only issues about users the caller may see (admin = every internal user).
        var visible = (await _hr.GetVisibleUserIdsAsync(CurrentUserId(), includeInactive: true, cancellationToken))
            .Select(x => (long)x).ToHashSet();
        return rows.Where(x => visible.Contains(Int(x, "user_id"))).ToList();
    }

    public sealed class CreateRequest
    {
        public long? UserId { get; set; }
        public string? Issue { get; set; }
    }

    public sealed class ResolveRequest
    {
        public string? Status { get; set; }
        public long? SolvedBy { get; set; }
        public string? SolveRemark { get; set; }
    }

    // ---- Helpers --------------------------------------------------------------
    private static (string Start, string End) ResolveRange(DateTime? startDate, DateTime? endDate)
    {
        var istToday = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).Date;
        var end = endDate?.Date ?? istToday;
        var start = startDate?.Date ?? end.AddMonths(-1);
        return (start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private ulong? CurrentUserId()
    {
        var value = User.FindFirst("id")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
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
