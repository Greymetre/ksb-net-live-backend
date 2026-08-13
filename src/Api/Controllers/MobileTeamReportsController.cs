using System.Data;
using System.Security.Claims;
using Application.Interfaces.Repositories;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class MobileTeamReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;

    public MobileTeamReportsController(AppDbContext db, IHrRepository hr) { _db = db; _hr = hr; }

    [HttpGet("today-attendance-zone")]
    public async Task<IActionResult> Attendance([FromQuery] TeamReportFilter filter, CancellationToken ct)
    {
        var users = await Team(filter, ct);
        var today = IndiaNow().Date;
        var ids = users.Select(x => x.Id).ToArray();
        var attendance = await _db.Attendances.AsNoTracking()
            .Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value) && x.PunchinDate >= today && x.PunchinDate < today.AddDays(1))
            .Select(x => new { UserId = x.UserId!.Value, x.WorkingType }).ToListAsync(ct);
        var byUser = attendance.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => x.First().WorkingType ?? "");

        var rows = users.Select(user =>
        {
            var punched = byUser.TryGetValue(user.Id, out var workingType);
            var leave = punched && IsLeave(workingType!);
            return new { user, punched, leave, working = punched && !leave };
        }).Where(x => filter.Status switch
        {
            "punch_in" => x.punched,
            "not_punch_in" => !x.punched,
            "leave" => x.leave,
            _ => true
        }).ToList();

        var zones = rows.GroupBy(x => x.user.Zone ?? "Unknown").OrderBy(x => x.Key).Select(zone => new
        {
            zone = zone.Key,
            users = zone.Select(x => new
            {
                id = x.user.Id, name = x.user.Name, branch = x.user.Branch ?? "N/A",
                reporting = new { id = x.user.ReportingId, name = x.user.ReportingName, mobile = x.user.ReportingMobile },
                punchin = x.punched, not_punchin = !x.punched, on_leave = x.leave, working = x.working
            })
        }).ToArray();
        var working = rows.Count(x => x.working);
        return Ok(new { success = true, message = "Today team attendance fetched successfully", data = new
        {
            zones,
            summary = new { total_users = rows.Count, total_punch_in = working, total_not_punch_in = rows.Count - working, total_on_leave = rows.Count(x => x.leave), total_working = working }
        }});
    }

    [HttpGet("sales/sales-summary")]
    public async Task<IActionResult> Sales([FromQuery] TeamReportFilter filter, CancellationToken ct)
    {
        var users = await Team(filter, ct);
        var ids = users.Select(x => x.Id).ToArray();
        var now = IndiaNow(); var today = now.Date; var tomorrow = today.AddDays(1); var month = new DateTime(now.Year, now.Month, 1); var nextMonth = month.AddMonths(1);
        var orders = await _db.Orders.AsNoTracking().Where(x => x.CreatedBy.HasValue && ids.Contains(x.CreatedBy.Value) && x.OrderDate >= month && x.OrderDate < nextMonth)
            .Select(x => new { UserId = x.CreatedBy!.Value, x.OrderDate, x.GrandTotal, x.TotalQty, x.BuyerId }).ToListAsync(ct);
        var targets = await _db.SalesTargetUsers.AsNoTracking().Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value) && x.Type == "secondary" && x.Month == now.ToString("MMM") && x.Year == now.Year)
            .GroupBy(x => x.UserId!.Value).Select(x => new { UserId = x.Key, Target = x.Sum(y => y.Target ?? 0), Qty = x.Sum(y => y.QuantityTarget ?? 0) }).ToListAsync(ct);
        var retailers = await RetailerCounts(ids, today, ct);
        var visits = await VisitCounts(ids, today, tomorrow, month, nextMonth, ct);
        var targetMap = targets.ToDictionary(x => x.UserId);

        var userRows = users.Select(user =>
        {
            var all = orders.Where(x => x.UserId == user.Id).ToList(); var current = all.Where(x => x.OrderDate >= today && x.OrderDate < tomorrow).ToList();
            targetMap.TryGetValue(user.Id, out var target); retailers.TryGetValue(user.Id, out var retailer); visits.TryGetValue(user.Id, out var visit);
            var targetValue = target?.Target ?? 0; var targetQty = target?.Qty ?? 0; var monthValue = all.Sum(x => x.GrandTotal); var monthQty = all.Sum(x => x.TotalQty);
            return new SalesRow(user, retailer.Total, targetValue, targetQty, current.Sum(x => x.GrandTotal), current.Sum(x => x.TotalQty), current.Count,
                monthValue, monthQty, all.Count, targetValue > 0 ? Math.Round(monthValue / targetValue * 100, 2) : 0,
                targetQty > 0 ? Math.Round(monthQty / targetQty * 100, 2) : 0, visit.Today, visit.Month, visit.Unique, all.Where(x => x.BuyerId.HasValue).Select(x => x.BuyerId).Distinct().Count());
        }).ToList();
        var zones = userRows.GroupBy(x => x.User.Zone ?? "Unknown").OrderBy(x => x.Key).Select(g => new { zone = g.Key, users = g.Select(SalesJson), totals = new { target = g.Sum(x => x.Target), month_value = g.Sum(x => x.MonthValue), today_value = g.Sum(x => x.TodayValue) } }).ToArray();
        return Ok(new { success = true, message = "Today team sales fetched successfully", data = new { zones, summary = new
        {
            total_users = userRows.Count, total_target = userRows.Sum(x => x.Target), total_target_qty = userRows.Sum(x => x.TargetQty), total_month_value = userRows.Sum(x => x.MonthValue),
            total_today_value = userRows.Sum(x => x.TodayValue), total_today_orders = userRows.Sum(x => x.TodayCount), total_month_orders = userRows.Sum(x => x.MonthCount),
            total_visits_today = userRows.Sum(x => x.TodayVisits), total_visits_month = userRows.Sum(x => x.MonthVisits), month_unique_retailer_visits = userRows.Sum(x => x.UniqueVisits), total_unique_retailers_month = userRows.Sum(x => x.UniqueBuyers)
        }}});
    }

    [HttpGet("sales/retailer-sales-summary")]
    public async Task<IActionResult> RetailerSales([FromQuery] TeamReportFilter filter, CancellationToken ct)
    {
        var users = await Team(filter, ct); var ids = users.Select(x => x.Id).ToArray(); var today = IndiaNow().Date; var year = new DateTime(today.Year, 1, 1);
        var orders = await _db.Orders.AsNoTracking().Where(x => x.ExecutiveId.HasValue && ids.Contains(x.ExecutiveId.Value) && x.OrderDate >= year && x.OrderDate < today.AddDays(1))
            .Select(x => new { UserId = x.ExecutiveId!.Value, x.Id, x.BuyerId, x.GrandTotal }).ToListAsync(ct);
        var orderIds = orders.Select(x => x.Id).ToArray();
        var quantities = await _db.OrderDetails.AsNoTracking().Where(x => x.OrderId.HasValue && orderIds.Contains(x.OrderId.Value)).GroupBy(x => x.OrderId!.Value).Select(x => new { Id = x.Key, Qty = x.Sum(y => y.Quantity) }).ToDictionaryAsync(x => x.Id, x => x.Qty, ct);
        var retailers = await RetailerCounts(ids, today, ct);
        var rows = users.Select(user => { var own = orders.Where(x => x.UserId == user.Id).ToList(); retailers.TryGetValue(user.Id, out var r); return new RetailerRow(user, r.Total, r.Today, own.Where(x => x.BuyerId.HasValue).Select(x => x.BuyerId).Distinct().Count(), own.Count, own.Sum(x => quantities.GetValueOrDefault(x.Id)), own.Sum(x => x.GrandTotal)); }).ToList();
        var zones = rows.GroupBy(x => x.User.Zone ?? "Unknown").OrderBy(x => x.Key).Select(g => new { zone = g.Key, users = g.Select(RetailerJson), totals = RetailerTotals(g) }).ToArray();
        return Ok(new { success = true, message = "Retailer sales summary fetched successfully", data = new { zones, summary = RetailerTotals(rows) } });
    }

    private async Task<List<TeamUser>> Team(TeamReportFilter filter, CancellationToken ct)
    {
        var actor = CurrentUserId(); var ids = (await _hr.GetVisibleUserIdsAsync(actor, ct)).Append(actor).Distinct().ToArray();
        var designation = filter.Designation?.ToLowerInvariant() switch { "asr" => 3UL, "dsr" => 6UL, _ => 0UL };
        var raw = await (from user in _db.Users.AsNoTracking() join manager in _db.Users.AsNoTracking() on user.ReportingId equals manager.Id into managers from manager in managers.DefaultIfEmpty()
            join division in _db.Divisions.AsNoTracking() on user.DivisionId equals division.Id into divisions from division in divisions.DefaultIfEmpty()
            where ids.Contains(user.Id) && user.Active == "Y" && !user.IsDeleted && (designation == 0 || user.DesignationId == designation) && (!filter.UserId.HasValue || user.Id == filter.UserId)
            select new { user.Id, user.Name, user.ReportingId, ReportingName = manager == null ? null : manager.Name, ReportingMobile = manager == null ? null : manager.Mobile, Zone = division == null ? null : division.DivisionName, user.PrimaryBranchId, user.BranchId }).ToListAsync(ct);

        var branchIds = raw.Select(x => x.PrimaryBranchId ?? FirstId(x.BranchId)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var branchNames = await _db.Branches.AsNoTracking().Where(x => branchIds.Contains(x.Id) && x.DeletedAt == null).ToDictionaryAsync(x => x.Id, x => x.BranchName, ct);
        var team = raw.Select(x =>
        {
            var branchId = x.PrimaryBranchId ?? FirstId(x.BranchId);
            return new TeamUser(x.Id, x.Name, x.ReportingId, x.ReportingName, x.ReportingMobile, x.Zone, branchId.HasValue ? branchNames.GetValueOrDefault(branchId.Value) : null);
        }).ToList();
        return team.Where(x => (string.IsNullOrWhiteSpace(filter.Zone) || (x.Zone?.Contains(filter.Zone, StringComparison.OrdinalIgnoreCase) ?? false)) && (string.IsNullOrWhiteSpace(filter.Branch) || (x.Branch?.Contains(filter.Branch, StringComparison.OrdinalIgnoreCase) ?? false))).OrderBy(x => x.ReportingName).ThenBy(x => x.Name).ToList();
    }

    private static ulong? FirstId(string? value) => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => ulong.TryParse(x, out var id) ? (ulong?)id : null).FirstOrDefault(x => x.HasValue);

    private async Task<Dictionary<ulong, (int Total, int Today)>> RetailerCounts(ulong[] ids, DateTime today, CancellationToken ct)
    {
        var rows = await _db.Customers.AsNoTracking().Where(x => x.CreatedBy.HasValue && ids.Contains(x.CreatedBy.Value) && x.DeletedAt == null && x.Active == "Y" && (x.CustomFields == null || EF.Functions.Like(x.CustomFields, "%\"status\":\"approved\"%")))
            .Select(x => new { UserId = x.CreatedBy!.Value, x.CreatedAt }).ToListAsync(ct);
        return rows.GroupBy(x => x.UserId).ToDictionary(x => x.Key, x => (x.Count(), x.Count(y => y.CreatedAt >= today && y.CreatedAt < today.AddDays(1))));
    }

    private async Task<Dictionary<ulong, (int Today, int Month, int Unique)>> VisitCounts(ulong[] ids, DateTime today, DateTime tomorrow, DateTime month, DateTime nextMonth, CancellationToken ct)
    {
        var result = ids.ToDictionary(x => x, _ => (0, 0, 0)); if (ids.Length == 0) return result;
        await using var command = _db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT user_id, SUM(CASE WHEN checkin_date >= @today AND checkin_date < @tomorrow THEN 1 ELSE 0 END), COUNT(*), COUNT(DISTINCT entity_id) FROM check_in WHERE deleted_at IS NULL AND entity_type='secondary_customer' AND user_id IN ({string.Join(',', ids)}) AND checkin_date >= @month AND checkin_date < @nextMonth GROUP BY user_id";
        foreach (var p in new[] { ("@today", today), ("@tomorrow", tomorrow), ("@month", month), ("@nextMonth", nextMonth) }) { var parameter = command.CreateParameter(); parameter.ParameterName = p.Item1; parameter.Value = p.Item2; command.Parameters.Add(parameter); }
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) result[Convert.ToUInt64(reader.GetValue(0))] = (Convert.ToInt32(reader.GetValue(1)), Convert.ToInt32(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(3)));
        return result;
    }

    private static bool IsLeave(string value) => value.Contains("Full Day Leave", StringComparison.OrdinalIgnoreCase) || value.Contains("First Half Leave", StringComparison.OrdinalIgnoreCase) || value.Contains("Second Half Leave", StringComparison.OrdinalIgnoreCase);
    private static object SalesJson(SalesRow x) => new { id=x.User.Id,name=x.User.Name,branch=x.User.Branch??"N/A",reporting=new{id=x.User.ReportingId,name=x.User.ReportingName,mobile=x.User.ReportingMobile},registered_retailers=x.Retailers,target=x.Target,targetQty=x.TargetQty,today_order_value=x.TodayValue,today_order_qty=x.TodayQty,today_order_count=x.TodayCount,month_order_value=x.MonthValue,month_order_qty=x.MonthQty,month_order_count=x.MonthCount,achievement_percent=x.Achievement,achievement_percent_qty=x.QtyAchievement,today_visits=x.TodayVisits,month_visits=x.MonthVisits,month_unique_retailer_visits=x.UniqueVisits,unique_retailers_month=x.UniqueBuyers};
    private static object RetailerJson(RetailerRow x) => new { id=x.User.Id,name=x.User.Name,branch=x.User.Branch??"N/A",reporting=new{id=x.User.ReportingId,name=x.User.ReportingName,mobile=x.User.ReportingMobile},registered_retailers=x.Retailers,today_registered_retailers=x.TodayRetailers,unique_orders=x.UniqueOrders,total_orders=x.Orders,order_total_qty=(x.Quantity/1000m).ToString("0.00"),order_total_value=(int)Math.Round(x.Value/100000m) };
    private static object RetailerTotals(IEnumerable<RetailerRow> rows) => new { total_users=rows.Count(),total_registered_retailers=rows.Sum(x=>x.Retailers),total_today_registered_retailers=rows.Sum(x=>x.TodayRetailers),total_unique_orders=rows.Sum(x=>x.UniqueOrders),total_orders=rows.Sum(x=>x.Orders),total_order_qty=(rows.Sum(x=>x.Quantity)/1000m).ToString("0.00"),total_order_value=(int)Math.Round(rows.Sum(x=>x.Value)/100000m) };
    private static DateTime IndiaNow() => DateTime.UtcNow.AddHours(5).AddMinutes(30);
    private ulong CurrentUserId() => ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new UnauthorizedAccessException();
    private sealed record TeamUser(ulong Id,string Name,ulong? ReportingId,string? ReportingName,string? ReportingMobile,string? Zone,string? Branch);
    private sealed record SalesRow(TeamUser User,int Retailers,decimal Target,decimal TargetQty,decimal TodayValue,long TodayQty,int TodayCount,decimal MonthValue,long MonthQty,int MonthCount,decimal Achievement,decimal QtyAchievement,int TodayVisits,int MonthVisits,int UniqueVisits,int UniqueBuyers);
    private sealed record RetailerRow(TeamUser User,int Retailers,int TodayRetailers,int UniqueOrders,int Orders,long Quantity,decimal Value);
}

public sealed class TeamReportFilter
{
    [FromQuery(Name="designation")] public string? Designation { get; set; }
    [FromQuery(Name="branch")] public string? Branch { get; set; }
    [FromQuery(Name="zone")] public string? Zone { get; set; }
    [FromQuery(Name="user_id")] public ulong? UserId { get; set; }
    [FromQuery(Name="status")] public string? Status { get; set; }
}
