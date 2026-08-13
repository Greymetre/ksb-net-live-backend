using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Api.Filters;
using Application.Interfaces.Repositories;
using ClosedXML.Excel;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api/reports")]
public sealed class ReportManagementController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;
    public ReportManagementController(AppDbContext db, IHrRepository hr) { _db = db; _hr = hr; }

    [HttpGet("asr-performance/options")]
    [RequirePermission("ASR_report_Download")]
    public async Task<IActionResult> AsrPerformanceOptions(CancellationToken cancellationToken)
    {
        var actor = CurrentUserId();
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, cancellationToken)).Append(actor).Distinct().ToArray();
        var users = await _db.Users.AsNoTracking().Where(x => visibleIds.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null)
            .OrderBy(x => x.Name).Select(x => new { id = x.Id, name = x.Name }).ToListAsync(cancellationToken);
        var divisions = await _db.Divisions.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .OrderBy(x => x.DivisionName).Select(x => new { id = x.Id, name = x.DivisionName }).ToListAsync(cancellationToken);
        var branches = await _db.Branches.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .OrderBy(x => x.BranchName).Select(x => new { id = x.Id, name = x.BranchName }).ToListAsync(cancellationToken);
        var designations = await _db.Designations.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .OrderBy(x => x.DesignationName).Select(x => new { id = x.Id, name = x.DesignationName }).ToListAsync(cancellationToken);
        var defaultDesignationId = designations.FirstOrDefault(x => string.Equals(x.name.Trim(), "ASR", StringComparison.OrdinalIgnoreCase))?.id;
        return Ok(new { users, divisions, branches, designations, default_designation_id = defaultDesignationId });
    }

    [HttpGet("rating-report/options")]
    [RequirePermission("asm_rating_report")]
    public Task<IActionResult> RatingReportOptions(CancellationToken cancellationToken) => AsrPerformanceOptions(cancellationToken);

    [HttpGet("rating-report/export")]
    [RequirePermission("asm_rating_report")]
    public async Task<IActionResult> ExportRatingReport([FromQuery] RatingReportFilter filter, CancellationToken ct)
    {
        var isWeekly = string.Equals(filter.Period, "weekly", StringComparison.OrdinalIgnoreCase);
        if (!filter.DesignationId.HasValue) return BadRequest(new { status = false, message = "Designation is required." });
        if (!string.IsNullOrWhiteSpace(filter.Period) && !isWeekly) return BadRequest(new { status = false, message = "A valid period is required." });
        if (!isWeekly && filter.Year is < 2000 or > 2100) return BadRequest(new { status = false, message = "A valid year is required." });
        if (filter.Month.HasValue && filter.Month is < 1 or > 12) return BadRequest(new { status = false, message = "A valid month is required." });

        var actor = CurrentUserId();
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, ct)).Append(actor).Distinct().ToHashSet();
        var users = await _db.Users.AsNoTracking()
            .Where(x => visibleIds.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null && x.DesignationId == filter.DesignationId)
            .ToListAsync(ct);
        users = users.Where(x => (!filter.DivisionId.HasValue || x.DivisionId == filter.DivisionId)
            && (!filter.BranchId.HasValue || UserHasBranch(x, filter.BranchId.Value))).ToList();

        var userIds = users.Select(x => x.Id).ToArray();
        var divisions = await _db.Divisions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DivisionName, ct);
        var branches = await _db.Branches.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.BranchName, ct);
        var userNames = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        users = users.OrderBy(x => ZoneOrder(Name(divisions, x.DivisionId))).ThenBy(x => Name(divisions, x.DivisionId))
            .ThenBy(x => BranchName(x, branches)).ThenBy(x => x.Name).ToList();

        var indiaToday = DateTime.UtcNow.AddHours(5).AddMinutes(30).Date;
        var isYtd = !isWeekly && !filter.Month.HasValue;
        var start = isWeekly ? indiaToday.AddDays(-7) : new DateTime(filter.Year!.Value, filter.Month ?? 1, 1);
        var end = isWeekly ? indiaToday : filter.Month.HasValue ? start.AddMonths(1)
            : filter.Year == indiaToday.Year ? indiaToday.AddDays(1) : new DateTime(filter.Year!.Value + 1, 1, 1);
        var retailerAssignments = await RetailerAssignments(userIds, ct);
        var assignedRetailerIds = retailerAssignments.Values.SelectMany(x => x).Distinct().ToArray();
        var activeRetailerIds = assignedRetailerIds.Length == 0 ? new HashSet<ulong>() : (await _db.Orders.AsNoTracking()
            .Where(x => x.BuyerId.HasValue && assignedRetailerIds.Contains(x.BuyerId.Value) && x.DeletedAt == null)
            .Select(x => x.BuyerId!.Value).Distinct().ToListAsync(ct)).ToHashSet();

        const decimal marketWeight = 5m, visitWeight = 30m, salesWeight = 40m, promoWeight = 10m, retailerWeight = 15m;

        async Task<List<RatingReportRow>> CalculatePeriod(DateTime periodStart, DateTime periodEnd, bool weekly, bool ytd)
        {
            var monthCount = ((periodEnd.AddDays(-1).Year - periodStart.Year) * 12) + periodEnd.AddDays(-1).Month - periodStart.Month + 1;
            var monthStarts = Enumerable.Range(0, monthCount).Select(periodStart.AddMonths).Select(x => new DateTime(x.Year, x.Month, 1)).ToArray();
            var targetMonths = monthStarts.SelectMany(x => new[] { x.ToString("MMM", CultureInfo.InvariantCulture), x.ToString("MMMM", CultureInfo.InvariantCulture) }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var targetYears = monthStarts.Select(x => x.Year).Distinct().ToArray();
            var periodMonths = ytd ? monthCount : 1;
            var attendance = await _db.Attendances.AsNoTracking().Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value)
                && x.PunchinDate >= periodStart && x.PunchinDate < periodEnd && x.DeletedAt == null).Select(x => new { UserId = x.UserId!.Value, x.PunchinDate, x.WorkingType }).ToListAsync(ct);
            var targets = await _db.SalesTargetUsers.AsNoTracking().Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value)
                && x.Year.HasValue && targetYears.Contains(x.Year.Value) && x.Month != null && targetMonths.Contains(x.Month)).Select(x => new { UserId = x.UserId!.Value, x.Year, x.Month, x.Target }).ToListAsync(ct);
            var orders = await _db.Orders.AsNoTracking().Where(x => x.CreatedBy.HasValue && userIds.Contains(x.CreatedBy.Value)
                && x.OrderDate >= periodStart && x.OrderDate < periodEnd && x.DeletedAt == null).Select(x => new { UserId = x.CreatedBy!.Value, x.SubTotal }).ToListAsync(ct);
            var visits = await RatingVisitCounts(userIds, DateOnly.FromDateTime(periodStart), DateOnly.FromDateTime(periodEnd.AddDays(-1)), ct);

            return users.Select(user =>
            {
                var userAttendance = attendance.Where(x => x.UserId == user.Id).ToList();
                var marketDays = userAttendance.Where(x => !IsLeaveOrOffice(x.WorkingType)).Select(x => x.PunchinDate.Date).Distinct().Count();
                var promotional = userAttendance.Sum(x => PromotionalActivityCount(x.WorkingType));
                var customerVisits = visits.GetValueOrDefault(user.Id);
                var userTargets = targets.Where(x => x.UserId == user.Id).ToList();
                var target = weekly ? monthStarts.Sum(monthStart =>
                {
                    var segmentStart = periodStart > monthStart ? periodStart : monthStart;
                    var monthEnd = monthStart.AddMonths(1);
                    var segmentEnd = periodEnd < monthEnd ? periodEnd : monthEnd;
                    var days = Math.Max(0, (segmentEnd - segmentStart).Days);
                    var monthTarget = userTargets.Where(x => x.Year == monthStart.Year && (string.Equals(x.Month, monthStart.ToString("MMM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase) || string.Equals(x.Month, monthStart.ToString("MMMM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))).Sum(x => x.Target ?? 0m);
                    return monthTarget / DateTime.DaysInMonth(monthStart.Year, monthStart.Month) * days;
                }) : userTargets.Sum(x => x.Target ?? 0m);
                var orderValue = orders.Where(x => x.UserId == user.Id).Sum(x => x.SubTotal);
                var achievement = orderValue > 1m ? Math.Round((orderValue - orderValue / 100m) / 100000m, 2) : 0m;
                var assigned = retailerAssignments.GetValueOrDefault(user.Id, []);
                var active = assigned.Count(activeRetailerIds.Contains);
                var marketTarget = weekly ? 5 : 20 * periodMonths;
                var visitTarget = weekly ? 50 : 200 * periodMonths;
                var promotionalTarget = weekly ? 1 : 4 * periodMonths;
                var marketRatio = CappedRatio(marketDays, marketTarget); var visitRatio = CappedRatio(customerVisits, visitTarget);
                var salesRatio = CappedRatio(achievement, target); var promoRatio = CappedRatio(promotional, promotionalTarget);
                var activeRatio = assigned.Count == 0 ? 0m : (decimal)active / assigned.Count; var activeRatingRatio = Math.Min(activeRatio, .30m) / .30m;
                var final = marketWeight * marketRatio + visitWeight * visitRatio + salesWeight * salesRatio + promoWeight * promoRatio + retailerWeight * activeRatingRatio;
                return new RatingReportRow(user.Id, BranchName(user, branches), user.EmployeeCodes ?? string.Empty, user.Name, Name(userNames, user.ReportingId), Name(divisions, user.DivisionId), null, Math.Round(final, 2),
                    marketTarget, marketDays, marketRatio, marketWeight * marketRatio, visitTarget, customerVisits, visitRatio, visitWeight * visitRatio, Math.Round(target, 2), achievement, salesRatio, salesWeight * salesRatio,
                    promotional, promoRatio, promoWeight * promoRatio, promotionalTarget, assigned.Count, active, activeRatio, Math.Min(activeRatio, .30m), retailerWeight * activeRatingRatio);
            }).ToList();
        }

        var rows = await CalculatePeriod(start, end, isWeekly, isYtd);
        if (!isYtd)
        {
            var previousStart = isWeekly ? start.AddDays(-7) : start.AddMonths(-1);
            var previousEnd = start;
            var previous = (await CalculatePeriod(previousStart, previousEnd, isWeekly, false)).ToDictionary(x => x.UserId, x => x.FinalRating);
            rows = rows.Select(x => x with { LastFinalRating = previous.GetValueOrDefault(x.UserId) }).ToList();
        }
        rows = rows.OrderByDescending(x => x.FinalRating).ThenBy(x => x.EmployeeName).ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(isWeekly ? "ASR(Weekly)" : "ASR(Monthly)");
        BuildRatingSheet(sheet, rows, start, marketWeight, visitWeight, salesWeight, promoWeight, retailerWeight, !isYtd);
        if (isWeekly) { sheet.Cell("A1").Value = $"Weekly {start:dd-MMM-yyyy} to {end.AddDays(-1):dd-MMM-yyyy}"; sheet.Cell("A1").Style.DateFormat.Format = "General"; }
        else if (isYtd) { sheet.Cell("A1").Value = $"YTD {filter.Year}"; sheet.Cell("A1").Style.DateFormat.Format = "General"; }
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        var period = isWeekly ? $"Weekly_{start:yyyy-MM-dd}_to_{end.AddDays(-1):yyyy-MM-dd}" : isYtd ? $"YTD_{filter.Year}" : start.ToString("MMM_yyyy", CultureInfo.InvariantCulture);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Rating_Report_{period}.xlsx");
    }

    [HttpGet("productivity/options")]
    [RequirePermission("retailer_productivity_report")]
    public async Task<IActionResult> ProductivityOptions(CancellationToken cancellationToken)
    {
        var actor = CurrentUserId();
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, cancellationToken)).Append(actor).Distinct().ToArray();
        var users = await _db.Users.AsNoTracking().Where(x => visibleIds.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null)
            .OrderBy(x => x.Name).Select(x => new { id = x.Id, name = x.Name }).ToListAsync(cancellationToken);
        var divisions = await _db.Divisions.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null).OrderBy(x => x.DivisionName).Select(x => new { id = x.Id, name = x.DivisionName }).ToListAsync(cancellationToken);
        var branches = await _db.Branches.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null).OrderBy(x => x.BranchName).Select(x => new { id = x.Id, name = x.BranchName }).ToListAsync(cancellationToken);
        var designations = await _db.Designations.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null).OrderBy(x => x.DesignationName).Select(x => new { id = x.Id, name = x.DesignationName }).ToListAsync(cancellationToken);
        var states = await _db.States.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null).OrderBy(x => x.StateName).Select(x => new { id = x.Id, name = x.StateName }).ToListAsync(cancellationToken);
        var customers = await _db.Customers.AsNoTracking().Where(x => x.DeletedAt == null && (x.CustomerType == 1 || x.CustomerType == 2)).OrderBy(x => x.Name)
            .Select(x => new { id = x.Id, name = x.Name, type = x.CustomerType }).ToListAsync(cancellationToken);
        var asrId = designations.FirstOrDefault(x => string.Equals(x.name.Trim(), "ASR", StringComparison.OrdinalIgnoreCase))?.id;
        var dealerDefaults = designations.Where(x => string.Equals(x.name.Trim(), "ASR", StringComparison.OrdinalIgnoreCase) || string.Equals(x.name.Trim(), "DSR", StringComparison.OrdinalIgnoreCase)).Select(x => x.id).ToArray();
        return Ok(new { users, divisions, branches, designations, states, retailers = customers.Where(x => x.type == 2), dealers = customers.Where(x => x.type == 1), default_retailer_designation_ids = asrId.HasValue ? new[] { asrId.Value } : Array.Empty<ulong>(), default_dealer_designation_ids = dealerDefaults });
    }

    [HttpGet("retailer-performance/export")]
    [RequirePermission("retailer_productivity_report")]
    public async Task<IActionResult> ExportRetailerPerformance([FromQuery] ProductivityFilter filter, CancellationToken ct)
    {
        if (!filter.DivisionId.HasValue) return BadRequest(new { status = false, message = "Please select a zone before downloading the report." });
        var actor = CurrentUserId();
        var visible = (await _hr.GetVisibleUserIdsAsync(actor, ct)).Append(actor).Distinct().ToHashSet();
        var users = await _db.Users.AsNoTracking().Where(x => visible.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null).ToListAsync(ct);
        users = users.Where(x => (!filter.EmployeeId.HasValue || x.Id == filter.EmployeeId) && x.DivisionId == filter.DivisionId && (filter.DesignationIds.Length == 0 || (x.DesignationId.HasValue && filter.DesignationIds.Contains(x.DesignationId.Value)))).ToList();
        var userIds = users.Select(x => x.Id).ToArray();
        var retailers = await _db.Customers.AsNoTracking().Where(x => x.DeletedAt == null && x.CustomerType == 2 && (!filter.RetailerId.HasValue || x.Id == filter.RetailerId)).ToListAsync(ct);
        retailers = retailers.Where(x => (x.ExecutiveId.HasValue && userIds.Contains(x.ExecutiveId.Value)) || userIds.Contains(x.CreatedBy ?? 0)).ToList();
        if (filter.StateId.HasValue) retailers = retailers.Where(x => JsonULong(x.CustomFields, "state_id") == filter.StateId).ToList();
        if (filter.DealerId.HasValue) retailers = retailers.Where(x => JsonULong(x.CustomFields, "distributor_name") == filter.DealerId).ToList();
        var retailerIds = retailers.Select(x => x.Id).ToArray();
        var year = filter.Year ?? DateTime.UtcNow.Year;
        var orders = await _db.Orders.AsNoTracking().Where(x => x.BuyerId.HasValue && retailerIds.Contains(x.BuyerId.Value) && x.OrderDate.HasValue && x.OrderDate.Value.Year == year && x.DeletedAt == null)
            .Select(x => new PerformanceOrder(x.BuyerId, x.SellerId, x.ExecutiveId, x.CreatedBy, x.OrderDate, (long?)x.TotalQty ?? 0, (decimal?)x.GrandTotal ?? 0)).ToListAsync(ct);
        var cities = await _db.Cities.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.CityName, ct);
        var districts = await _db.Districts.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DistrictName, ct);
        var states = await _db.States.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.StateName, ct);
        var dealers = await _db.Customers.AsNoTracking().Where(x => x.CustomerType == 1 && x.DeletedAt == null).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var userMap = users.ToDictionary(x => x.Id);
        var allUserNames = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("Retailer Productivity");
        var headers = new List<string> { "Employee Name", "Customer Name", "City", "District", "State", "Distributor Name", "Reporting Manager" };
        for (var month = 1; month <= 12; month++) { headers.Add(CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month) + "-Qty"); headers.Add(CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month) + "-Value"); }
        headers.AddRange(["YTD Order Qty", "YTD Order Value", "Customer Id", "Employee Code", "Distributor Id"]);
        for (var i = 0; i < headers.Count; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var rowNumber = 2;
        foreach (var customer in retailers.OrderByDescending(x => x.CreatedAt))
        {
            var employeeId = customer.ExecutiveId ?? customer.CreatedBy; userMap.TryGetValue(employeeId ?? 0, out var employee);
            var customerOrders = orders.Where(x => x.BuyerId == customer.Id).ToList(); var dealerId = JsonULong(customer.CustomFields, "distributor_name");
            var values = new List<object?> { employee?.Name ?? "-", customer.Name, Name(cities, JsonULong(customer.CustomFields, "city_id")), Name(districts, JsonULong(customer.CustomFields, "district_id")), Name(states, JsonULong(customer.CustomFields, "state_id")), Name(dealers, dealerId), Name(allUserNames, employee?.ReportingId) };
            for (var month = 1; month <= 12; month++) { var monthOrders = customerOrders.Where(x => x.OrderDate!.Value.Month == month); values.Add(monthOrders.Sum(x => x.TotalQty)); values.Add(monthOrders.Sum(x => x.GrandTotal)); }
            values.Add(customerOrders.Sum(x => x.TotalQty)); values.Add(customerOrders.Sum(x => x.GrandTotal)); values.Add(customer.Id); values.Add(employee?.EmployeeCodes ?? "-"); values.Add(dealerId ?? 0);
            WriteRow(sheet, rowNumber++, values);
        }
        StyleSimpleExport(sheet, headers.Count, rowNumber - 1, "D9E1F2");
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Retailer_Productivity_Report_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }

    [HttpGet("dealer-performance/export")]
    [RequirePermission("retailer_productivity_report")]
    public async Task<IActionResult> ExportDealerPerformance([FromQuery] ProductivityFilter filter, CancellationToken ct)
    {
        var actor = CurrentUserId(); var visible = (await _hr.GetVisibleUserIdsAsync(actor, ct)).Append(actor).Distinct().ToHashSet();
        var users = await _db.Users.AsNoTracking().Where(x => visible.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null).ToListAsync(ct);
        users = users.Where(x => (!filter.EmployeeId.HasValue || x.Id == filter.EmployeeId) && (!filter.DivisionId.HasValue || x.DivisionId == filter.DivisionId) && (!filter.BranchId.HasValue || UserHasBranch(x, filter.BranchId.Value)) && (filter.DesignationIds.Length == 0 || (x.DesignationId.HasValue && filter.DesignationIds.Contains(x.DesignationId.Value)))).ToList();
        var userIds = users.Select(x => x.Id).ToArray(); var assignments = await DealerAssignments(userIds, ct);
        var dealerIds = assignments.Keys.ToArray();
        var dealers = await _db.Customers.AsNoTracking().Where(x => x.CustomerType == 1 && x.DeletedAt == null && dealerIds.Contains(x.Id) && (!filter.DealerId.HasValue || x.Id == filter.DealerId)).ToListAsync(ct);
        var year = filter.Year ?? DateTime.UtcNow.Year; var selectedDealerIds = dealers.Select(x => x.Id).ToArray();
        var orders = await _db.Orders.AsNoTracking().Where(x => x.SellerId.HasValue && selectedDealerIds.Contains(x.SellerId.Value) && x.OrderDate.HasValue && x.OrderDate.Value.Year == year && x.DeletedAt == null)
            .Select(x => new PerformanceOrder(x.BuyerId, x.SellerId, x.ExecutiveId, x.CreatedBy, x.OrderDate, (long?)x.TotalQty ?? 0, (decimal?)x.GrandTotal ?? 0)).ToListAsync(ct);
        var branches = await _db.Branches.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.BranchName, ct); var divisions = await _db.Divisions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DivisionName, ct); var designations = await _db.Designations.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DesignationName, ct); var allNames = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct); var cities = await _db.Cities.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.CityName, ct);
        var prepared = dealers.SelectMany(dealer => assignments.GetValueOrDefault(dealer.Id, [])
            .Where(userIds.Contains)
            .Distinct()
            .Select(userId => users.FirstOrDefault(x => x.Id == userId))
            .Where(user => user is not null)
            .Cast<Domain.Entities.User>()
            .Select(user =>
            {
                var monthly = Enumerable.Range(1, 12).ToDictionary(month => month, month => orders
                    .Where(order => order.SellerId == dealer.Id && order.CreatedBy == user.Id && order.OrderDate!.Value.Month == month)
                    .Sum(order => order.GrandTotal));
                return new DealerPerformanceRow(dealer, user, monthly, Name(divisions, user.DivisionId), BranchName(user, branches), Name(allNames, user.ReportingId));
            }))
            .OrderBy(x => ZoneOrder(x.Zone)).ThenBy(x => x.Zone).ThenBy(x => x.Branch).ThenBy(x => x.Dealer.Name).ThenBy(x => x.User.Name).ToList();
        using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("Distributor Productivity");
        var headers = new[] { "Distributor Code", "Distributor Name", "Distributor Location", "Employees Code", "Designation", "Employees Name", "Reporting Manager", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec", "Total" };
        for (var m = 1; m <= 12; m++) sheet.Cell(1, 7 + m).Value = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(m); sheet.Cell(1, 20).Value = "Total";
        for (var i = 0; i < 7; i++) sheet.Cell(2, i + 1).Value = headers[i]; for (var i = 7; i < headers.Length; i++) sheet.Cell(2, i + 1).Value = "Secondary Val (Lac)";
        var outputRow = 3;
        foreach (var zone in prepared.GroupBy(x => x.Zone)) { foreach (var branch in zone.GroupBy(x => x.Branch)) { foreach (var item in branch) { var cityId = JsonULong(item.Dealer.CustomFields, "billing_city") ?? JsonULong(item.Dealer.CustomFields, "city_id"); var vals = new List<object?> { item.Dealer.CustomerCode, item.Dealer.Name, Name(cities, cityId), item.User.EmployeeCodes, Name(designations, item.User.DesignationId), item.User.Name, item.Reporting }; vals.AddRange(item.Monthly.Values.Select(value => (object?)ToLac(value))); vals.Add(ToLac(item.Monthly.Values.Sum())); WriteRow(sheet, outputRow++, vals); } WriteDealerTotal(sheet, outputRow++, branch.Key + " Total", branch.SelectMany(x => x.Monthly).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Sum(y => y.Value)), XLColor.FromHtml("FFF59D"), false); } WriteDealerTotal(sheet, outputRow++, zone.Key + " Total", zone.SelectMany(x => x.Monthly).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Sum(y => y.Value)), XLColor.FromHtml("004A88"), true); }
        WriteDealerTotal(sheet, outputRow++, "Grand Total", prepared.SelectMany(x => x.Monthly).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Sum(y => y.Value)), XLColor.FromHtml("43A047"), true);
        StyleSimpleExport(sheet, 20, outputRow - 1, "1E88E5", 2); sheet.Range(3, 8, outputRow - 1, 20).Style.NumberFormat.Format = "0.00"; sheet.SheetView.FreezeRows(2);
        using var stream = new MemoryStream(); workbook.SaveAs(stream); return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Distributors_Productivity_Report_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }

    [HttpGet("asr-performance/export")]
    [RequirePermission("ASR_report_Download")]
    public async Task<IActionResult> ExportAsrPerformance([FromQuery] AsrPerformanceFilter filter, CancellationToken cancellationToken)
    {
        if (filter.StartDate == default || filter.EndDate == default)
            return BadRequest(new { status = false, message = "Start date and end date are required." });
        if (filter.StartDate > filter.EndDate)
            return BadRequest(new { status = false, message = "Start date cannot be after end date." });
        if (filter.DesignationId is null)
            return BadRequest(new { status = false, message = "Designation is required." });

        var actor = CurrentUserId();
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, cancellationToken)).Append(actor).Distinct().ToHashSet();
        var allUsers = await _db.Users.AsNoTracking().Where(x => visibleIds.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null)
            .ToListAsync(cancellationToken);
        var users = allUsers.Where(x => (!filter.EmployeeId.HasValue || x.Id == filter.EmployeeId)
                && (!filter.DivisionId.HasValue || x.DivisionId == filter.DivisionId)
                && (!filter.DesignationId.HasValue || x.DesignationId == filter.DesignationId)
                && (!filter.BranchId.HasValue || UserHasBranch(x, filter.BranchId.Value)))
            .ToList();

        var userIds = users.Select(x => x.Id).ToArray();
        var divisions = await _db.Divisions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DivisionName, cancellationToken);
        var branches = await _db.Branches.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
        var designations = await _db.Designations.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DesignationName, cancellationToken);
        var userNames = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        users = users.OrderBy(x => ZoneOrder(Name(divisions, x.DivisionId))).ThenBy(x => Name(divisions, x.DivisionId))
            .ThenBy(x => BranchName(x, branches)).ThenBy(x => x.Name).ToList();
        var rangeStart = filter.StartDate.ToDateTime(TimeOnly.MinValue);
        var rangeEndExclusive = filter.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var orders = await _db.Orders.AsNoTracking().Where(x => x.ExecutiveId.HasValue && userIds.Contains(x.ExecutiveId.Value)
                && x.OrderDate >= rangeStart && x.OrderDate < rangeEndExclusive && x.DeletedAt == null).ToListAsync(cancellationToken);
        var orderIds = orders.Select(x => x.Id).ToArray();
        // OrderDetailConfiguration intentionally ignores BaseEntity.DeletedAt because
        // the deployed order_details mapping does not expose it to EF.
        var details = await _db.OrderDetails.AsNoTracking().Where(x => x.OrderId.HasValue && orderIds.Contains(x.OrderId.Value))
            .Select(x => new { x.OrderId, x.ProductId }).ToListAsync(cancellationToken);
        var attendances = await _db.Attendances.AsNoTracking().Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value)
                && x.PunchinDate >= rangeStart && x.PunchinDate < rangeEndExclusive && x.DeletedAt == null).ToListAsync(cancellationToken);
        var customers = await _db.Customers.AsNoTracking().Where(x => x.DeletedAt == null).Select(x => new { x.Id, x.CreatedBy, x.ExecutiveId, x.CreatedAt }).ToListAsync(cancellationToken);

        var visitCounts = await VisitCounts(userIds, filter.StartDate, filter.EndDate, cancellationToken);
        var assignedCounts = await AssignedCustomerCounts(userIds, cancellationToken);
        var rows = users.Select(user =>
        {
            var userOrders = orders.Where(x => x.ExecutiveId == user.Id).ToList();
            var ids = userOrders.Select(x => x.Id).ToHashSet();
            var workingDays = attendances.Where(x => x.UserId == user.Id && !string.Equals(x.WorkingType, "Full Day Leave", StringComparison.OrdinalIgnoreCase)).Select(x => x.PunchinDate.Date).Distinct().Count();
            var visited = visitCounts.GetValueOrDefault(user.Id);
            var productive = userOrders.Count;
            var target = workingDays * 15;
            var newCounters = customers.Count(x => x.CreatedBy == user.Id && x.CreatedAt >= rangeStart && x.CreatedAt < rangeEndExclusive);
            return new AsrPerformanceRow(user.Id, user.Name, 15, workingDays, target, visited,
                target > 0 ? Math.Round(visited * 100m / target, 1) : 0, productive,
                visited > 0 ? Math.Round(productive * 100m / visited, 1) : 0, newCounters,
                userOrders.Sum(x => x.TotalQty), userOrders.Sum(x => x.GrandTotal),
                details.Where(x => x.OrderId.HasValue && ids.Contains(x.OrderId.Value) && x.ProductId.HasValue).Select(x => x.ProductId).Distinct().Count(),
                assignedCounts.GetValueOrDefault(user.Id, customers.Count(x => x.ExecutiveId == user.Id)),
                Name(divisions, user.DivisionId), BranchName(user, branches), Name(designations, user.DesignationId), Name(userNames, user.ReportingId));
        }).ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("ASR Performance");
        var headers = new[] { "Employees Code", "Employees Name", "Daily Visit Target", "Total Working Days", "Total Visit Target", "Total Customers Visited", "Adherance %", "Total Productive Visits", "Productivity %", "New Counter Added", "Total Order Qty", "Total Order Value", "Unique SKU Ordered", "Total Cumulative Counter", "ZONE", "Branch", "Designation", "Reporting Manager" };
        for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];
        var outputRow = 2;
        foreach (var zoneGroup in rows.GroupBy(x => x.Zone))
        {
            foreach (var branchGroup in zoneGroup.GroupBy(x => x.Branch))
            {
                foreach (var row in branchGroup) WriteRow(sheet, outputRow++, row.Values());
                WriteTotal(sheet, outputRow++, "SUBTOTAL - " + branchGroup.Key, branchGroup.ToList(), XLColor.Yellow);
            }
            WriteTotal(sheet, outputRow++, "ZONE TOTAL - " + zoneGroup.Key, zoneGroup.ToList(), XLColor.FromHtml("E53935"));
        }
        WriteTotal(sheet, outputRow++, "GRAND TOTAL", rows, XLColor.FromHtml("43A047"));
        var headerRange = sheet.Range(1, 1, 1, headers.Length);
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("1E88E5");
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        sheet.Row(1).Height = 25;
        var usedRange = sheet.RangeUsed();
        if (usedRange is not null)
        {
            usedRange.Style.Font.FontName = "Calibri";
            usedRange.Style.Font.FontSize = 9;
            if (outputRow > 2)
            {
                var dataRange = sheet.Range(2, 3, outputRow - 1, headers.Length);
                dataRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                dataRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            }
        }
        sheet.SheetView.FreezeRows(1); sheet.Columns().AdjustToContents(8, 45);
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Asr_performance_report.xlsx");
    }

    [HttpGet("market-intelligence")]
    [RequirePermission("market_intelligence_access")]
    public async Task<IActionResult> MarketIntelligence([FromQuery] string? search, CancellationToken cancellationToken)
    {
        var actor = CurrentUserId();
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, cancellationToken)).Append(actor).Distinct().ToArray();
        if (visibleIds.Length == 0) return Ok(new { status = true, data = Array.Empty<object>(), total = 0, summary = new { total_records = 0 } });

        var rows = await Query(@$"SELECT TOP 500 s.id, s.title, s.division_id, d.division_name, s.created_by,
u.name AS created_by_name, s.created_at, sd.[key] AS field_key, sd.[value] AS field_value
FROM market_intelligence_serveys s
LEFT JOIN market_intelligence_servey_data sd ON sd.servey_id = s.id
LEFT JOIN users u ON u.id = s.created_by
LEFT JOIN divisions d ON d.id = s.division_id
WHERE s.created_by IN ({string.Join(',', visibleIds)})
ORDER BY s.created_at DESC, s.id DESC, sd.id ASC", cancellationToken);

        var data = rows.GroupBy(row => ULong(row, "id")).Select(group =>
        {
            var first = group.First();
            var item = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ULong(first, "id"), ["title"] = Str(first, "title"),
                ["zone"] = Str(first, "division_name"), ["created_by"] = Str(first, "created_by_name"),
                ["created_at"] = Obj(first, "created_at")
            };
            foreach (var field in group)
            {
                var key = Str(field, "field_key");
                if (!string.IsNullOrWhiteSpace(key)) item[key] = Str(field, "field_value");
            }
            return item;
        }).Where(item => string.IsNullOrWhiteSpace(search) || item.Values.Any(value => Convert.ToString(value, CultureInfo.InvariantCulture)?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)).ToList();

        return Ok(new { status = true, message = data.Count > 0 ? "Data retrieved successfully." : "No Record Found.", data, total = data.Count, summary = new { total_records = data.Count } });
    }

    private async Task<List<Dictionary<string, object?>>> Query(string sql, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
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
    private ulong CurrentUserId() => ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new UnauthorizedAccessException();
    private static object? Obj(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not DBNull ? value : null;
    private static string Str(IReadOnlyDictionary<string, object?> row, string key) => Convert.ToString(Obj(row, key), CultureInfo.InvariantCulture) ?? string.Empty;
    private static ulong ULong(IReadOnlyDictionary<string, object?> row, string key) => Obj(row, key) is null ? 0 : Convert.ToUInt64(Obj(row, key), CultureInfo.InvariantCulture);

    private async Task<Dictionary<ulong, int>> VisitCounts(ulong[] userIds, DateOnly start, DateOnly end, CancellationToken ct)
    {
        if (userIds.Length == 0) return [];
        var rows = await Query($"SELECT CAST(user_id AS bigint) user_id, COUNT(DISTINCT COALESCE(entity_id, customer_id)) visit_count FROM check_in WHERE deleted_at IS NULL AND checkin_date >= '{start:yyyy-MM-dd}' AND checkin_date <= '{end:yyyy-MM-dd}' AND user_id IN ({string.Join(',', userIds)}) GROUP BY user_id", ct);
        return rows.ToDictionary(x => ULong(x, "user_id"), x => Convert.ToInt32(Obj(x, "visit_count"), CultureInfo.InvariantCulture));
    }

    private async Task<Dictionary<ulong, int>> AssignedCustomerCounts(ulong[] userIds, CancellationToken ct)
    {
        if (userIds.Length == 0) return [];
        var rows = await Query($@"SELECT user_id, COUNT(DISTINCT customer_id) customer_count FROM (
SELECT CAST(executive_id AS bigint) user_id, CAST(id AS bigint) customer_id FROM customers WHERE deleted_at IS NULL AND executive_id IN ({string.Join(',', userIds)})
UNION SELECT CAST(user_id AS bigint), CAST(customer_id AS bigint) FROM employee_details WHERE deleted_at IS NULL AND user_id IN ({string.Join(',', userIds)})
) assignments GROUP BY user_id", ct);
        return rows.ToDictionary(x => ULong(x, "user_id"), x => Convert.ToInt32(Obj(x, "customer_count"), CultureInfo.InvariantCulture));
    }

    private async Task<Dictionary<ulong, int>> RatingVisitCounts(ulong[] userIds, DateOnly start, DateOnly end, CancellationToken ct)
    {
        if (userIds.Length == 0) return [];
        var rows = await Query($@"SELECT CAST(user_id AS bigint) user_id, COUNT_BIG(*) visit_count
FROM check_in WHERE deleted_at IS NULL AND checkin_date >= '{start:yyyy-MM-dd}' AND checkin_date <= '{end:yyyy-MM-dd}'
AND user_id IN ({string.Join(',', userIds)}) GROUP BY user_id", ct);
        return rows.ToDictionary(x => ULong(x, "user_id"), x => Convert.ToInt32(Obj(x, "visit_count"), CultureInfo.InvariantCulture));
    }

    private async Task<Dictionary<ulong, List<ulong>>> RetailerAssignments(ulong[] userIds, CancellationToken ct)
    {
        if (userIds.Length == 0) return [];
        var ids = string.Join(',', userIds);
        var rows = await Query($@"SELECT CAST(user_id AS bigint) user_id, CAST(customer_id AS bigint) customer_id FROM (
SELECT executive_id user_id, id customer_id FROM customers
WHERE deleted_at IS NULL AND customertype = 2 AND executive_id IN ({ids})
UNION
SELECT ed.user_id, ed.customer_id FROM employee_details ed
INNER JOIN customers c ON c.id = ed.customer_id AND c.deleted_at IS NULL AND c.customertype = 2
WHERE ed.deleted_at IS NULL AND ed.active = 'Y' AND ed.user_id IN ({ids})
) assigned", ct);
        return rows.GroupBy(x => ULong(x, "user_id")).ToDictionary(x => x.Key,
            x => x.Select(y => ULong(y, "customer_id")).Where(id => id > 0).Distinct().ToList());
    }

    private static bool IsLeaveOrOffice(string? workingType)
    {
        var values = (workingType ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Length == 0 || values.All(x => x.Equals("Office Work", StringComparison.OrdinalIgnoreCase)
            || x.Equals("Office Meeting", StringComparison.OrdinalIgnoreCase) || x.Equals("Full Day Leave", StringComparison.OrdinalIgnoreCase)
            || x.Equals("Leave", StringComparison.OrdinalIgnoreCase) || x.Equals("Holiday", StringComparison.OrdinalIgnoreCase));
    }

    private static int PromotionalActivityCount(string? workingType) => (workingType ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Count(x => x.Equals("Retailer Meet", StringComparison.OrdinalIgnoreCase)
            || x.Equals("Nukkad Meet", StringComparison.OrdinalIgnoreCase) || x.Equals("Field Demo", StringComparison.OrdinalIgnoreCase));

    private static decimal CappedRatio(decimal achievement, decimal target) => target <= 0 ? 0m : Math.Min(achievement / target, 1m);

    private static void BuildRatingSheet(IXLWorksheet sheet, IReadOnlyList<RatingReportRow> rows, DateTime month,
        decimal marketWeight, decimal visitWeight, decimal salesWeight, decimal promoWeight, decimal retailerWeight, bool includeLastRating)
    {
        var shift = includeLastRating ? 1 : 0;
        var lastColumn = 31 + shift;
        int C(int original) => original + (original >= 7 ? shift : 0);
        sheet.Cell("A1").Value = month;
        sheet.Cell("A1").Style.DateFormat.Format = "mmm-yy";
        sheet.Range(1, 1, 1, 6 + shift).Merge();
        sheet.Cell(1, C(7)).Value = "Number of days per Month dedicated to market visits"; sheet.Range(1, C(7), 1, C(11)).Merge();
        sheet.Cell(1, C(12)).Value = "All Customer Visit"; sheet.Range(1, C(12), 1, C(16)).Merge();
        sheet.Cell(1, C(17)).Value = "Target Vs Ach"; sheet.Range(1, C(17), 1, C(21)).Merge();
        sheet.Cell(1, C(22)).Value = "Promotional Activity"; sheet.Range(1, C(22), 1, C(26)).Merge();
        sheet.Cell(1, C(27)).Value = "Active Retailer"; sheet.Range(1, C(27), 1, C(31)).Merge();

        sheet.Cell("A2").Value = "Target & Weightage"; sheet.Range("A2:E2").Merge();
        sheet.Cell(2, 6 + shift).Value = 100;
        sheet.Cell(2, C(7)).Value = marketWeight; sheet.Cell(2, C(8)).Value = "Above 100% achievement will be considered 100%"; sheet.Range(2, C(8), 2, C(11)).Merge();
        sheet.Cell(2, C(12)).Value = visitWeight; sheet.Cell(2, C(13)).Value = "Above 100% achievement will be considered 100%"; sheet.Range(2, C(13), 2, C(16)).Merge();
        sheet.Cell(2, C(17)).Value = "Target"; sheet.Cell(2, C(18)).Value = "Ach"; sheet.Cell(2, C(19)).Value = salesWeight;
        sheet.Cell(2, C(20)).Value = "Above 100% achievement will be considered 100%"; sheet.Range(2, C(20), 2, C(21)).Merge();
        sheet.Cell(2, C(22)).Value = promoWeight; sheet.Cell(2, C(23)).Value = "Above 100% achievement will be considered 100%"; sheet.Range(2, C(23), 2, C(26)).Merge();
        sheet.Cell(2, C(27)).Value = retailerWeight; sheet.Cell(2, C(28)).Value = "Above 30% achievement will be considered 30%"; sheet.Range(2, C(28), 2, C(31)).Merge();

        var headers = new List<string> { "Branch", "Emp Code", "ASR", "DOJ", "Zone" };
        if (includeLastRating) headers.Add("L Final Rating");
        headers.AddRange(["C Final Rating", "Tgt", "Ach", "% ACHD", "For Rating %", "Final Rating", "TGT", "Ach", "% ACHD", "For Rating %", "Final Rating", "TGT", "Ach", "% ACHD", "For Rating %", "Final Rating", "Tgt", "Ach", "% ACHD", "For Rating %", "Final Rating", "Total Registred Retailer", "Active", "Active %", "For Rating %", "Final Rating"]);
        for (var i = 0; i < headers.Count; i++) sheet.Cell(3, i + 1).Value = headers[i];

        var outputRow = 4;
        foreach (var item in rows)
        {
            WriteRow(sheet, outputRow, item.Values(includeLastRating));
            foreach (var column in new[] { 6, 7, C(11), C(16), C(17), C(18), C(21), C(26), C(31) }.Where(x => x <= lastColumn)) sheet.Cell(outputRow, column).Style.NumberFormat.Format = "0.00";
            sheet.Cell(outputRow, C(9)).Style.NumberFormat.Format = "0.00%"; sheet.Cell(outputRow, C(10)).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(outputRow, C(14)).Style.NumberFormat.Format = "0.00%"; sheet.Cell(outputRow, C(15)).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(outputRow, C(19)).Style.NumberFormat.Format = "0.00%"; sheet.Cell(outputRow, C(20)).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(outputRow, C(24)).Style.NumberFormat.Format = "0.00%"; sheet.Cell(outputRow, C(25)).Style.NumberFormat.Format = "0.00%";
            sheet.Cell(outputRow, C(29)).Style.NumberFormat.Format = "0.00%"; sheet.Cell(outputRow, C(30)).Style.NumberFormat.Format = "0.00%";
            if (includeLastRating) ApplyRatingColor(sheet.Cell(outputRow, 6), item.LastFinalRating ?? 0m);
            ApplyRatingColor(sheet.Cell(outputRow, 6 + shift), item.FinalRating);
            outputRow++;
        }

        var header = sheet.Range(1, 1, 3, lastColumn); header.Style.Font.Bold = true; header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center; header.Style.Alignment.WrapText = true;
        sheet.Range(1, 1, 1, lastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("87CEEB");
        sheet.Range(2, 1, 2, lastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("D9E1F2");
        sheet.Range(3, 1, 3, lastColumn).Style.Fill.BackgroundColor = XLColor.FromHtml("1E88E5"); sheet.Range(3, 1, 3, lastColumn).Style.Font.FontColor = XLColor.White;
        var used = sheet.Range(1, 1, Math.Max(3, outputRow - 1), lastColumn); used.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin; used.Style.Font.FontName = "Calibri"; used.Style.Font.FontSize = 9;
        sheet.Row(1).Height = 32; sheet.Row(2).Height = 30; sheet.Row(3).Height = 30; sheet.SheetView.FreezeRows(3);
        sheet.Columns().AdjustToContents(8, 38);
    }

    private static XLColor RatingColor(decimal rating) => rating >= 85m ? XLColor.FromHtml("43A047")
        : rating >= 65m ? XLColor.FromHtml("FFF59D") : rating >= 35m ? XLColor.FromHtml("F8BBD0") : XLColor.FromHtml("E53935");

    private static void ApplyRatingColor(IXLCell cell, decimal rating)
    {
        cell.Style.Fill.BackgroundColor = RatingColor(rating);
        if (rating >= 85m || rating < 35m) cell.Style.Font.FontColor = XLColor.White;
    }

    private async Task<Dictionary<ulong, List<ulong>>> DealerAssignments(ulong[] visibleUserIds, CancellationToken ct)
    {
        if (visibleUserIds.Length == 0) return [];
        var rows = await Query($@"SELECT CAST(customer_id AS bigint) customer_id, CAST(user_id AS bigint) user_id
FROM employee_details WHERE deleted_at IS NULL AND active = 'Y' AND user_id IN ({string.Join(',', visibleUserIds)})
UNION SELECT CAST(id AS bigint), CAST(executive_id AS bigint) FROM customers
WHERE customertype = 1 AND deleted_at IS NULL AND executive_id IN ({string.Join(',', visibleUserIds)})", ct);
        return rows.GroupBy(x => ULong(x, "customer_id")).ToDictionary(x => x.Key, x => x.Select(y => ULong(y, "user_id")).Where(id => id > 0).Distinct().ToList());
    }

    private static ulong? JsonULong(string? json, string key) => ulong.TryParse(JsonString(json, key), out var value) ? value : null;
    private static string JsonString(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try { using var document = JsonDocument.Parse(json); return document.RootElement.TryGetProperty(key, out var value) ? value.ToString() : string.Empty; }
        catch (JsonException) { return string.Empty; }
    }
    private static void StyleSimpleExport(IXLWorksheet sheet, int columns, int lastRow, string headerColor, int headerRows = 1)
    {
        var header = sheet.Range(1, 1, headerRows, columns); header.Style.Fill.BackgroundColor = XLColor.FromHtml(headerColor); header.Style.Font.Bold = true;
        if (!string.Equals(headerColor, "D9E1F2", StringComparison.OrdinalIgnoreCase)) header.Style.Font.FontColor = XLColor.White;
        header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        if (lastRow > 0) { var used = sheet.Range(1, 1, lastRow, columns); used.Style.Border.InsideBorder = XLBorderStyleValues.Thin; used.Style.Border.OutsideBorder = XLBorderStyleValues.Thin; used.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center; used.Style.Font.FontName = "Calibri"; used.Style.Font.FontSize = 9; }
        sheet.SheetView.FreezeRows(headerRows); sheet.Columns().AdjustToContents(8, 45);
    }
    private static void WriteDealerTotal(IXLWorksheet sheet, int row, string label, IReadOnlyDictionary<int, decimal> monthly, XLColor color, bool white)
    {
        var values = new List<object?> { "", label, "", "", "", "", "" }; values.AddRange(Enumerable.Range(1, 12).Select(month => (object?)ToLac(monthly.GetValueOrDefault(month)))); values.Add(ToLac(monthly.Values.Sum())); WriteRow(sheet, row, values);
        var range = sheet.Range(row, 1, row, 20); range.Style.Fill.BackgroundColor = color; range.Style.Font.Bold = true; if (white) range.Style.Font.FontColor = XLColor.White;
    }

    private static decimal ToLac(decimal value) => Math.Round(value / 100000m, 2, MidpointRounding.AwayFromZero);

    private static bool UserHasBranch(Domain.Entities.User user, ulong branchId) => user.PrimaryBranchId == branchId ||
        (user.BranchId ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(x => ulong.TryParse(x, out var id) && id == branchId);
    private static string BranchName(Domain.Entities.User user, IReadOnlyDictionary<ulong, string> branches)
    {
        if (user.PrimaryBranchId.HasValue && branches.TryGetValue(user.PrimaryBranchId.Value, out var primary)) return primary;
        var first = (user.BranchId ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return ulong.TryParse(first, out var id) && branches.TryGetValue(id, out var name) ? name : string.Empty;
    }
    private static string Name(IReadOnlyDictionary<ulong, string> values, ulong? id) => id.HasValue && values.TryGetValue(id.Value, out var value) ? value : string.Empty;
    private static int ZoneOrder(string zone) => zone.Trim().ToLowerInvariant() switch { "north" or "norrth" => 1, "east" => 2, "west" => 3, "south" => 4, _ => 99 };
    private static void WriteRow(IXLWorksheet sheet, int row, IReadOnlyList<object?> values) { for (var i = 0; i < values.Count; i++) sheet.Cell(row, i + 1).Value = XLCellValue.FromObject(values[i]); }
    private static void WriteTotal(IXLWorksheet sheet, int row, string label, IReadOnlyCollection<AsrPerformanceRow> rows, XLColor color)
    {
        var working = rows.Sum(x => x.WorkingDays); var target = rows.Sum(x => x.VisitTarget); var visited = rows.Sum(x => x.Visited); var productive = rows.Sum(x => x.Productive);
        WriteRow(sheet, row, new object?[] { "", label, "", working, target, visited, target > 0 ? $"{Math.Round(visited * 100m / target, 1)} %" : "0 %", productive, visited > 0 ? $"{Math.Round(productive * 100m / visited, 1)} %" : "0 %", rows.Sum(x => x.NewCounters), rows.Sum(x => x.OrderQty), rows.Sum(x => x.OrderValue), rows.Sum(x => x.UniqueSku), rows.Sum(x => x.Cumulative), "", "", "", "" });
        var range = sheet.Range(row, 1, row, 18); range.Style.Fill.BackgroundColor = color; range.Style.Font.Bold = true;
        if (color != XLColor.Yellow) range.Style.Font.FontColor = XLColor.White;
    }
}

public sealed class AsrPerformanceFilter
{
    [FromQuery(Name = "employee_id")] public ulong? EmployeeId { get; set; }
    [FromQuery(Name = "division_id")] public ulong? DivisionId { get; set; }
    [FromQuery(Name = "branch_id")] public ulong? BranchId { get; set; }
    [FromQuery(Name = "designation_id")] public ulong? DesignationId { get; set; }
    [FromQuery(Name = "start_date")] public DateOnly StartDate { get; set; }
    [FromQuery(Name = "end_date")] public DateOnly EndDate { get; set; }
}

public sealed class ProductivityFilter
{
    [FromQuery(Name = "employee_id")] public ulong? EmployeeId { get; set; }
    [FromQuery(Name = "retailer_id")] public ulong? RetailerId { get; set; }
    [FromQuery(Name = "dealer_id")] public ulong? DealerId { get; set; }
    [FromQuery(Name = "distributor_id")] public ulong? DistributorId { get => DealerId; set => DealerId = value; }
    [FromQuery(Name = "year")] public int? Year { get; set; }
    [FromQuery(Name = "division_id")] public ulong? DivisionId { get; set; }
    [FromQuery(Name = "zone_id")] public ulong? ZoneId { get => DivisionId; set => DivisionId = value; }
    [FromQuery(Name = "branch_id")] public ulong? BranchId { get; set; }
    [FromQuery(Name = "state_id")] public ulong? StateId { get; set; }
    [FromQuery(Name = "designation_id")] public ulong[] DesignationIds { get; set; } = [];
}

public sealed class RatingReportFilter
{
    [FromQuery(Name = "designation_id")] public ulong? DesignationId { get; set; }
    [FromQuery(Name = "year")] public int? Year { get; set; }
    [FromQuery(Name = "month")] public int? Month { get; set; }
    [FromQuery(Name = "period")] public string? Period { get; set; }
    [FromQuery(Name = "division_id")] public ulong? DivisionId { get; set; }
    [FromQuery(Name = "zone_id")] public ulong? ZoneId { get => DivisionId; set => DivisionId = value; }
    [FromQuery(Name = "branch_id")] public ulong? BranchId { get; set; }
}

internal sealed record RatingReportRow(ulong UserId, string Branch, string EmployeeCode, string EmployeeName, string ReportingManager, string Zone,
    decimal? LastFinalRating, decimal FinalRating, int MarketTarget, int MarketDays, decimal MarketRatio, decimal MarketRating, int VisitTarget, int Visits, decimal VisitRatio, decimal VisitRating,
    decimal SalesTarget, decimal SalesAchievement, decimal SalesRatio, decimal SalesRating, int Promotional, decimal PromotionalRatio,
    decimal PromotionalRating, int PromotionalTarget, int RegisteredRetailers, int ActiveRetailers, decimal ActiveRatio, decimal ActiveRatingRatio, decimal ActiveRating)
{
    public object?[] Values(bool includeLastRating)
    {
        var values = new List<object?> { Branch, EmployeeCode, EmployeeName, ReportingManager, Zone };
        if (includeLastRating) values.Add(LastFinalRating ?? 0m);
        values.AddRange([FinalRating, MarketTarget, MarketDays, MarketRatio, MarketRatio, MarketRating, VisitTarget, Visits, VisitRatio, VisitRatio, VisitRating,
        SalesTarget, SalesAchievement, SalesRatio, SalesRatio, SalesRating, PromotionalTarget, Promotional, PromotionalRatio, PromotionalRatio,
        PromotionalRating, RegisteredRetailers, ActiveRetailers, ActiveRatio, ActiveRatingRatio, ActiveRating]);
        return values.ToArray();
    }
}

public sealed record DealerPerformanceRow(Domain.Entities.Customer Dealer, Domain.Entities.User User, IReadOnlyDictionary<int, decimal> Monthly, string Zone, string Branch, string Reporting);
public sealed record PerformanceOrder(ulong? BuyerId, ulong? SellerId, ulong? ExecutiveId, ulong? CreatedBy, DateTime? OrderDate, long TotalQty, decimal GrandTotal);

public sealed record AsrPerformanceRow(ulong UserId, string UserName, int DailyTarget, int WorkingDays, int VisitTarget, int Visited, decimal Adherence, int Productive, decimal Productivity, int NewCounters, long OrderQty, decimal OrderValue, int UniqueSku, int Cumulative, string Zone, string Branch, string Designation, string ReportingManager)
{
    public object?[] Values() => [UserId, UserName, DailyTarget, WorkingDays, VisitTarget, Visited, $"{Adherence} %", Productive, $"{Productivity} %", NewCounters, OrderQty, OrderValue, UniqueSku, Cumulative, Zone, Branch, Designation, ReportingManager];
}
