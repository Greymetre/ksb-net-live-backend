using System.Security.Claims;
using Application.Interfaces.Repositories;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api/attendance")]
public sealed class MobileAttendanceSummaryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;

    public MobileAttendanceSummaryController(AppDbContext db, IHrRepository hr) { _db = db; _hr = hr; }

    [HttpGet("today-summary")]
    public async Task<IActionResult> TodaySummary(CancellationToken ct)
    {
        var actorId = CurrentUserId();
        var visible = (await _hr.GetVisibleUserIdsAsync(actorId, ct)).Distinct().ToArray();
        var visibleWithActor = visible.Append(actorId).Distinct().ToArray();
        var now = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var today = now.Date;
        var tomorrow = today.AddDays(1);
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var yearStart = new DateTime(now.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);

        // Dashboard always includes the authenticated user. A field user with
        // no reporting team must still see their own attendance and metrics.
        var teamUsers = await _db.Users.AsNoTracking().Where(x => visibleWithActor.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted)
            .Select(x => new { x.Id, x.DesignationId }).ToListAsync(ct);
        var effectiveTeamIds = teamUsers.Select(x => x.Id).ToArray();
        var asrIds = teamUsers.Where(x => x.DesignationId == 3).Select(x => x.Id).ToArray();
        var dsrIds = teamUsers.Where(x => x.DesignationId == 6).Select(x => x.Id).ToArray();

        var todayAttendance = await _db.Attendances.AsNoTracking()
            .Where(x => x.UserId.HasValue && effectiveTeamIds.Contains(x.UserId.Value) && x.PunchinDate >= today && x.PunchinDate < tomorrow)
            .Select(x => new TodayAttendanceRow(x.UserId!.Value, x.PunchoutTime, x.WorkingType)).ToListAsync(ct);
        var checkedInIds = todayAttendance.Select(x => x.UserId).Distinct().ToHashSet();

        var asrTargets = await Target(asrIds, now, ct);
        var dsrTargets = await Target(dsrIds, now, ct);
        var relevantAsr = asrIds.Append(actorId).Distinct().ToArray();
        var todayAsrOrders = await OrderTotal(relevantAsr, today, tomorrow, ct);
        var monthAsrOrders = await OrderTotal(relevantAsr, monthStart, monthEnd, ct);
        var todayDsrOrders = await OrderTotal(dsrIds, today, tomorrow, ct);
        var monthDsrOrders = await OrderTotal(dsrIds, monthStart, monthEnd, ct);
        var yearOrders = await OrderTotal(visibleWithActor, yearStart, yearEnd, ct);

        var topYearQty = await TopProducts(visibleWithActor, yearStart, yearEnd, false, ct);
        var topMonthQty = await TopProducts(visibleWithActor, monthStart, monthEnd, false, ct);
        var topYearValue = await TopProducts(visibleWithActor, yearStart, yearEnd, true, ct);
        var topMonthValue = await TopProducts(visibleWithActor, monthStart, monthEnd, true, ct);

        var approvedBase = _db.Customers.AsNoTracking().Where(x => x.DeletedAt == null && x.Active == "Y" && x.CreatedBy.HasValue && visibleWithActor.Contains(x.CreatedBy.Value)
            && x.CustomFields != null && EF.Functions.Like(x.CustomFields, "%\"status\":\"approved\"%"));
        var approvedToday = await approvedBase.CountAsync(x => x.CreatedAt >= today && x.CreatedAt < tomorrow, ct);
        var approvedYear = await approvedBase.CountAsync(x => x.CreatedAt >= yearStart && x.CreatedAt < yearEnd, ct);
        var orderedBuyerIds = await _db.Orders.AsNoTracking().Where(x => x.OrderDate >= yearStart && x.OrderDate < yearEnd && x.BuyerId.HasValue)
            .Select(x => x.BuyerId!.Value).Distinct().ToListAsync(ct);
        var secondaryWithOrder = await _db.Customers.AsNoTracking().CountAsync(x => x.CreatedBy.HasValue && visibleWithActor.Contains(x.CreatedBy.Value) && orderedBuyerIds.Contains(x.Id), ct);

        var data = new Dictionary<string, object?>
        {
            ["total_users"] = effectiveTeamIds.Length,
            ["total_punch_in"] = checkedInIds.Count,
            ["total_not_punch_in"] = Math.Max(0, effectiveTeamIds.Length - checkedInIds.Count),
            ["leave_asr_today"] = LeaveTotal(todayAttendance, asrIds),
            ["leave_dsr_today"] = LeaveTotal(todayAttendance, dsrIds),
            ["asr"] = AttendanceGroup(asrIds, checkedInIds),
            ["dsr"] = AttendanceGroup(dsrIds, checkedInIds),
            ["today_orders"] = OrderMetric(todayAsrOrders),
            ["current_month_orders"] = OrderMetric(monthAsrOrders),
            ["today_orders_dsr"] = OrderMetric(todayDsrOrders),
            ["current_month_orders_dsr"] = OrderMetric(monthDsrOrders),
            ["asr_target"] = asrTargets,
            ["dsr_target"] = dsrTargets,
            ["unique_buyers_from_asr"] = await UniqueBuyers(asrIds, monthStart, monthEnd, ct),
            ["unique_buyers_from_dsr"] = await UniqueBuyers(dsrIds, monthStart, monthEnd, ct),
            ["total_unique_buyers_current_year"] = await UniqueBuyers(visibleWithActor, yearStart, yearEnd, ct),
            ["punchout_remaining_asr_today"] = todayAttendance.Count(x => asrIds.Contains(x.UserId) && !x.PunchoutTime.HasValue),
            ["punchout_remaining_dsr_today"] = todayAttendance.Count(x => dsrIds.Contains(x.UserId) && !x.PunchoutTime.HasValue),
            ["secondary_customers_registered_approved_today"] = approvedToday,
            ["secondary_customers_registered_approved_current_year"] = approvedYear,
            ["secondary_customers_with_order_current_year"] = secondaryWithOrder,
            ["total_orders_current_year"] = yearOrders.Count,
            ["total_order_quantity_current_year"] = yearOrders.Quantity,
            ["total_order_value_current_year"] = yearOrders.Value,
            ["top_5_products"] = topYearQty,
            ["top_5_products_total"] = ProductTotal(topYearQty),
            ["top_5_products_current_month"] = topMonthQty,
            ["top_5_products_current_year"] = topYearQty,
            ["top_5_products_total_current_month"] = ProductTotal(topMonthQty),
            ["top_5_products_total_current_year"] = ProductTotal(topYearQty),
            ["top_5_products_value_wise"] = topYearValue,
            ["top_5_products_total_value_wise"] = ProductTotal(topYearValue),
            ["top_5_products_current_month_value_wise"] = topMonthValue,
            ["top_5_products_total_current_month_value_wise"] = ProductTotal(topMonthValue),
            ["working_type_asr_today"] = WorkingTypes(await AttendanceRange(asrIds, today, tomorrow, ct)),
            ["working_type_asr_current_month"] = WorkingTypes(await AttendanceRange(asrIds, monthStart, monthEnd, ct)),
            ["working_type_asr_current_year"] = WorkingTypes(await AttendanceRange(asrIds, yearStart, yearEnd, ct)),
            ["working_type_dsr_today"] = WorkingTypes(await AttendanceRange(dsrIds, today, tomorrow, ct)),
            ["working_type_dsr_current_month"] = WorkingTypes(await AttendanceRange(dsrIds, monthStart, monthEnd, ct)),
            ["working_type_dsr_current_year"] = WorkingTypes(await AttendanceRange(dsrIds, yearStart, yearEnd, ct))
        };

        return Ok(new { status = "success", message = effectiveTeamIds.Length == 0 ? "No team members found." : "Today's team attendance & order summary retrieved successfully.", data });
    }

    [AcceptVerbs("GET", "POST")]
    [HttpPost("changeStatus")]
    public async Task<IActionResult> ChangeStatus([FromBody] ChangeAttendanceStatusRequest request, CancellationToken ct)
    {
        var attendanceIds = request.AttendanceIds();
        if (string.IsNullOrWhiteSpace(attendanceIds) || !request.Status.HasValue)
            return BadRequest(new { status = "error", message = "The status and attendance_id fields are required." });
        if (request.Status == 2 && string.IsNullOrWhiteSpace(request.RemarkStatus))
            return BadRequest(new { status = "error", message = "If you want to reject the attendance please add a remark." });

        var ids = attendanceIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => ulong.TryParse(value, out var id) ? id : 0).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return BadRequest(new { status = "error", message = "attendance_id is invalid." });

        var visibleIds = (await _hr.GetVisibleUserIdsAsync(CurrentUserId(), ct)).Append(CurrentUserId()).Distinct().ToArray();
        var rows = await _db.Attendances.Where(x => ids.Contains(x.Id) && x.UserId.HasValue && visibleIds.Contains(x.UserId.Value)).ToListAsync(ct);
        if (rows.Count != ids.Length)
            return NotFound(new { status = "error", message = "One or more attendance records were not found or are not accessible." });

        var actor = CurrentUserId().ToString();
        foreach (var row in rows)
        {
            row.AttendanceStatus = request.Status;
            row.ApproveRejectBy = actor;
            row.RemarkStatus = request.RemarkStatus;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { status = "success", message = "Status changed successfully." });
    }

    private async Task<object> Target(ulong[] ids, DateTime now, CancellationToken ct)
    {
        var rows = await _db.SalesTargetUsers.AsNoTracking().Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value) && x.Type == "secondary" && x.Month == now.ToString("MMM") && x.Year == now.Year).ToListAsync(ct);
        var target = rows.Sum(x => x.Target ?? 0); var achievement = rows.Sum(x => x.Achievement ?? 0);
        var qtyTarget = rows.Sum(x => x.QuantityTarget ?? 0); var qtyAchievement = rows.Sum(x => x.QuantityAchievement ?? 0);
        return new { target, achievement, achievement_percent = target > 0 ? Math.Round(achievement / target * 100, 2) : 0, target_qty = qtyTarget, achievement_qty = qtyAchievement, quantity_achievement_percent = qtyTarget > 0 ? Math.Round(qtyAchievement / qtyTarget * 100, 2) : 0 };
    }

    private async Task<OrderAggregate> OrderTotal(ulong[] ids, DateTime start, DateTime end, CancellationToken ct)
    {
        var rows = await _db.Orders.AsNoTracking().Where(x => x.CreatedBy.HasValue && ids.Contains(x.CreatedBy.Value) && x.OrderDate >= start && x.OrderDate < end)
            .Select(x => new { x.TotalQty, x.GrandTotal }).ToListAsync(ct);
        return new OrderAggregate(rows.Count, rows.Sum(x => x.TotalQty), Math.Round(rows.Sum(x => x.GrandTotal), 2));
    }

    private async Task<int> UniqueBuyers(ulong[] ids, DateTime start, DateTime end, CancellationToken ct) =>
        await _db.Orders.AsNoTracking().Where(x => x.CreatedBy.HasValue && ids.Contains(x.CreatedBy.Value) && x.OrderDate >= start && x.OrderDate < end && x.BuyerId.HasValue).Select(x => x.BuyerId).Distinct().CountAsync(ct);

    private async Task<List<ProductRow>> TopProducts(ulong[] ids, DateTime start, DateTime end, bool valueWise, CancellationToken ct)
    {
        var groupedRows = await (from detail in _db.OrderDetails.AsNoTracking()
                    join order in _db.Orders.AsNoTracking() on detail.OrderId equals order.Id
                    join product in _db.Products.AsNoTracking() on detail.ProductId equals product.Id
                    where order.CreatedBy.HasValue && ids.Contains(order.CreatedBy.Value) && order.OrderDate >= start && order.OrderDate < end
                    group detail by new { detail.ProductId, product.ProductName } into grouped
                    select new { grouped.Key.ProductName, Quantity = grouped.Sum(x => x.Quantity), Value = grouped.Sum(x => x.LineTotal) }).ToListAsync(ct);
        var rows = groupedRows.Select(x => new ProductRow(x.ProductName, x.Quantity, Math.Round(x.Value, 2)));
        return (valueWise ? rows.OrderByDescending(x => x.Value) : rows.OrderByDescending(x => x.Quantity)).Take(5).ToList();
    }

    private async Task<List<string>> AttendanceRange(ulong[] ids, DateTime start, DateTime end, CancellationToken ct) =>
        await _db.Attendances.AsNoTracking().Where(x => x.UserId.HasValue && ids.Contains(x.UserId.Value) && x.PunchinDate >= start && x.PunchinDate < end && x.WorkingType != "").Select(x => x.WorkingType).ToListAsync(ct);

    private static object AttendanceGroup(ulong[] ids, HashSet<ulong> checkedIn) { var count = ids.Count(checkedIn.Contains); return new { total = ids.Length, checked_in_today = count, not_checked_in_today = Math.Max(0, ids.Length - count) }; }
    private static decimal LeaveTotal(IEnumerable<TodayAttendanceRow> rows, ulong[] ids) => rows.Where(x => ids.Contains(x.UserId)).Sum(x => x.WorkingType == "Full Day Leave" ? 1m : x.WorkingType is "First Half Leave" or "Second Half Leave" ? .5m : 0m);
    private static object WorkingTypes(IEnumerable<string> values) { var rows = values.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList(); var known = new[] { "Retailer Visit", "Retailer Meet", "Nukkad Meet", "Field Demo", "Full Day Leave", "First Half Leave", "Second Half Leave" }; return new { retailer_visit = rows.Count(x => x.Equals("Retailer Visit", StringComparison.OrdinalIgnoreCase)), retailer_meet = rows.Count(x => x.Equals("Retailer Meet", StringComparison.OrdinalIgnoreCase)), nukkad_meet = rows.Count(x => x.Equals("Nukkad Meet", StringComparison.OrdinalIgnoreCase)), field_demo = rows.Count(x => x.Equals("Field Demo", StringComparison.OrdinalIgnoreCase)), other = rows.Count(x => !known.Contains(x, StringComparer.OrdinalIgnoreCase)) }; }
    private static object ProductTotal(IEnumerable<ProductRow> rows) => new { quantity = rows.Sum(x => x.Quantity), value = Math.Round(rows.Sum(x => x.Value), 2) };
    private static object OrderMetric(OrderAggregate row) => new { quantity = row.Quantity, value = row.Value };
    private ulong CurrentUserId() => ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new UnauthorizedAccessException("Authenticated user id is missing.");

    private sealed record OrderAggregate(int Count, long Quantity, decimal Value);
    private sealed record ProductRow(string ProductName, long Quantity, decimal Value);
    private sealed record TodayAttendanceRow(ulong UserId, TimeSpan? PunchoutTime, string WorkingType);
}

public sealed class ChangeAttendanceStatusRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("attendance_id")]
    public JsonElement AttendanceId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public int? Status { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("remark_status")]
    public string? RemarkStatus { get; set; }

    public string? AttendanceIds() => AttendanceId.ValueKind switch
    {
        JsonValueKind.String => AttendanceId.GetString(),
        JsonValueKind.Number => AttendanceId.GetRawText(),
        JsonValueKind.Array => string.Join(',', AttendanceId.EnumerateArray()
            .Where(value => value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())
            .Where(value => !string.IsNullOrWhiteSpace(value))),
        _ => null
    };
}
