using System.Security.Claims;
using Api.Filters;
using ClosedXML.Excel;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController, Authorize, Route("api/reports/activity-report")]
public sealed class ActivityReportExportsController : ControllerBase
{
    private readonly AppDbContext _db;
    public ActivityReportExportsController(AppDbContext db) => _db = db;

    [HttpGet("options"), RequirePermission("activity_report_access")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        var rows = await Rows(new ActivityReportFilter(), ct);
        return Ok(new {
            zones = rows.Where(x => x.DivisionId.HasValue).Select(x => new { id = x.DivisionId, name = x.Zone }).Distinct().OrderBy(x => x.name),
            branches = rows.Where(x => x.BranchId.HasValue).Select(x => new { id = x.BranchId, name = x.Branch, zone_id = x.DivisionId }).Distinct().OrderBy(x => x.name),
            meets = new[] { new { id = "retailer", name = "Retailer Meet" }, new { id = "nukkad", name = "Nukkad Meet" }, new { id = "farmer", name = "Farmer Meet / Demo" }, new { id = "influencer", name = "Influencer Meet" } }
        });
    }

    [HttpGet("sales-engineer-export"), RequirePermission("activity_report_sales_engineer_download")]
    public async Task<IActionResult> SalesEngineer([FromQuery] ActivityReportFilter filter, CancellationToken ct)
    {
        if (!ValidMeet(filter.Meet)) return BadRequest(new { status = "error", message = "Please select a valid meet before downloading the report." });
        var raw = await Rows(filter, ct);
        var rows = raw.GroupBy(x => new { x.Zone, x.Branch, x.CreatorId, x.CreatorName, x.UserId, x.UserName })
            .Select(g => new ActivityExportRow(g.Key.Zone, g.Key.Branch, null, g.Key.CreatorName, g.Key.UserName, g.Count(), g.Sum(x => x.Participants), g.Sum(x => x.GiftCount), g.Sum(x => x.TotalExpense)))
            .OrderBy(x => x.Zone).ThenBy(x => x.Branch).ThenBy(x => x.SalesEngineer).ThenBy(x => x.AsrName).ToList();
        return Workbook(rows, filter, "Sales Engg wise", false);
    }

    [HttpGet("distributor-export"), RequirePermission("activity_report_distributor_download")]
    public async Task<IActionResult> Distributor([FromQuery] ActivityReportFilter filter, CancellationToken ct)
    {
        if (!ValidMeet(filter.Meet)) return BadRequest(new { status = "error", message = "Please select a valid meet before downloading the report." });
        var raw = await Rows(filter, ct);
        var rows = raw.GroupBy(x => new { x.Zone, x.Branch, x.DistributorId, x.DistributorName, x.CreatorId, x.CreatorName, x.UserId, x.UserName })
            .Select(g => new ActivityExportRow(g.Key.Zone, g.Key.Branch, g.Key.DistributorName, g.Key.CreatorName, g.Key.UserName, g.Count(), g.Sum(x => x.Participants), g.Sum(x => x.GiftCount), g.Sum(x => x.TotalExpense)))
            .OrderBy(x => x.Zone).ThenBy(x => x.Branch).ThenBy(x => x.Distributor).ThenBy(x => x.AsrName).ToList();
        return Workbook(rows, filter, "Distributor wise", true);
    }

    [HttpGet("gift-summary-export"), RequirePermission("activity_report_gift_summary_download")]
    public async Task<IActionResult> GiftSummary([FromQuery] ActivityReportFilter filter, CancellationToken ct)
    {
        if (!ValidMeet(filter.Meet)) return BadRequest(new { status = "error", message = "Please select a valid meet before downloading the report." });
        var raw = await Rows(filter, ct); var ids = raw.Select(x => x.Id).ToArray();
        var gifts = ids.Length == 0 ? [] : await _db.PromotionalActivityParticipants.AsNoTracking()
            .Where(x => ids.Contains(x.ActivityId) && x.DeletedAt == null && x.GiftName != null && x.GiftName != "")
            .Select(x => new GiftEntry(x.ActivityId, x.GiftName!)).ToListAsync(ct);
        var giftNames = gifts.Select(x => x.Name.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var byActivity = gifts.GroupBy(x => x.ActivityId).ToDictionary(g => g.Key, g => g.Select(x => x.Name.Trim()).ToList());
        using var book = new XLWorkbook(); var sheet = book.Worksheets.Add("Gift Summary");
        const int headerRow = 1; string[] fixedHeaders = ["Sr. No", "Zone", "Branch"];
        for (var i = 0; i < fixedHeaders.Length; i++) sheet.Cell(headerRow, i + 1).Value = fixedHeaders[i];
        for (var i = 0; i < giftNames.Count; i++) sheet.Cell(headerRow, i + 4).Value = giftNames[i];
        var columns = giftNames.Count + 4; sheet.Cell(headerRow, columns).Value = "Total Gifts"; var output = headerRow + 1; var serial = 1;
        foreach (var zone in raw.GroupBy(x => x.Zone).OrderBy(x => x.Key)) {
            var zoneTotals = giftNames.ToDictionary(x => x, _ => 0, StringComparer.OrdinalIgnoreCase);
            foreach (var branch in zone.GroupBy(x => x.Branch).OrderBy(x => x.Key)) {
                sheet.Cell(output, 1).Value = serial++; sheet.Cell(output, 2).Value = zone.Key; sheet.Cell(output, 3).Value = branch.Key;
                var names = branch.SelectMany(x => byActivity.GetValueOrDefault(x.Id, [])).ToList();
                for (var i = 0; i < giftNames.Count; i++) { var count = names.Count(x => string.Equals(x, giftNames[i], StringComparison.OrdinalIgnoreCase)); sheet.Cell(output, i + 4).Value = count; zoneTotals[giftNames[i]] += count; }
                sheet.Cell(output, columns).Value = names.Count; output++;
            }
            GiftTotal(sheet, output++, $"{zone.Key} ZONE TOTAL", giftNames, zoneTotals, XLColor.FromHtml("FFF2CC"));
        }
        var grand = giftNames.ToDictionary(n => n, n => gifts.Count(x => string.Equals(x.Name.Trim(), n, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        GiftTotal(sheet, output, "GRAND TOTAL", giftNames, grand, XLColor.FromHtml("1F4E78"), true); Style(sheet, headerRow, output, columns);
        return Excel(book, $"{MeetFileName(filter.Meet)}_Gift_Summary_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }

    [HttpGet("preview"), RequirePermission("activity_report_access")]
    public async Task<IActionResult> Preview([FromQuery] ActivityReportFilter filter, CancellationToken ct)
    {
        if (!ValidMeet(filter.Meet)) return BadRequest(new { status = "error", message = "Please select a valid meet." });
        var raw = await Rows(filter, ct); var ids = raw.Select(x => x.Id).ToArray();
        var giftEntries = ids.Length == 0 ? [] : await _db.PromotionalActivityParticipants.AsNoTracking()
            .Where(x => ids.Contains(x.ActivityId) && x.DeletedAt == null && x.GiftName != null && x.GiftName != "")
            .Select(x => new GiftEntry(x.ActivityId, x.GiftName!)).ToListAsync(ct);
        var sales = raw.GroupBy(x => new { x.Zone, x.Branch, x.CreatorId, x.CreatorName, x.UserId, x.UserName })
            .Select(g => new { zone = g.Key.Zone, branch = g.Key.Branch, sales_engineer = g.Key.CreatorName, asr_name = g.Key.UserName, meets = g.Count(), participants = g.Sum(x => x.Participants), gifts = g.Sum(x => x.GiftCount), expense = g.Sum(x => x.TotalExpense) })
            .OrderBy(x => x.zone).ThenBy(x => x.branch).ThenBy(x => x.sales_engineer).ToList();
        var distributors = raw.GroupBy(x => new { x.Zone, x.Branch, x.DistributorId, x.DistributorName, x.CreatorName, x.UserName })
            .Select(g => new { zone = g.Key.Zone, branch = g.Key.Branch, distributor = g.Key.DistributorName, sales_engineer = g.Key.CreatorName, asr_name = g.Key.UserName, meets = g.Count(), participants = g.Sum(x => x.Participants), gifts = g.Sum(x => x.GiftCount), expense = g.Sum(x => x.TotalExpense) })
            .OrderBy(x => x.zone).ThenBy(x => x.branch).ThenBy(x => x.distributor).ToList();
        var giftSummary = giftEntries.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(x => x.Key.Length > 0)
            .Select(g => new { gift_name = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ThenBy(x => x.gift_name).ToList();
        return Ok(new { status = "success", data = new { summary = new { meets = raw.Count, participants = raw.Sum(x => x.Participants), gifts = raw.Sum(x => x.GiftCount), expense = raw.Sum(x => x.TotalExpense), distributors = raw.Where(x => x.DistributorId.HasValue).Select(x => x.DistributorId).Distinct().Count(), sales_engineers = raw.Select(x => x.CreatorId).Distinct().Count() }, sales_engineer_wise = sales, distributor_wise = distributors, gift_summary = giftSummary } });
    }

    private async Task<List<ActivityRawRow>> Rows(ActivityReportFilter filter, CancellationToken ct)
    {
        var current = ulong.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!); var visible = await VisibleIds(current, ct); var visibleLong = visible.Select(x => checked((long)x)).ToArray();
        var query = _db.PromotionalActivities.AsNoTracking().Where(x => x.DeletedAt == null && x.Status == "submitted" && (visibleLong.Contains(x.CreatedById) || visibleLong.Contains(x.UserId)));
        if (filter.StartDate.HasValue) query = query.Where(x => x.ActivityDate >= filter.StartDate.Value.Date);
        if (filter.EndDate.HasValue) query = query.Where(x => x.ActivityDate < filter.EndDate.Value.Date.AddDays(1));
        if (filter.BranchId.HasValue) { var id = checked((long)filter.BranchId.Value); query = query.Where(x => x.BranchId == id); }
        if (!string.IsNullOrWhiteSpace(filter.Meet)) query = query.Where(x => x.ActivityType == filter.Meet.Trim().ToLower());
        var result = await (from a in query join user in _db.Users.AsNoTracking() on (ulong)a.UserId equals user.Id join creator in _db.Users.AsNoTracking() on (ulong)a.CreatedById equals creator.Id
            join division in _db.Divisions.AsNoTracking() on user.DivisionId equals (ulong?)division.Id into divisions from division in divisions.DefaultIfEmpty()
            join branch in _db.Branches.AsNoTracking() on (ulong?)a.BranchId equals (ulong?)branch.Id into branches from branch in branches.DefaultIfEmpty()
            select new ActivityRawRow(a.Id, division == null ? null : division.Id, division == null ? "Unassigned" : division.DivisionName, a.BranchId, branch == null ? "Unassigned" : branch.BranchName,
                a.DistributorId, a.DistributorName ?? "Unassigned", creator.Id, creator.Name, user.Id, user.Name, a.Participants.Count, a.GiftCount, a.TotalExpense)).ToListAsync(ct);
        return filter.ZoneId.HasValue ? result.Where(x => x.DivisionId == filter.ZoneId).ToList() : result;
    }

    private async Task<HashSet<ulong>> VisibleIds(ulong current, CancellationToken ct) {
        var all = await _db.Users.AsNoTracking().Where(x => x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null).Select(x => new { x.Id, x.ReportingId }).ToListAsync(ct);
        var admin = await _db.ModelHasRoles.AsNoTracking().Where(x => x.ModelId == current && x.ModelType == "App\\Models\\User").Join(_db.Roles.AsNoTracking(), x => x.RoleId, x => x.Id, (_, r) => r.Name).AnyAsync(x => x.ToLower().Contains("admin"), ct);
        if (admin) return all.Select(x => x.Id).ToHashSet(); var result = new HashSet<ulong> { current }; var frontier = new HashSet<ulong> { current };
        while (frontier.Count > 0) frontier = all.Where(x => x.ReportingId.HasValue && frontier.Contains(x.ReportingId.Value) && result.Add(x.Id)).Select(x => x.Id).ToHashSet(); return result;
    }

    private IActionResult Workbook(List<ActivityExportRow> rows, ActivityReportFilter filter, string kind, bool distributor) {
        using var book = new XLWorkbook(); var sheet = book.Worksheets.Add("Activity Report");
        var headers = distributor ? new[] { "Sr. No", "Zone", "Branch", "Distributor Name", "Sales Engineer", "ASR / DSR Name", "No. of Meets", "Participation Count", "Gift Count", "Expenses Total" } : new[] { "Sr. No", "Zone", "Branch", "Sales Engineer", "ASR / DSR Name", "No. of Meets", "Participation Count", "Gift Count", "Expenses Total" };
        const int header = 1; for (var i = 0; i < headers.Length; i++) sheet.Cell(header, i + 1).Value = headers[i]; var output = header + 1; var serial = 1;
        foreach (var zone in rows.GroupBy(x => x.Zone)) { foreach (var row in zone) { object?[] values = distributor ? [serial++, row.Zone, row.Branch, row.Distributor, row.SalesEngineer, row.AsrName, row.Meets, row.Participants, row.Gifts, row.Expense] : [serial++, row.Zone, row.Branch, row.SalesEngineer, row.AsrName, row.Meets, row.Participants, row.Gifts, row.Expense]; for (var i = 0; i < values.Length; i++) sheet.Cell(output, i + 1).Value = XLCellValue.FromObject(values[i]); output++; } Total(sheet, output++, $"{zone.Key} ZONE TOTAL", zone, headers.Length, XLColor.FromHtml("FFF2CC")); }
        Total(sheet, output, "GRAND TOTAL", rows, headers.Length, XLColor.FromHtml("1F4E78"), true); Style(sheet, header, output, headers.Length); return Excel(book, $"{MeetFileName(filter.Meet)}_Activity_{kind.Replace(" ", "_")}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
    }
    private static string MeetLabel(string? meet) => string.IsNullOrWhiteSpace(meet) ? "All Meets" : meet.ToLower() switch { "retailer" => "Retailer Meet", "nukkad" => "Nukkad Meet", "farmer" => "Farmer Meet / Demo", "influencer" => "Influencer Meet", _ => meet };
    private static bool ValidMeet(string? meet) => meet is not null && new[] { "retailer", "nukkad", "farmer", "influencer" }.Contains(meet.Trim().ToLower());
    private static string MeetFileName(string? meet) => MeetLabel(meet).Replace(" / ", "_").Replace(" ", "_");
    private static void Total(IXLWorksheet s, int row, string label, IEnumerable<ActivityExportRow> source, int columns, XLColor color, bool white = false) { var values = source.ToList(); s.Cell(row, 1).Value = label; s.Range(row, 1, row, columns - 4).Merge(); s.Cell(row, columns - 3).Value = values.Sum(x => x.Meets); s.Cell(row, columns - 2).Value = values.Sum(x => x.Participants); s.Cell(row, columns - 1).Value = values.Sum(x => x.Gifts); s.Cell(row, columns).Value = values.Sum(x => x.Expense); FormatTotal(s.Range(row, 1, row, columns), color, white); }
    private static void GiftTotal(IXLWorksheet s, int row, string label, List<string> names, Dictionary<string, int> totals, XLColor color, bool white = false) { s.Cell(row, 1).Value = label; s.Range(row, 1, row, 3).Merge(); for (var i = 0; i < names.Count; i++) s.Cell(row, i + 4).Value = totals.GetValueOrDefault(names[i]); s.Cell(row, names.Count + 4).Value = totals.Values.Sum(); FormatTotal(s.Range(row, 1, row, names.Count + 4), color, white); }
    private static void FormatTotal(IXLRange range, XLColor color, bool white) { range.Style.Fill.BackgroundColor = color; range.Style.Font.Bold = true; if (white) range.Style.Font.FontColor = XLColor.White; }
    private static void Style(IXLWorksheet s, int header, int last, int columns) { var h = s.Range(header, 1, header, columns); h.Style.Fill.BackgroundColor = XLColor.FromHtml("87CEEB"); h.Style.Font.Bold = true; h.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; h.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center; var used = s.Range(header, 1, last, columns); used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin; used.Style.Border.InsideBorder = XLBorderStyleValues.Thin; used.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center; s.Column(columns).Style.NumberFormat.Format = "#,##0.00"; s.SheetView.FreezeRows(header); s.Columns(1, columns).AdjustToContents(); for (var column = 1; column <= columns; column++) s.Column(column).Width += 3; s.Row(header).Height = 24; }
    private IActionResult Excel(XLWorkbook book, string name) { using var stream = new MemoryStream(); book.SaveAs(stream); return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name); }
}

public sealed class ActivityReportFilter { public DateTime? StartDate { get; set; } public DateTime? EndDate { get; set; } public ulong? ZoneId { get; set; } public ulong? BranchId { get; set; } public string? Meet { get; set; } }
public sealed record ActivityRawRow(long Id, ulong? DivisionId, string Zone, long? BranchId, string Branch, long? DistributorId, string DistributorName, ulong CreatorId, string CreatorName, ulong UserId, string UserName, int Participants, int GiftCount, decimal TotalExpense);
public sealed record ActivityExportRow(string Zone, string Branch, string? Distributor, string SalesEngineer, string AsrName, int Meets, int Participants, int Gifts, decimal Expense);
public sealed record GiftEntry(long ActivityId, string Name);
