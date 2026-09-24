using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Api.Filters;
using Application.Interfaces.Repositories;
using ClosedXML.Excel;
using Infrastructure.Data;
using Infrastructure.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Domain.Services;
using Shared.Json;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api/reports")]
public sealed class ReportManagementController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;
    public ReportManagementController(AppDbContext db, IHrRepository hr) { _db = db; _hr = hr; }

    // Filter dropdowns only ever return id/name lists (users already limited to the
    // caller's own hierarchy), so they stay ungated - the report data itself does not.
    [HttpGet("asr-performance/options")]
    public async Task<IActionResult> AsrPerformanceOptions(CancellationToken cancellationToken)
    {
        var actor = CurrentUserId();
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, cancellationToken)).Distinct().ToArray();
        var users = await _db.Users.AsNoTracking().Where(x => visibleIds.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null)
            .OrderBy(x => x.Name).Select(x => new { id = x.Id, name = x.Name }).ToListAsync(cancellationToken);
        var divisions = (await _db.Divisions.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .Select(x => new { id = x.Id, name = x.DivisionName }).ToListAsync(cancellationToken)).ByZone(x => x.name).ToList();
        var branches = await BranchOptionRowsAsync(cancellationToken);
        var designations = await _db.Designations.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .OrderBy(x => x.DesignationName).Select(x => new { id = x.Id, name = x.DesignationName }).ToListAsync(cancellationToken);
        var defaultDesignationId = designations.FirstOrDefault(x => string.Equals(x.name.Trim(), "ASR", StringComparison.OrdinalIgnoreCase))?.id;
        return Ok(new { users, divisions, branches, designations, default_designation_id = defaultDesignationId });
    }

    /// <summary>The branch dropdown, each branch with its zone so a report filtered on a
    /// zone offers only that zone's branches.</summary>
    private async Task<object> BranchOptionRowsAsync(CancellationToken cancellationToken)
    {
        var zones = await _db.BranchZoneMapAsync(cancellationToken);
        return (await _db.Branches.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
                .OrderBy(x => x.BranchName).Select(x => new { id = x.Id, name = x.BranchName }).ToListAsync(cancellationToken))
            .Select(x => new { x.id, x.name, zone_id = zones.TryGetValue(x.id, out var zoneId) ? zoneId : (ulong?)null })
            .ToList();
    }

    // Loyalty > Performance Report. Dropdown feeds only, ungated like the other report
    // options above; each of its two downloads carries a permission of its own.
    // Segments come from the product Segment master (Domestic, Agriculture, ...), zones in
    // NEWS order, and only published schemes that have not been deleted - no invoice is
    // raised under any other, and a deleted scheme is gone from the Scheme Creation list too.
    [HttpGet("loyalty-performance/options")]
    public async Task<IActionResult> LoyaltyPerformanceOptions(CancellationToken cancellationToken)
    {
        var segments = await _db.ProductCategories.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .OrderBy(x => x.Ranking).ThenBy(x => x.CategoryName)
            .Select(x => new { id = x.Id, name = x.CategoryName }).ToListAsync(cancellationToken);
        var zones = (await _db.Divisions.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .Select(x => new { id = x.Id, name = x.DivisionName }).ToListAsync(cancellationToken)).ByZone(x => x.name).ToList();
        var schemes = await _db.LoyaltySchemes.AsNoTracking().Where(x => x.Status == "Published" && x.DeletedAt == null)
            .OrderByDescending(x => x.StartDate).ThenBy(x => x.SchemeName)
            .Select(x => new { id = x.Id, name = x.SchemeName, code = x.SchemeCode, start_date = x.StartDate, end_date = x.EndDate })
            .ToListAsync(cancellationToken);
        return Ok(new { segments, zones, schemes });
    }

    [HttpGet("rating-report/options")]
    public Task<IActionResult> RatingReportOptions(CancellationToken cancellationToken) => AsrPerformanceOptions(cancellationToken);

    [HttpGet("rating-report/export")]
    [RequirePermission("rating_report.export")]
    public async Task<IActionResult> ExportRatingReport([FromQuery] RatingReportFilter filter, CancellationToken ct)
    {
        var validation = ValidateRatingReportFilter(filter);
        if (validation is not null) return BadRequest(new { status = false, message = validation });
        var report = await CalculateRatingReport(filter, ct);
        var (rows, start, end, isWeekly, isYtd) = (report.Rows, report.Start, report.End, report.IsWeekly, report.IsYtd);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(isWeekly ? "ASR(Weekly)" : "ASR(Monthly)");
        BuildRatingSheet(sheet, rows, start, report.MarketWeight, report.VisitWeight, report.SalesWeight, report.PromoWeight, report.RetailerWeight, !isYtd);
        if (isWeekly) { sheet.Cell("A1").Value = $"Weekly {start:dd-MMM-yyyy} to {end.AddDays(-1):dd-MMM-yyyy}"; sheet.Cell("A1").Style.DateFormat.Format = "General"; }
        else if (isYtd) { sheet.Cell("A1").Value = $"YTD {filter.Year}"; sheet.Cell("A1").Style.DateFormat.Format = "General"; }
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        var period = isWeekly ? $"Weekly_{start:yyyy-MM-dd}_to_{end.AddDays(-1):yyyy-MM-dd}" : isYtd ? $"YTD_{filter.Year}" : start.ToString("MMM_yyyy", CultureInfo.InvariantCulture);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Rating_Report_{period}.xlsx");
    }

    [HttpGet("rating-report/dashboard")]
    [RequirePermission("rating_report.view")]
    public async Task<IActionResult> RatingReportDashboard([FromQuery] RatingReportFilter filter, CancellationToken ct)
    {
        if (!filter.DesignationId.HasValue) return BadRequest(new { status = false, message = "Designation is required." });

        var actor = CurrentUserId();
        // An employee switched off since still worked the months being rated, so they keep
        // their row; the Employee Status filter narrows to one or the other.
        var employeeStatus = Domain.Services.EmployeeStatus.Read(filter.EmployeeStatus);
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, includeInactive: true, ct)).Distinct().ToHashSet();
        var users = await _db.Users.AsNoTracking()
            .Where(x => visibleIds.Contains(x.Id) && !x.IsDeleted && x.DeletedAt == null && x.DesignationId == filter.DesignationId)
            .ToListAsync(ct);
        users = users.Where(x => (!filter.DivisionId.HasValue || x.DivisionId == filter.DivisionId)
            && (!filter.BranchId.HasValue || UserHasBranch(x, filter.BranchId.Value))
            && Domain.Services.EmployeeStatus.Matches(employeeStatus, x.Active)).ToList();

        var userIds = users.Select(x => x.Id).ToArray();
        var userDetailJoiningDates = (await _db.UserDetails.AsNoTracking()
            .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value) && x.DeletedAt == null && x.DateOfJoining.HasValue)
            .Select(x => new { UserId = x.UserId!.Value, x.DateOfJoining, x.Id })
            .ToListAsync(ct))
            .GroupBy(x => x.UserId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(item => item.Id).Select(item => item.DateOfJoining).FirstOrDefault());
        var divisions = await _db.Divisions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DivisionName, ct);
        var branches = await _db.Branches.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.BranchName, ct);
        var userNames = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        users = users.OrderBy(x => ZoneOrder.Rank(Name(divisions, x.DivisionId))).ThenBy(x => Name(divisions, x.DivisionId))
            .ThenBy(x => BranchName(x, branches)).ThenBy(x => x.Name).ToList();

        var indiaToday = DateTime.UtcNow.AddHours(5).AddMinutes(30).Date;
        var currentMonth = new DateTime(indiaToday.Year, indiaToday.Month, 1);
        var monthStarts = Enumerable.Range(0, 6).Select(index => currentMonth.AddMonths(index - 5)).ToArray();
        // The month in progress is only part done, so scoring it against a whole month's
        // targets makes every employee look like a failure until the month closes. It is
        // measured against the share of the targets its elapsed days have earned, the same
        // way the weekly report prorates a month target across its seven days.
        var daysInCurrentMonth = DateTime.DaysInMonth(indiaToday.Year, indiaToday.Month);
        var currentMonthShare = (decimal)indiaToday.Day / daysInCurrentMonth;
        var rangeStart = monthStarts[0];
        var rangeEnd = currentMonth.AddMonths(1);
        var targetYears = monthStarts.Select(x => x.Year).Distinct().ToArray();
        var targetMonths = monthStarts.SelectMany(x => new[] { x.ToString("MMM", CultureInfo.InvariantCulture), x.ToString("MMMM", CultureInfo.InvariantCulture) })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var attendance = await _db.Attendances.AsNoTracking().Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value)
            && x.PunchinDate >= rangeStart && x.PunchinDate < rangeEnd && x.DeletedAt == null)
            .Select(x => new { UserId = x.UserId!.Value, x.PunchinDate, x.WorkingType }).ToListAsync(ct);
        var promotionalActivities = await Api.Services.PromotionalActivityCounts.LoadAsync(_db, userIds, rangeStart, rangeEnd, ct);
        var targets = await _db.SalesTargetUsers.AsNoTracking().Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value)
            && x.Year.HasValue && targetYears.Contains(x.Year.Value) && x.Month != null && targetMonths.Contains(x.Month))
            .Select(x => new { UserId = x.UserId!.Value, x.Year, x.Month, x.Target }).ToListAsync(ct);
        var orders = await _db.Orders.AsNoTracking().Where(x => x.CreatedBy.HasValue && userIds.Contains(x.CreatedBy.Value)
            && x.OrderDate >= rangeStart && x.OrderDate < rangeEnd && x.DeletedAt == null)
            .Select(x => new { UserId = x.CreatedBy!.Value, x.OrderDate, Value = x.GrandTotal }).ToListAsync(ct);

        List<Dictionary<string, object?>> visitRows = userIds.Length == 0 ? [] : await Query($@"SELECT CAST(user_id AS bigint) user_id,
YEAR(checkin_date) visit_year, MONTH(checkin_date) visit_month, COUNT_BIG(*) visit_count
FROM check_in WHERE deleted_at IS NULL AND checkin_date >= '{rangeStart:yyyy-MM-dd}' AND checkin_date < '{rangeEnd:yyyy-MM-dd}'
AND user_id IN ({string.Join(',', userIds)}) GROUP BY user_id, YEAR(checkin_date), MONTH(checkin_date)", ct);
        var visitCounts = visitRows.ToDictionary(
            x => (ULong(x, "user_id"), Convert.ToInt32(Obj(x, "visit_year"), CultureInfo.InvariantCulture), Convert.ToInt32(Obj(x, "visit_month"), CultureInfo.InvariantCulture)),
            x => Convert.ToInt32(Obj(x, "visit_count"), CultureInfo.InvariantCulture));

        var retailerAssignmentPeriods = await RetailerAssignmentPeriods(userIds, rangeEnd, ct);
        var assignedRetailerIds = retailerAssignmentPeriods.Select(x => x.CustomerId).Distinct().ToArray();
        var retailerOrders = assignedRetailerIds.Length == 0 ? [] : await _db.Orders.AsNoTracking()
            .Where(x => x.BuyerId.HasValue && assignedRetailerIds.Contains(x.BuyerId.Value)
                && x.OrderDate >= rangeStart && x.OrderDate < rangeEnd && x.DeletedAt == null)
            .Select(x => new RetailerOrderActivity(x.BuyerId!.Value, x.OrderDate!.Value)).ToListAsync(ct);

            var rows = users.Select(user =>
            {
                var joiningDate = user.DateOfJoining ?? userDetailJoiningDates.GetValueOrDefault(user.Id);
                var ratingStartMonth = RatingAverageStartMonth(joiningDate, rangeStart);
                var monthlyRatings = new Dictionary<string, decimal>();
                var monthlyDetails = new Dictionary<string, RatingTrendMonthDetail>();
                var cumulativeAssignedRetailers = new HashSet<ulong>();
                var cumulativeActiveRetailers = new HashSet<ulong>();
                var carriedActive = 0;
                var carriedAssigned = 0;
                foreach (var monthStart in monthStarts)
                {
                    var monthEnd = monthStart.AddMonths(1);
                    var activeRetailerIds = retailerOrders
                        .Where(x => x.OrderDate >= monthStart && x.OrderDate < monthEnd)
                        .Select(x => x.CustomerId).ToHashSet();
                    var registeredRetailers = 0;
                    var active = 0;
                    if (monthStart >= ratingStartMonth)
                    {
                        var assigned = RetailerAssignmentsForPeriod(retailerAssignmentPeriods, user.Id, monthStart, monthEnd);
                        cumulativeAssignedRetailers.UnionWith(assigned);
                        // A retailer becomes active from the first month it was both assigned to this
                        // user and placed an order, and stays active for the remaining months. Only
                        // this user's own retailers count: retailerOrders spans every employee in the
                        // result set, so unfiltered order ids would push active above assigned.
                        cumulativeActiveRetailers.UnionWith(assigned.Where(activeRetailerIds.Contains));
                        registeredRetailers = Math.Max(carriedAssigned, cumulativeAssignedRetailers.Count);
                        active = Math.Max(carriedActive, cumulativeActiveRetailers.Count);
                        carriedAssigned = registeredRetailers;
                        carriedActive = active;
                    }
                    var userAttendance = attendance.Where(x => x.UserId == user.Id && x.PunchinDate >= monthStart && x.PunchinDate < monthEnd).ToList();
                    var marketDays = userAttendance.Where(x => !IsLeaveOrOffice(x.WorkingType)).Select(x => x.PunchinDate.Date).Distinct().Count();
                    var promotional = Api.Services.PromotionalActivityCounts.Count(promotionalActivities, user.Id, monthStart, monthEnd);
                    var visits = visitCounts.GetValueOrDefault((user.Id, monthStart.Year, monthStart.Month));
                    var target = targets.Where(x => x.UserId == user.Id && x.Year == monthStart.Year
                    && (string.Equals(x.Month, monthStart.ToString("MMM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.Month, monthStart.ToString("MMMM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)))
                    .Sum(x => x.Target ?? 0m);
                var orderValue = orders.Where(x => x.UserId == user.Id && x.OrderDate >= monthStart && x.OrderDate < monthEnd).Sum(x => x.Value);
                var achievement = orderValue > 1m ? Math.Round((orderValue - orderValue / 100m) / 100000m, 2) : 0m;
                    // A closed month is scored against the full targets; the one still running
                    // against the part of them its elapsed days have earned.
                    var share = monthStart == currentMonth ? currentMonthShare : 1m;
                    var marketTarget = Math.Round(20m * share, 2);
                    var visitTarget = Math.Round(200m * share, 2);
                    var promotionalTarget = Math.Round(4m * share, 2);
                    var salesTarget = Math.Round(target * share, 2);
                    var score = CalculateRatingScores(marketDays, visits, achievement, salesTarget, promotional,
                        registeredRetailers, active, marketTarget, visitTarget, promotionalTarget);
                    var monthKey = monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                    monthlyRatings[monthKey] = score.FinalRating;
                    monthlyDetails[monthKey] = new RatingTrendMonthDetail(score.FinalRating,
                    [
                        new("market_days", "Market Days", Math.Round(score.MarketRatio * 100m, 2), marketDays, marketTarget, 5m,
                        $"{marketDays} of {marketTarget:0.##} target market days completed"),
                    new("customer_visits", "Customer Visits", Math.Round(score.VisitRatio * 100m, 2), visits, visitTarget, 30m,
                        $"{visits} of {visitTarget:0.##} target customer visits completed"),
                    new("sales_achievement", "Sales Achievement", Math.Round(score.SalesRatio * 100m, 2), achievement, salesTarget, 40m,
                        $"Achieved {achievement:0.00}L against {salesTarget:0.00}L target"),
                    new("promotional_activity", "Promotional Activity", Math.Round(score.PromoRatio * 100m, 2), promotional, promotionalTarget, 10m,
                        $"{promotional} of {promotionalTarget:0.##} target promotional activities completed"),
                    new("active_retailer", "Active Retailer", Math.Round(score.ActiveRatingRatio * 100m, 2), active, registeredRetailers, 15m,
                        $"{active} of {registeredRetailers} assigned retailers active (30% active gives full score)")
                ]);
                }

            var eligibleMonthKeys = monthStarts.Where(monthStart => monthStart >= ratingStartMonth)
                .Select(monthStart => monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var eligibleRatings = monthlyRatings.Where(item => eligibleMonthKeys.Contains(item.Key)).Select(item => item.Value).ToArray();

            return new RatingTrendRow(user.Id, BranchName(user, branches), user.EmployeeCodes ?? string.Empty, user.Name,
                Domain.Services.EmployeeStatus.Of(user.Active), Name(userNames, user.ReportingId), Name(divisions, user.DivisionId),
                Math.Round(eligibleRatings.DefaultIfEmpty(0m).Average(), 2), joiningDate, ratingStartMonth,
                eligibleRatings.Length, monthlyRatings, monthlyDetails);
        }).OrderByDescending(x => x.AverageRating).ThenBy(x => x.EmployeeName).ToList();

        var search = filter.Search?.Trim();
        var filteredRows = string.IsNullOrWhiteSpace(search) ? rows : rows.Where(x =>
            x.EmployeeName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || x.EmployeeCode.Contains(search, StringComparison.OrdinalIgnoreCase)
            || x.Branch.Contains(search, StringComparison.OrdinalIgnoreCase)
            || x.Zone.Contains(search, StringComparison.OrdinalIgnoreCase)
            || x.ReportingManager.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        var pageSize = new[] { 10, 25, 50, 100 }.Contains(filter.PageSize) ? filter.PageSize : 10;
        var total = filteredRows.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (decimal)pageSize));
        var page = Math.Clamp(filter.Page, 1, totalPages);
        var pagedRows = filteredRows.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var monthlyAverages = monthStarts.ToDictionary(x => x.ToString("yyyy-MM", CultureInfo.InvariantCulture), monthStart =>
        {
            var eligibleRows = filteredRows.Where(row => row.RatingStartMonth <= monthStart).ToList();
            return eligibleRows.Count == 0 ? 0m : Math.Round(eligibleRows.Average(row =>
                row.MonthlyRatings.GetValueOrDefault(monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture))), 2);
        });
        var average = filteredRows.Count == 0 ? 0m : Math.Round(filteredRows.Average(x => x.AverageRating), 2);
        var oldestAverage = monthlyAverages.GetValueOrDefault(monthStarts[0].ToString("yyyy-MM", CultureInfo.InvariantCulture));
        var top = filteredRows.FirstOrDefault();
        var attention = filteredRows.LastOrDefault();
        var periodLabel = $"{monthStarts[0]:MMM yyyy} to {monthStarts[^1]:MMM yyyy}";

        return Ok(new
        {
            status = true,
            period = new
            {
                label = periodLabel,
                start_date = rangeStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                end_date = rangeEnd.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                months = monthStarts.Select(x => new
                {
                    key = x.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    label = x.ToString("MMM", CultureInfo.InvariantCulture),
                    full_label = x.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                    // The month still running is scored on its elapsed days, so the screen
                    // can say so rather than leaving a part-month target unexplained.
                    in_progress = x == currentMonth,
                    elapsed_days = x == currentMonth ? indiaToday.Day : DateTime.DaysInMonth(x.Year, x.Month),
                    days_in_month = DateTime.DaysInMonth(x.Year, x.Month)
                })
            },
            summary = new
            {
                total_employees = filteredRows.Count,
                total_zones = filteredRows.Select(x => x.Zone).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                average_rating = average,
                oldest_month_average = oldestAverage,
                average_change = Math.Round(average - oldestAverage, 2),
                comparison_month = monthStarts[0].ToString("MMM", CultureInfo.InvariantCulture),
                monthly_averages = monthlyAverages,
                top_rated = top is null ? null : new { name = top.EmployeeName, employee_code = top.EmployeeCode, branch = top.Branch, zone = top.Zone, rating = top.AverageRating },
                needs_attention = attention is null ? null : new { name = attention.EmployeeName, employee_code = attention.EmployeeCode, branch = attention.Branch, zone = attention.Zone, rating = attention.AverageRating }
            },
            pagination = new
            {
                page,
                page_size = pageSize,
                total,
                total_pages = totalPages,
                from = total == 0 ? 0 : (page - 1) * pageSize + 1,
                to = Math.Min(total, page * pageSize)
            },
            rows = pagedRows.Select(x => new
            {
                user_id = x.UserId,
                branch = x.Branch,
                employee_code = x.EmployeeCode,
                employee_name = x.EmployeeName,
                employee_status = x.EmployeeStatus,
                reporting_manager = x.ReportingManager,
                zone = x.Zone,
                average_rating = x.AverageRating,
                date_of_joining = x.DateOfJoining?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                rating_start_month = x.RatingStartMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                average_month_count = x.AverageMonthCount,
                monthly_ratings = x.MonthlyRatings,
                monthly_details = x.MonthlyDetails.ToDictionary(item => item.Key, item => new
                {
                    final_rating = item.Value.FinalRating,
                    components = item.Value.Components.Select(component => new
                    {
                        key = component.Key,
                        label = component.Label,
                        percentage = component.Percentage,
                        actual = component.Actual,
                        target = component.Target,
                        weight = component.Weight,
                        weighted_score = Math.Round(component.Weight * component.Percentage / 100m, 2),
                        description = component.Description
                    })
                })
            })
        });
    }

    [HttpGet("productivity/options")]
    public async Task<IActionResult> ProductivityOptions(CancellationToken cancellationToken)
    {
        var actor = CurrentUserId();
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, cancellationToken)).Distinct().ToArray();
        var users = await _db.Users.AsNoTracking().Where(x => visibleIds.Contains(x.Id) && x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null)
            .OrderBy(x => x.Name).Select(x => new { id = x.Id, name = x.Name }).ToListAsync(cancellationToken);
        var divisions = (await _db.Divisions.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null).Select(x => new { id = x.Id, name = x.DivisionName }).ToListAsync(cancellationToken)).ByZone(x => x.name).ToList();
        var branches = await BranchOptionRowsAsync(cancellationToken);
        var designations = await _db.Designations.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null).OrderBy(x => x.DesignationName).Select(x => new { id = x.Id, name = x.DesignationName }).ToListAsync(cancellationToken);
        var states = await _db.States.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null).OrderBy(x => x.StateName).Select(x => new { id = x.Id, name = x.StateName }).ToListAsync(cancellationToken);
        var customers = await _db.Customers.AsNoTracking().Where(x => x.DeletedAt == null && x.Active == "Y" && (x.CustomerType == 1 || x.CustomerType == 2)).OrderBy(x => x.Name)
            .Select(x => new { id = x.Id, name = x.Name, type = x.CustomerType }).ToListAsync(cancellationToken);
        var asrId = designations.FirstOrDefault(x => string.Equals(x.name.Trim(), "ASR", StringComparison.OrdinalIgnoreCase))?.id;
        var dealerDefaults = designations.Where(x => string.Equals(x.name.Trim(), "ASR", StringComparison.OrdinalIgnoreCase) || string.Equals(x.name.Trim(), "DSR", StringComparison.OrdinalIgnoreCase)).Select(x => x.id).ToArray();
        return Ok(new { users, divisions, branches, designations, states, retailers = customers.Where(x => x.type == 2), dealers = customers.Where(x => x.type == 1), default_retailer_designation_ids = asrId.HasValue ? new[] { asrId.Value } : Array.Empty<ulong>(), default_dealer_designation_ids = dealerDefaults });
    }

    [HttpGet("retailer-performance/export")]
    [RequirePermission("retailer_performance_report.export")]
    public async Task<IActionResult> ExportRetailerPerformance([FromQuery] ProductivityFilter filter, CancellationToken ct)
    {
        if (!filter.DivisionId.HasValue) return BadRequest(new { status = false, message = "Please select a zone before downloading the report." });
        var actor = CurrentUserId();
        var employeeStatus = Domain.Services.EmployeeStatus.Read(filter.EmployeeStatus);
        var visible = (await _hr.GetVisibleUserIdsAsync(actor, includeInactive: true, ct)).Distinct().ToHashSet();
        var users = await _db.Users.AsNoTracking().Where(x => visible.Contains(x.Id) && !x.IsDeleted && x.DeletedAt == null).ToListAsync(ct);
        users = users.Where(x => (!filter.EmployeeId.HasValue || x.Id == filter.EmployeeId) && x.DivisionId == filter.DivisionId && (filter.DesignationIds.Length == 0 || (x.DesignationId.HasValue && filter.DesignationIds.Contains(x.DesignationId.Value))) && Domain.Services.EmployeeStatus.Matches(employeeStatus, x.Active)).ToList();
        var userIds = users.Select(x => x.Id).ToArray();
        var retailers = await _db.Customers.AsNoTracking().Where(x => x.DeletedAt == null && x.Active == "Y" && x.CustomerType == 2 && (!filter.RetailerId.HasValue || x.Id == filter.RetailerId)).ToListAsync(ct);
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
        var headers = new List<string> { "Employee Name", "Employee Status", "Customer Name", "City", "District", "State", "Distributor Name", "Reporting Manager" };
        for (var month = 1; month <= 12; month++) { headers.Add(CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month) + "-Qty"); headers.Add(CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month) + "-Value"); }
        headers.AddRange(["YTD Order Qty", "YTD Order Value", "Customer Id", "Employee Code", "Distributor Id"]);
        for (var i = 0; i < headers.Count; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var rowNumber = 2;
        foreach (var customer in retailers.OrderByDescending(x => x.CreatedAt))
        {
            var employeeId = customer.ExecutiveId ?? customer.CreatedBy; userMap.TryGetValue(employeeId ?? 0, out var employee);
            var customerOrders = orders.Where(x => x.BuyerId == customer.Id).ToList(); var dealerId = JsonULong(customer.CustomFields, "distributor_name");
            var values = new List<object?> { employee?.Name ?? "-", employee is null ? "-" : Domain.Services.EmployeeStatus.Of(employee.Active), customer.Name, Name(cities, JsonULong(customer.CustomFields, "city_id")), Name(districts, JsonULong(customer.CustomFields, "district_id")), Name(states, JsonULong(customer.CustomFields, "state_id")), Name(dealers, dealerId), Name(allUserNames, employee?.ReportingId) };
            for (var month = 1; month <= 12; month++) { var monthOrders = customerOrders.Where(x => x.OrderDate!.Value.Month == month); values.Add(monthOrders.Sum(x => x.TotalQty)); values.Add(monthOrders.Sum(x => x.GrandTotal)); }
            values.Add(customerOrders.Sum(x => x.TotalQty)); values.Add(customerOrders.Sum(x => x.GrandTotal)); values.Add(customer.Id); values.Add(employee?.EmployeeCodes ?? "-"); values.Add(dealerId ?? 0);
            WriteRow(sheet, rowNumber++, values);
        }
        StyleSimpleExport(sheet, headers.Count, rowNumber - 1, "D9E1F2");
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Retailer_Productivity_Report_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }

    [HttpGet("dealer-performance/export")]
    [RequirePermission("dealer_performance_report.export")]
    public async Task<IActionResult> ExportDealerPerformance([FromQuery] ProductivityFilter filter, CancellationToken ct)
    {
        var actor = CurrentUserId();
        var employeeStatus = Domain.Services.EmployeeStatus.Read(filter.EmployeeStatus);
        var visible = (await _hr.GetVisibleUserIdsAsync(actor, includeInactive: true, ct)).Distinct().ToHashSet();
        var users = await _db.Users.AsNoTracking().Where(x => visible.Contains(x.Id) && !x.IsDeleted && x.DeletedAt == null).ToListAsync(ct);
        users = users.Where(x => (!filter.EmployeeId.HasValue || x.Id == filter.EmployeeId) && (!filter.DivisionId.HasValue || x.DivisionId == filter.DivisionId) && (!filter.BranchId.HasValue || UserHasBranch(x, filter.BranchId.Value)) && (filter.DesignationIds.Length == 0 || (x.DesignationId.HasValue && filter.DesignationIds.Contains(x.DesignationId.Value))) && Domain.Services.EmployeeStatus.Matches(employeeStatus, x.Active)).ToList();
        var userIds = users.Select(x => x.Id).ToArray(); var assignments = await DealerAssignments(userIds, ct);
        var dealerIds = assignments.Keys.ToArray();
        var dealers = await _db.Customers.AsNoTracking().Where(x => x.CustomerType == 1 && x.DeletedAt == null && x.Active == "Y" && dealerIds.Contains(x.Id) && (!filter.DealerId.HasValue || x.Id == filter.DealerId)).ToListAsync(ct);
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
            .OrderBy(x => ZoneOrder.Rank(x.Zone)).ThenBy(x => x.Zone).ThenBy(x => x.Branch).ThenBy(x => x.Dealer.Name).ThenBy(x => x.User.Name).ToList();
        using var workbook = new XLWorkbook(); var sheet = workbook.Worksheets.Add("Distributor Productivity");
        var headers = new[] { "Distributor Code", "Distributor Name", "Distributor Location", "Employees Code", "Designation", "Employees Name", "Employee Status", "Reporting Manager", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec", "Total" };
        for (var m = 1; m <= 12; m++) sheet.Cell(1, 8 + m).Value = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(m); sheet.Cell(1, 21).Value = "Total";
        for (var i = 0; i < 8; i++) sheet.Cell(2, i + 1).Value = headers[i]; for (var i = 8; i < headers.Length; i++) sheet.Cell(2, i + 1).Value = "Secondary Val (Lac)";
        var outputRow = 3;
        foreach (var zone in prepared.GroupBy(x => x.Zone)) { foreach (var branch in zone.GroupBy(x => x.Branch)) { foreach (var item in branch) { var cityId = JsonULong(item.Dealer.CustomFields, "billing_city") ?? JsonULong(item.Dealer.CustomFields, "city_id"); var vals = new List<object?> { item.Dealer.CustomerCode, item.Dealer.Name, Name(cities, cityId), item.User.EmployeeCodes, Name(designations, item.User.DesignationId), item.User.Name, Domain.Services.EmployeeStatus.Of(item.User.Active), item.Reporting }; vals.AddRange(item.Monthly.Values.Select(value => (object?)ToLac(value))); vals.Add(ToLac(item.Monthly.Values.Sum())); WriteRow(sheet, outputRow++, vals); } WriteDealerTotal(sheet, outputRow++, branch.Key + " Total", branch.SelectMany(x => x.Monthly).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Sum(y => y.Value)), XLColor.FromHtml("FFF59D"), false); } WriteDealerTotal(sheet, outputRow++, zone.Key + " Total", zone.SelectMany(x => x.Monthly).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Sum(y => y.Value)), XLColor.FromHtml("004A88"), true); }
        WriteDealerTotal(sheet, outputRow++, "Grand Total", prepared.SelectMany(x => x.Monthly).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Sum(y => y.Value)), XLColor.FromHtml("43A047"), true);
        StyleSimpleExport(sheet, 21, outputRow - 1, "1E88E5", 2); sheet.Range(3, 9, outputRow - 1, 21).Style.NumberFormat.Format = "0.00"; sheet.SheetView.FreezeRows(2);
        using var stream = new MemoryStream(); workbook.SaveAs(stream); return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Distributors_Productivity_Report_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }

    [HttpGet("asr-performance/export")]
    [RequirePermission("asr_performance_report.export")]
    public async Task<IActionResult> ExportAsrPerformance([FromQuery] AsrPerformanceFilter filter, CancellationToken cancellationToken)
    {
        if (filter.StartDate == default || filter.EndDate == default)
            return BadRequest(new { status = false, message = "Start date and end date are required." });
        if (filter.StartDate > filter.EndDate)
            return BadRequest(new { status = false, message = "Start date cannot be after end date." });
        if (filter.DesignationId is null)
            return BadRequest(new { status = false, message = "Designation is required." });

        var actor = CurrentUserId();
        var employeeStatus = Domain.Services.EmployeeStatus.Read(filter.EmployeeStatus);
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, includeInactive: true, cancellationToken)).Distinct().ToHashSet();
        var allUsers = await _db.Users.AsNoTracking().Where(x => visibleIds.Contains(x.Id) && !x.IsDeleted && x.DeletedAt == null)
            .ToListAsync(cancellationToken);
        var users = allUsers.Where(x => (!filter.EmployeeId.HasValue || x.Id == filter.EmployeeId)
                && (!filter.DivisionId.HasValue || x.DivisionId == filter.DivisionId)
                && (!filter.DesignationId.HasValue || x.DesignationId == filter.DesignationId)
                && (!filter.BranchId.HasValue || UserHasBranch(x, filter.BranchId.Value))
                && Domain.Services.EmployeeStatus.Matches(employeeStatus, x.Active))
            .ToList();

        var userIds = users.Select(x => x.Id).ToArray();
        var divisions = await _db.Divisions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DivisionName, cancellationToken);
        var branches = await _db.Branches.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
        var designations = await _db.Designations.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DesignationName, cancellationToken);
        var userNames = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        users = users.OrderBy(x => ZoneOrder.Rank(Name(divisions, x.DivisionId))).ThenBy(x => Name(divisions, x.DivisionId))
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
        var customers = await _db.Customers.AsNoTracking().Where(x => x.DeletedAt == null && x.Active == "Y").Select(x => new { x.Id, x.CreatedBy, x.ExecutiveId, x.CreatedAt }).ToListAsync(cancellationToken);

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
            return new AsrPerformanceRow(user.Id, user.Name, Domain.Services.EmployeeStatus.Of(user.Active), 15, workingDays, target, visited,
                target > 0 ? Math.Round(visited * 100m / target, 1) : 0, productive,
                visited > 0 ? Math.Round(productive * 100m / visited, 1) : 0, newCounters,
                userOrders.Sum(x => x.TotalQty), userOrders.Sum(x => x.GrandTotal),
                details.Where(x => x.OrderId.HasValue && ids.Contains(x.OrderId.Value) && x.ProductId.HasValue).Select(x => x.ProductId).Distinct().Count(),
                assignedCounts.GetValueOrDefault(user.Id, customers.Count(x => x.ExecutiveId == user.Id)),
                Name(divisions, user.DivisionId), BranchName(user, branches), Name(designations, user.DesignationId), Name(userNames, user.ReportingId));
        }).ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("ASR Performance");
        var headers = new[] { "Employees Code", "Employees Name", "Employee Status", "Daily Visit Target", "Total Working Days", "Total Visit Target", "Total Visits", "Adherance %", "Total Productive Visits", "Productivity %", "New Counter Added", "Total Order Qty", "Total Order Value", "Unique SKU Ordered", "Total Cumulative Counter", "ZONE", "Branch", "Designation", "Reporting Manager" };
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
    [RequirePermission("market_intelligence_report.view")]
    public async Task<IActionResult> MarketIntelligence([FromQuery] string? search, CancellationToken cancellationToken)
    {
        var actor = CurrentUserId();
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, cancellationToken)).Distinct().ToArray();
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

    /// <summary>Every check-in is a visit, including a repeat visit to a customer already
    /// seen. The ASR performance report and the rating report share this one definition so
    /// the same employee cannot show two different visit totals in two reports.</summary>
    private async Task<Dictionary<ulong, int>> VisitCounts(ulong[] userIds, DateOnly start, DateOnly end, CancellationToken ct)
    {
        if (userIds.Length == 0) return [];
        var rows = await Query($@"SELECT CAST(user_id AS bigint) user_id, COUNT_BIG(*) visit_count
FROM check_in WHERE deleted_at IS NULL AND checkin_date >= '{start:yyyy-MM-dd}' AND checkin_date <= '{end:yyyy-MM-dd}'
AND user_id IN ({string.Join(',', userIds)}) GROUP BY user_id", ct);
        return rows.ToDictionary(x => ULong(x, "user_id"), x => Convert.ToInt32(Obj(x, "visit_count"), CultureInfo.InvariantCulture));
    }

    private async Task<Dictionary<ulong, int>> AssignedCustomerCounts(ulong[] userIds, CancellationToken ct)
    {
        if (userIds.Length == 0) return [];
        var rows = await Query($@"SELECT user_id, COUNT(DISTINCT customer_id) customer_count FROM (
SELECT CAST(executive_id AS bigint) user_id, CAST(id AS bigint) customer_id FROM customers WHERE deleted_at IS NULL AND active = 'Y' AND executive_id IN ({string.Join(',', userIds)})
UNION SELECT CAST(ed.user_id AS bigint), CAST(ed.customer_id AS bigint) FROM employee_details ed INNER JOIN customers c ON c.id = ed.customer_id AND c.deleted_at IS NULL AND c.active = 'Y' WHERE ed.deleted_at IS NULL AND ed.user_id IN ({string.Join(',', userIds)})
) assignments GROUP BY user_id", ct);
        return rows.ToDictionary(x => ULong(x, "user_id"), x => Convert.ToInt32(Obj(x, "customer_count"), CultureInfo.InvariantCulture));
    }

    private async Task<List<RetailerAssignmentPeriod>> RetailerAssignmentPeriods(ulong[] userIds, DateTime rangeEnd, CancellationToken ct)
    {
        if (userIds.Length == 0) return [];
        var ids = string.Join(',', userIds);
        var rows = await Query($@"SELECT CAST(user_id AS bigint) user_id, CAST(customer_id AS bigint) customer_id,
assigned_at, unassigned_at FROM (
    SELECT c.executive_id user_id, c.id customer_id, c.created_at assigned_at, c.deleted_at unassigned_at
    FROM customers c
    WHERE c.customertype = 2 AND c.executive_id IN ({ids})
        AND NOT EXISTS (SELECT 1 FROM employee_details ed WHERE ed.customer_id = c.id)
        AND (c.created_at IS NULL OR c.created_at < '{rangeEnd:yyyy-MM-dd HH:mm:ss}')
    UNION ALL
    SELECT ed.user_id, ed.customer_id,
        CASE
            WHEN ed.created_at IS NULL THEN c.created_at
            WHEN c.created_at IS NULL THEN ed.created_at
            WHEN ed.created_at >= c.created_at THEN ed.created_at ELSE c.created_at
        END assigned_at,
        CASE
            WHEN ed.deleted_at IS NULL THEN c.deleted_at
            WHEN c.deleted_at IS NULL THEN ed.deleted_at
            WHEN ed.deleted_at <= c.deleted_at THEN ed.deleted_at ELSE c.deleted_at
        END unassigned_at
    FROM employee_details ed
    INNER JOIN customers c ON c.id = ed.customer_id AND c.customertype = 2
    WHERE ed.user_id IN ({ids}) AND (ed.active = 'Y' OR ed.active IS NULL)
        AND ((CASE
            WHEN ed.created_at IS NULL THEN c.created_at
            WHEN c.created_at IS NULL THEN ed.created_at
            WHEN ed.created_at >= c.created_at THEN ed.created_at ELSE c.created_at
        END) IS NULL OR (CASE
            WHEN ed.created_at IS NULL THEN c.created_at
            WHEN c.created_at IS NULL THEN ed.created_at
            WHEN ed.created_at >= c.created_at THEN ed.created_at ELSE c.created_at
        END) < '{rangeEnd:yyyy-MM-dd HH:mm:ss}')
) assigned", ct);

        return rows.Select(x => new RetailerAssignmentPeriod(
                ULong(x, "user_id"),
                ULong(x, "customer_id"),
                NullableDateTime(x, "assigned_at"),
                NullableDateTime(x, "unassigned_at")))
            .Where(x => x.UserId > 0 && x.CustomerId > 0)
            .ToList();
    }

    private static List<ulong> RetailerAssignmentsForPeriod(IReadOnlyCollection<RetailerAssignmentPeriod> assignments,
        ulong userId, DateTime periodStart, DateTime periodEnd) => assignments
        .Where(x => x.UserId == userId
            && (!x.AssignedAt.HasValue || x.AssignedAt.Value < periodEnd)
            && (!x.UnassignedAt.HasValue || x.UnassignedAt.Value > periodStart))
        .Select(x => x.CustomerId)
        .Distinct()
        .ToList();

    private static DateTime? NullableDateTime(IReadOnlyDictionary<string, object?> row, string key)
    {
        var value = Obj(row, key);
        return value is null ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
    }

    private static bool IsLeaveOrOffice(string? workingType)
    {
        var values = (workingType ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values.Length == 0 || values.All(x => x.Equals("Office Work", StringComparison.OrdinalIgnoreCase)
            || x.Equals("Office Meeting", StringComparison.OrdinalIgnoreCase) || x.Equals("Full Day Leave", StringComparison.OrdinalIgnoreCase)
            || x.Equals("Leave", StringComparison.OrdinalIgnoreCase) || x.Equals("Holiday", StringComparison.OrdinalIgnoreCase));
    }


    private static DateTime RatingAverageStartMonth(DateTime? joiningDate, DateTime defaultStart)
    {
        if (!joiningDate.HasValue) return new DateTime(defaultStart.Year, defaultStart.Month, 1);
        var joiningMonth = new DateTime(joiningDate.Value.Year, joiningDate.Value.Month, 1);
        return joiningDate.Value.Day <= 15 ? joiningMonth : joiningMonth.AddMonths(1);
    }

    private static decimal CappedRatio(decimal achievement, decimal target) => target <= 0 ? 0m : Math.Min(achievement / target, 1m);

    /// <summary>The three activity targets are decimals because a part-finished month is
    /// scored against its own elapsed share of them, which is rarely a whole number.</summary>
    private static RatingScores CalculateRatingScores(int marketDays, int visits, decimal salesAchievement, decimal salesTarget,
        int promotional, int registeredRetailers, int activeRetailers, decimal marketTarget, decimal visitTarget, decimal promotionalTarget)
    {
        const decimal marketWeight = 5m, visitWeight = 30m, salesWeight = 40m, promoWeight = 10m, retailerWeight = 15m;
        var marketRatio = CappedRatio(marketDays, marketTarget);
        var visitRatio = CappedRatio(visits, visitTarget);
        var salesRatio = CappedRatio(salesAchievement, salesTarget);
        var promoRatio = CappedRatio(promotional, promotionalTarget);
        var activeRatio = registeredRetailers == 0 ? 0m : (decimal)activeRetailers / registeredRetailers;
        var activeRatingRatio = Math.Min(activeRatio, .30m) / .30m;
        var final = marketWeight * marketRatio + visitWeight * visitRatio + salesWeight * salesRatio
            + promoWeight * promoRatio + retailerWeight * activeRatingRatio;
        return new RatingScores(marketRatio, visitRatio, salesRatio, promoRatio, activeRatio, activeRatingRatio,
            marketWeight * marketRatio, visitWeight * visitRatio, salesWeight * salesRatio, promoWeight * promoRatio,
            retailerWeight * activeRatingRatio, Math.Round(final, 2));
    }

    private static string? ValidateRatingReportFilter(RatingReportFilter filter)
    {
        var isWeekly = string.Equals(filter.Period, "weekly", StringComparison.OrdinalIgnoreCase);
        if (!filter.DesignationId.HasValue) return "Designation is required.";
        if (!string.IsNullOrWhiteSpace(filter.Period) && !isWeekly) return "A valid period is required.";
        if (!isWeekly && (!filter.Year.HasValue || filter.Year is < 2000 or > 2100)) return "A valid year is required.";
        if (filter.Month.HasValue && filter.Month is < 1 or > 12) return "A valid month is required.";
        return null;
    }

    private async Task<RatingReportCalculation> CalculateRatingReport(RatingReportFilter filter, CancellationToken ct)
    {
        var isWeekly = string.Equals(filter.Period, "weekly", StringComparison.OrdinalIgnoreCase);
        var actor = CurrentUserId();
        // An employee switched off since still worked the months being rated, so they keep
        // their row; the Employee Status filter narrows to one or the other.
        var employeeStatus = Domain.Services.EmployeeStatus.Read(filter.EmployeeStatus);
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(actor, includeInactive: true, ct)).Distinct().ToHashSet();
        var users = await _db.Users.AsNoTracking()
            .Where(x => visibleIds.Contains(x.Id) && !x.IsDeleted && x.DeletedAt == null && x.DesignationId == filter.DesignationId)
            .ToListAsync(ct);
        users = users.Where(x => (!filter.DivisionId.HasValue || x.DivisionId == filter.DivisionId)
            && (!filter.BranchId.HasValue || UserHasBranch(x, filter.BranchId.Value))
            && Domain.Services.EmployeeStatus.Matches(employeeStatus, x.Active)).ToList();

        var userIds = users.Select(x => x.Id).ToArray();
        var divisions = await _db.Divisions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DivisionName, ct);
        var branches = await _db.Branches.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.BranchName, ct);
        var userNames = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        users = users.OrderBy(x => ZoneOrder.Rank(Name(divisions, x.DivisionId))).ThenBy(x => Name(divisions, x.DivisionId))
            .ThenBy(x => BranchName(x, branches)).ThenBy(x => x.Name).ToList();

        var indiaToday = DateTime.UtcNow.AddHours(5).AddMinutes(30).Date;
        var isYtd = !isWeekly && !filter.Month.HasValue;
        var start = isWeekly ? indiaToday.AddDays(-7) : new DateTime(filter.Year!.Value, filter.Month ?? 1, 1);
        // A month that has not finished yet ends today, so it is scored on the days that
        // have actually happened rather than on a whole month the employee has not had.
        var monthEndsToday = filter.Month.HasValue && filter.Year == indiaToday.Year && filter.Month == indiaToday.Month;
        var end = isWeekly ? indiaToday
            : filter.Month.HasValue ? (monthEndsToday ? indiaToday.AddDays(1) : start.AddMonths(1))
            : filter.Year == indiaToday.Year ? indiaToday.AddDays(1) : new DateTime(filter.Year!.Value + 1, 1, 1);
        var retailerAssignmentPeriods = await RetailerAssignmentPeriods(userIds, end, ct);

        const decimal marketWeight = 5m, visitWeight = 30m, salesWeight = 40m, promoWeight = 10m, retailerWeight = 15m;

        async Task<List<RatingReportRow>> CalculatePeriod(DateTime periodStart, DateTime periodEnd, bool weekly, bool ytd)
        {
            var monthCount = ((periodEnd.AddDays(-1).Year - periodStart.Year) * 12) + periodEnd.AddDays(-1).Month - periodStart.Month + 1;
            var monthStarts = Enumerable.Range(0, monthCount).Select(periodStart.AddMonths).Select(x => new DateTime(x.Year, x.Month, 1)).ToArray();
            var targetMonths = monthStarts.SelectMany(x => new[] { x.ToString("MMM", CultureInfo.InvariantCulture), x.ToString("MMMM", CultureInfo.InvariantCulture) }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var targetYears = monthStarts.Select(x => x.Year).Distinct().ToArray();
            // How much of each month the window actually covers. A closed month counts once;
            // the month still running counts only the share its elapsed days have earned.
            decimal ElapsedShare(DateTime monthStart)
            {
                var monthEnd = monthStart.AddMonths(1);
                var from = periodStart > monthStart ? periodStart : monthStart;
                var to = periodEnd < monthEnd ? periodEnd : monthEnd;
                return (decimal)Math.Max(0, (to - from).Days) / DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
            }
            var periodMonths = monthStarts.Sum(ElapsedShare);
            var attendance = await _db.Attendances.AsNoTracking().Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value)
                && x.PunchinDate >= periodStart && x.PunchinDate < periodEnd && x.DeletedAt == null).Select(x => new { UserId = x.UserId!.Value, x.PunchinDate, x.WorkingType }).ToListAsync(ct);
            var promotionalActivities = await Api.Services.PromotionalActivityCounts.LoadAsync(_db, userIds, periodStart, periodEnd, ct);
            var targets = await _db.SalesTargetUsers.AsNoTracking().Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value)
                && x.Year.HasValue && targetYears.Contains(x.Year.Value) && x.Month != null && targetMonths.Contains(x.Month)).Select(x => new { UserId = x.UserId!.Value, x.Year, x.Month, x.Target }).ToListAsync(ct);
            var orders = await _db.Orders.AsNoTracking().Where(x => x.CreatedBy.HasValue && userIds.Contains(x.CreatedBy.Value)
                && x.OrderDate >= periodStart && x.OrderDate < periodEnd && x.DeletedAt == null).Select(x => new { UserId = x.CreatedBy!.Value, Value = x.GrandTotal }).ToListAsync(ct);
            var visits = await VisitCounts(userIds, DateOnly.FromDateTime(periodStart), DateOnly.FromDateTime(periodEnd.AddDays(-1)), ct);
            var assignmentsByUser = users.ToDictionary(x => x.Id,
                x => RetailerAssignmentsForPeriod(retailerAssignmentPeriods, x.Id, periodStart, periodEnd));
            var periodRetailerIds = assignmentsByUser.Values.SelectMany(x => x).Distinct().ToArray();
            var activeRetailerIds = periodRetailerIds.Length == 0 ? new HashSet<ulong>() : (await _db.Orders.AsNoTracking()
                .Where(x => x.BuyerId.HasValue && periodRetailerIds.Contains(x.BuyerId.Value)
                    && x.OrderDate >= periodStart && x.OrderDate < periodEnd && x.DeletedAt == null)
                .Select(x => x.BuyerId!.Value).Distinct().ToListAsync(ct)).ToHashSet();

            return users.Select(user =>
            {
                var userAttendance = attendance.Where(x => x.UserId == user.Id).ToList();
                var marketDays = userAttendance.Where(x => !IsLeaveOrOffice(x.WorkingType)).Select(x => x.PunchinDate.Date).Distinct().Count();
                var promotional = Api.Services.PromotionalActivityCounts.Count(promotionalActivities, user.Id, periodStart, periodEnd);
                var customerVisits = visits.GetValueOrDefault(user.Id);
                var userTargets = targets.Where(x => x.UserId == user.Id).ToList();
                var target = monthStarts.Sum(monthStart =>
                {
                    var monthTarget = userTargets.Where(x => x.Year == monthStart.Year && (string.Equals(x.Month, monthStart.ToString("MMM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase) || string.Equals(x.Month, monthStart.ToString("MMMM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))).Sum(x => x.Target ?? 0m);
                    return monthTarget * ElapsedShare(monthStart);
                });
                var orderValue = orders.Where(x => x.UserId == user.Id).Sum(x => x.Value);
                var achievement = orderValue > 1m ? Math.Round((orderValue - orderValue / 100m) / 100000m, 2) : 0m;
                var assigned = assignmentsByUser.GetValueOrDefault(user.Id, []);
                var active = assigned.Count(activeRetailerIds.Contains);
                var marketTarget = weekly ? 5m : Math.Round(20m * periodMonths, 2);
                var visitTarget = weekly ? 50m : Math.Round(200m * periodMonths, 2);
                var promotionalTarget = weekly ? 1m : Math.Round(4m * periodMonths, 2);
                var score = CalculateRatingScores(marketDays, customerVisits, achievement, target, promotional, assigned.Count, active,
                    marketTarget, visitTarget, promotionalTarget);
                return new RatingReportRow(user.Id, BranchName(user, branches), user.EmployeeCodes ?? string.Empty, user.Name, Domain.Services.EmployeeStatus.Of(user.Active), Name(userNames, user.ReportingId), Name(divisions, user.DivisionId), null, score.FinalRating,
                    marketTarget, marketDays, score.MarketRatio, score.MarketRating, visitTarget, customerVisits, score.VisitRatio, score.VisitRating, Math.Round(target, 2), achievement, score.SalesRatio, score.SalesRating,
                    promotional, score.PromoRatio, score.PromoRating, promotionalTarget, assigned.Count, active, score.ActiveRatio, score.ActiveRatingRatio, score.ActiveRating);
            }).ToList();
        }

        var rows = await CalculatePeriod(start, end, isWeekly, isYtd);
        if (!isYtd)
        {
            var previousStart = isWeekly ? start.AddDays(-7) : start.AddMonths(-1);
            var previous = (await CalculatePeriod(previousStart, start, isWeekly, false)).ToDictionary(x => x.UserId, x => x.FinalRating);
            rows = rows.Select(x => x with { LastFinalRating = previous.GetValueOrDefault(x.UserId) }).ToList();
        }
        rows = rows.OrderByDescending(x => x.FinalRating).ThenBy(x => x.EmployeeName).ToList();
        return new RatingReportCalculation(rows, start, end, isWeekly, isYtd, marketWeight, visitWeight, salesWeight, promoWeight, retailerWeight);
    }

    private static void BuildRatingSheet(IXLWorksheet sheet, IReadOnlyList<RatingReportRow> rows, DateTime month,
        decimal marketWeight, decimal visitWeight, decimal salesWeight, decimal promoWeight, decimal retailerWeight, bool includeLastRating)
    {
        var shift = includeLastRating ? 1 : 0;
        // Six label columns now (Employee Status sits after the ASR name), so every banded
        // column of the old layout moves one to the right.
        var lastColumn = 32 + shift;
        int C(int original) => original + 1 + (original >= 7 ? shift : 0);
        sheet.Cell("A1").Value = month;
        sheet.Cell("A1").Style.DateFormat.Format = "mmm-yy";
        sheet.Range(1, 1, 1, 7 + shift).Merge();
        sheet.Cell(1, C(7)).Value = "Number of days per Month dedicated to market visits"; sheet.Range(1, C(7), 1, C(11)).Merge();
        sheet.Cell(1, C(12)).Value = "All Customer Visit"; sheet.Range(1, C(12), 1, C(16)).Merge();
        sheet.Cell(1, C(17)).Value = "Target Vs Ach"; sheet.Range(1, C(17), 1, C(21)).Merge();
        sheet.Cell(1, C(22)).Value = "Promotional Activity"; sheet.Range(1, C(22), 1, C(26)).Merge();
        sheet.Cell(1, C(27)).Value = "Active Retailer"; sheet.Range(1, C(27), 1, C(31)).Merge();

        sheet.Cell("A2").Value = "Target & Weightage"; sheet.Range("A2:F2").Merge();
        sheet.Cell(2, 7 + shift).Value = 100;
        sheet.Cell(2, C(7)).Value = marketWeight; sheet.Cell(2, C(8)).Value = "Above 100% achievement will be considered 100%"; sheet.Range(2, C(8), 2, C(11)).Merge();
        sheet.Cell(2, C(12)).Value = visitWeight; sheet.Cell(2, C(13)).Value = "Above 100% achievement will be considered 100%"; sheet.Range(2, C(13), 2, C(16)).Merge();
        sheet.Cell(2, C(17)).Value = "Target"; sheet.Cell(2, C(18)).Value = "Ach"; sheet.Cell(2, C(19)).Value = salesWeight;
        sheet.Cell(2, C(20)).Value = "Above 100% achievement will be considered 100%"; sheet.Range(2, C(20), 2, C(21)).Merge();
        sheet.Cell(2, C(22)).Value = promoWeight; sheet.Cell(2, C(23)).Value = "Above 100% achievement will be considered 100%"; sheet.Range(2, C(23), 2, C(26)).Merge();
        sheet.Cell(2, C(27)).Value = retailerWeight; sheet.Cell(2, C(28)).Value = "Above 30% achievement will be considered 30%"; sheet.Range(2, C(28), 2, C(31)).Merge();

        var headers = new List<string> { "Branch", "Emp Code", "ASR", "Employee Status", "Reporting Person", "Zone" };
        if (includeLastRating) headers.Add("LM Rating");
        headers.AddRange(["CM Rating", "Tgt", "Ach", "% ACHD", "For Rating %", "Final Rating", "TGT", "Ach", "% ACHD", "For Rating %", "Final Rating", "TGT", "Ach", "% ACHD", "For Rating %", "Final Rating", "Tgt", "Ach", "% ACHD", "For Rating %", "Final Rating", "Total Registred Retailer", "Active", "Active %", "For Rating %", "Final Rating"]);
        for (var i = 0; i < headers.Count; i++) sheet.Cell(3, i + 1).Value = headers[i];

        var outputRow = 4;
        foreach (var item in rows)
        {
            WriteRow(sheet, outputRow, item.Values(includeLastRating));
            foreach (var column in new[] { 7, 8, C(11), C(16), C(17), C(18), C(21), C(26), C(31) }.Where(x => x <= lastColumn)) sheet.Cell(outputRow, column).Style.NumberFormat.Format = "0.00";
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
        var values = new List<object?> { "", label, "", "", "", "", "", "" }; values.AddRange(Enumerable.Range(1, 12).Select(month => (object?)ToLac(monthly.GetValueOrDefault(month)))); values.Add(ToLac(monthly.Values.Sum())); WriteRow(sheet, row, values);
        var range = sheet.Range(row, 1, row, 21); range.Style.Fill.BackgroundColor = color; range.Style.Font.Bold = true; if (white) range.Style.Font.FontColor = XLColor.White;
    }

    private static decimal ToLac(decimal value) => Math.Round(value / 100000m, 2, MidpointRounding.AwayFromZero);

    // Loyalty > Performance Report. ASR wise and Dealer wise read the same invoices,
    // retailers, KYC and HO approvals; they differ only in how the rows are keyed.
    //
    // Invoices are those under the chosen scheme dated inside the range, whatever their
    // status. A retailer belongs to the first ASR on its own assigned-employee list, and
    // only to that one, so no subtotal counts a retailer twice for an ASR. An invoice
    // belongs to the dealer chosen on it; invoices raised before that was recorded fall
    // back to the retailer's domestic dealer, then its agri dealer. The segment narrows
    // secondary sales only - invoices carry no segment - and dealer wise sales are that
    // dealer's orders taken by that ASR.
    [HttpGet("loyalty-performance/asr-export")]
    [RequirePermission("loyalty_performance_report.export_asr")]
    public Task<IActionResult> ExportLoyaltyPerformanceAsr([FromQuery] LoyaltyPerformanceFilter filter, CancellationToken cancellationToken) =>
        ExportLoyaltyPerformance(filter, dealerWise: false, cancellationToken);

    [HttpGet("loyalty-performance/dealer-export")]
    [RequirePermission("loyalty_performance_report.export_dealer")]
    public Task<IActionResult> ExportLoyaltyPerformanceDealer([FromQuery] LoyaltyPerformanceFilter filter, CancellationToken cancellationToken) =>
        ExportLoyaltyPerformance(filter, dealerWise: true, cancellationToken);

    private async Task<IActionResult> ExportLoyaltyPerformance(LoyaltyPerformanceFilter filter, bool dealerWise, CancellationToken cancellationToken)
    {
        var validation = ValidateLoyaltyPerformanceFilter(filter);
        if (validation is not null) return BadRequest(new { status = false, message = validation });

        var zoneName = await _db.Divisions.AsNoTracking().Where(x => x.Id == filter.ZoneId).Select(x => x.DivisionName).FirstOrDefaultAsync(cancellationToken);
        if (zoneName is null) return BadRequest(new { status = false, message = "The selected zone was not found." });
        if (!await _db.LoyaltySchemes.AsNoTracking().AnyAsync(x => x.Id == filter.SchemeId && x.DeletedAt == null, cancellationToken))
            return BadRequest(new { status = false, message = "The selected scheme was not found." });

        var asrDesignationIds = await _db.Designations.AsNoTracking()
            .Where(x => x.DesignationName.Trim() == "ASR").Select(x => x.Id).ToListAsync(cancellationToken);
        // An ASR switched off since keeps the invoices raised under them in the report - the
        // row says so in Employee Status - unless the filter asks for one side only.
        var employeeStatus = Domain.Services.EmployeeStatus.Read(filter.EmployeeStatus);
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(CurrentUserId(), includeInactive: true, cancellationToken)).ToHashSet();
        var zoneAsrs = (await _db.Users.AsNoTracking()
                .Where(x => x.DesignationId.HasValue && asrDesignationIds.Contains(x.DesignationId.Value)
                    && x.DivisionId == filter.ZoneId && !x.IsDeleted && x.DeletedAt == null)
                .ToListAsync(cancellationToken))
            .Where(x => visibleIds.Contains(x.Id) && Domain.Services.EmployeeStatus.Matches(employeeStatus, x.Active))
            .ToDictionary(x => x.Id);

        var rangeStart = filter.StartDate.ToDateTime(TimeOnly.MinValue);
        var rangeEndExclusive = filter.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var invoices = await _db.NewInvoices.AsNoTracking()
            .Where(x => x.LoyaltySchemeId == filter.SchemeId && x.InvoiceDate >= rangeStart && x.InvoiceDate < rangeEndExclusive)
            .Select(x => new { x.Id, x.SecondaryCustomerId, x.DealerCustomerId, x.Amount, x.ApprovalStatus })
            .ToListAsync(cancellationToken);

        var retailerIds = invoices.Select(x => x.SecondaryCustomerId).Distinct().ToArray();
        var retailerFields = (await _db.Customers.AsNoTracking()
                .Where(x => retailerIds.Contains(x.Id))
                .Select(x => new { x.Id, x.CustomFields })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => CustomFieldsJson.Read(x.CustomFields));
        var employeeLists = retailerFields.ToDictionary(x => x.Key, x => ReadIdList(x.Value.GetValueOrDefault("employee_id")));
        var listedEmployeeIds = employeeLists.Values.SelectMany(x => x).Distinct().ToArray();
        var listedAsrIds = (await _db.Users.AsNoTracking()
                .Where(x => listedEmployeeIds.Contains(x.Id) && x.DesignationId.HasValue && asrDesignationIds.Contains(x.DesignationId.Value))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        // Decided on the whole list, before the zone is applied, so the same retailer can
        // never land under one ASR in one zone's report and another ASR in another's.
        var asrByRetailer = employeeLists
            .Select(x => (RetailerId: x.Key, AsrId: x.Value.FirstOrDefault(listedAsrIds.Contains)))
            .Where(x => x.AsrId != 0 && zoneAsrs.ContainsKey(x.AsrId))
            .ToDictionary(x => x.RetailerId, x => x.AsrId);

        ulong DealerOf(ulong? invoiceDealer, ulong retailerId)
        {
            if (invoiceDealer is > 0) return invoiceDealer.Value;
            var fields = retailerFields[retailerId];
            var domestic = ReadIdList(fields.GetValueOrDefault("distributor_name")).FirstOrDefault();
            return domestic != 0 ? domestic : ReadIdList(fields.GetValueOrDefault("agri_distributor")).FirstOrDefault();
        }

        var reportInvoices = invoices
            .Where(x => asrByRetailer.ContainsKey(x.SecondaryCustomerId))
            .Select(x => new
            {
                x.Id,
                x.SecondaryCustomerId,
                x.Amount,
                x.ApprovalStatus,
                AsrId = asrByRetailer[x.SecondaryCustomerId],
                DealerId = dealerWise ? DealerOf(x.DealerCustomerId, x.SecondaryCustomerId) : 0UL
            })
            .ToList();
        var hoInvoiceIds = reportInvoices.Where(x => x.ApprovalStatus == Domain.Entities.NewInvoice.StatusApprovedHo).Select(x => x.Id).ToArray();
        // The amount HO approved - the latest HO approval on the invoice, as the invoice
        // screens read it, falling back to the invoice amount where none was recorded.
        var hoAmounts = (await _db.NewInvoiceApprovalLogs.AsNoTracking()
                .Where(x => x.NewInvoiceId.HasValue && hoInvoiceIds.Contains(x.NewInvoiceId.Value)
                    && x.ToStatus == Domain.Entities.NewInvoice.StatusApprovedHo && x.ApprovedAmount.HasValue)
                .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                .Select(x => new { InvoiceId = x.NewInvoiceId!.Value, Amount = x.ApprovedAmount!.Value })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.InvoiceId)
            .ToDictionary(x => x.Key, x => x.First().Amount);

        var reportAsrIds = reportInvoices.Select(x => x.AsrId).Distinct().ToArray();
        // A line's segment is its product's segment in the product master - the same rule as the
        // order download. The segment stored on the line itself is not used: lines from April-May
        // were saved without one (and were left out), and a product moved to another segment
        // later must count under its new one. The stored value only counts for a deleted product.
        var salesLines = await (from order in _db.Orders.AsNoTracking()
                                join line in _db.OrderDetails.AsNoTracking() on (ulong?)order.Id equals line.OrderId
                                join productRow in _db.Products.AsNoTracking().IgnoreQueryFilters() on line.ProductId equals (ulong?)productRow.Id into products
                                from product in products.DefaultIfEmpty()
                                where order.ExecutiveId.HasValue && reportAsrIds.Contains(order.ExecutiveId.Value)
                                    && order.DeletedAt == null
                                    && order.OrderDate >= rangeStart && order.OrderDate < rangeEndExclusive
                                    && (!filter.SegmentId.HasValue
                                        || (product != null ? product.CategoryId : line.CategoryId) == filter.SegmentId)
                                group line by new { AsrId = order.ExecutiveId!.Value, DealerId = order.SellerId } into sales
                                select new { sales.Key.AsrId, sales.Key.DealerId, Total = sales.Sum(x => x.LineTotal) })
            .ToListAsync(cancellationToken);
        var salesByAsr = salesLines.GroupBy(x => x.AsrId).ToDictionary(x => x.Key, x => x.Sum(y => y.Total));
        var salesByDealerAsr = salesLines.Where(x => x.DealerId.HasValue)
            .GroupBy(x => (DealerId: x.DealerId!.Value, x.AsrId))
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Total));

        var branches = await _db.Branches.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
        var managerIds = reportAsrIds.Select(id => zoneAsrs[id].ReportingId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var managers = await _db.Users.AsNoTracking().Where(x => managerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var dealerIds = reportInvoices.Select(x => x.DealerId).Where(x => x > 0).Distinct().ToArray();
        // A dealer deleted since still carries its name on the invoices raised under it.
        var dealerNames = await _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(x => dealerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var rows = reportInvoices
            .GroupBy(x => (x.DealerId, x.AsrId))
            .Select(group =>
            {
                var asr = zoneAsrs[group.Key.AsrId];
                var activeRetailers = group.Select(x => x.SecondaryCustomerId).Distinct().ToList();
                return new LoyaltyReportRow(
                    BranchName(asr, branches),
                    dealerWise ? (group.Key.DealerId == 0 ? "No Dealer" : Name(dealerNames, group.Key.DealerId)) : null,
                    asr.Name,
                    Domain.Services.EmployeeStatus.Of(asr.Active),
                    Name(managers, asr.ReportingId),
                    dealerWise ? salesByDealerAsr.GetValueOrDefault((group.Key.DealerId, asr.Id)) : salesByAsr.GetValueOrDefault(asr.Id),
                    // Every invoice under the scheme in the range, whatever its approval stage -
                    // the same figure as Total Amount on the invoice screen.
                    group.Sum(x => x.Amount),
                    group.Where(x => x.ApprovalStatus == Domain.Entities.NewInvoice.StatusApprovedHo)
                        .Sum(x => hoAmounts.TryGetValue(x.Id, out var approved) ? approved : x.Amount),
                    activeRetailers.Count,
                    activeRetailers.Count(id => !KycApproved(retailerFields[id])));
            })
            .OrderBy(x => string.IsNullOrWhiteSpace(x.Branch) ? 1 : 0)
            .ThenBy(x => x.Branch, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DealerName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AsrName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(dealerWise ? "Dealer Wise" : "ASR Wise");
        var headers = dealerWise
            ? new[] { "Branch", "Dealer Name", "ASR Name", "Employee Status", "Reporting Mgr", "Primary Sales", "Act Sec Sales (Lac)", "Total Invoice Amount (Lac)", "Approved Invoice Val (Lac)", "No. of Active Retailers", "KYC Pending" }
            : new[] { "Branch", "ASR Name", "Employee Status", "Reporting Mgr", "Primary Sales", "Act Sec Sales (Lac)", "Total Invoice Amount (Lac)", "Approved Invoice Val (Lac)", "No. of Active Retailers", "KYC Pending" };
        // Everything left of Primary Sales names the row; the figures sit to its right.
        var labelColumns = dealerWise ? 5 : 4;
        // Two header rows: the last four columns sit under one "Loyalty Program Performance"
        // heading, and every other heading spans both rows.
        const int loyaltyColumns = 4;
        var loyaltyStart = headers.Length - loyaltyColumns + 1;
        for (var column = 1; column < loyaltyStart; column++)
        {
            sheet.Cell(1, column).Value = headers[column - 1];
            sheet.Range(1, column, 2, column).Merge();
        }
        sheet.Cell(1, loyaltyStart).Value = "Loyalty Program Performance";
        sheet.Range(1, loyaltyStart, 1, headers.Length).Merge();
        for (var column = loyaltyStart; column <= headers.Length; column++) sheet.Cell(2, column).Value = headers[column - 1];

        void WriteLine(int row, IEnumerable<object?> names, IReadOnlyCollection<LoyaltyReportRow> figures, bool isTotal)
        {
            WriteRow(sheet, row, names.Concat(new object?[]
            {
                null,
                ToLakh(figures.Sum(x => x.SecondarySales)),
                ToLakh(figures.Sum(x => x.TotalInvoiceAmount)),
                ToLakh(figures.Sum(x => x.ApprovedInvoiceValue)),
                figures.Sum(x => x.ActiveRetailers),
                figures.Sum(x => x.KycPending)
            }).ToList());
        }

        void WriteTotal(int row, string label, IReadOnlyCollection<LoyaltyReportRow> figures, XLColor color, bool whiteText)
        {
            WriteLine(row, new object?[] { label }.Concat(Enumerable.Repeat<object?>("", labelColumns - 1)), figures, isTotal: true);
            var range = sheet.Range(row, 1, row, headers.Length);
            range.Style.Fill.BackgroundColor = color;
            range.Style.Font.Bold = true;
            if (whiteText) range.Style.Font.FontColor = XLColor.White;
        }

        var outputRow = 3;
        foreach (var branchGroup in rows.GroupBy(x => x.Branch))
        {
            foreach (var row in branchGroup)
            {
                var names = dealerWise
                    ? new object?[] { row.Branch, row.DealerName, row.AsrName, row.EmployeeStatus, row.ReportingManager }
                    : new object?[] { row.Branch, row.AsrName, row.EmployeeStatus, row.ReportingManager };
                WriteLine(outputRow++, names, new[] { row }, isTotal: false);
            }
            WriteTotal(outputRow++, "SUBTOTAL - " + (string.IsNullOrWhiteSpace(branchGroup.Key) ? "No Branch" : branchGroup.Key), branchGroup.ToList(), XLColor.Yellow, whiteText: false);
        }
        WriteTotal(outputRow++, "ZONE TOTAL - " + zoneName, rows, XLColor.FromHtml("E53935"), whiteText: true);

        var headerRange = sheet.Range(1, 1, 2, headers.Length);
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("1E88E5");
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        headerRange.Style.Alignment.WrapText = true;
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        headerRange.Style.Border.OutsideBorderColor = XLColor.White;
        headerRange.Style.Border.InsideBorderColor = XLColor.White;
        sheet.Row(1).Height = 20;
        sheet.Row(2).Height = 25;
        var usedRange = sheet.RangeUsed();
        if (usedRange is not null)
        {
            usedRange.Style.Font.FontName = "Calibri";
            usedRange.Style.Font.FontSize = 9;
            var dataRange = sheet.Range(3, labelColumns + 1, outputRow - 1, headers.Length);
            dataRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            dataRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            sheet.Range(3, labelColumns + 2, outputRow - 1, labelColumns + 4).Style.NumberFormat.Format = "#,##0.00";
        }
        // Widths follow row 2 and the data, not the merged group heading across three columns.
        sheet.SheetView.FreezeRows(2); sheet.Columns().AdjustToContents(2, Math.Max(2, outputRow - 1), 8, 45);
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            dealerWise ? "Loyalty_Performance_Dealer_Wise.xlsx" : "Loyalty_Performance_ASR_Wise.xlsx");
    }

    private static string? ValidateLoyaltyPerformanceFilter(LoyaltyPerformanceFilter filter)
    {
        if (!filter.ZoneId.HasValue) return "Zone is required.";
        if (!filter.SchemeId.HasValue) return "Scheme is required.";
        if (filter.StartDate == default || filter.EndDate == default) return "Start date and end date are required.";
        if (filter.StartDate > filter.EndDate) return "Start date cannot be after end date.";
        return null;
    }

    /// <summary>The ids in a custom field, in the order they are written: "41949,42034".</summary>
    private static List<ulong> ReadIdList(string? value) =>
        System.Text.RegularExpressions.Regex.Matches(value ?? string.Empty, @"\d+")
            .Select(match => ulong.TryParse(match.Value, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

    /// <summary>KYC is complete only when all four documents are approved - the rule the
    /// KYC screen applies; anything short of that counts as pending.</summary>
    private static bool KycApproved(IReadOnlyDictionary<string, string?> fields) =>
        new[] { "gst_kyc_status", "pan_kyc_status", "aadhar_kyc_status", "bank_kyc_status" }
            .All(key => string.Equals(fields.GetValueOrDefault(key)?.Trim(), "approved", StringComparison.OrdinalIgnoreCase));

    /// <summary>Rupees to lakhs. Written unrounded and shown to two decimals by the cell
    /// format, so a subtotal is the exact sum of its rows rather than of rounded figures.</summary>
    private static decimal ToLakh(decimal rupees) => rupees / 100000m;

    private static bool UserHasBranch(Domain.Entities.User user, ulong branchId) => user.PrimaryBranchId == branchId ||
        (user.BranchId ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(x => ulong.TryParse(x, out var id) && id == branchId);
    private static string BranchName(Domain.Entities.User user, IReadOnlyDictionary<ulong, string> branches)
    {
        if (user.PrimaryBranchId.HasValue && branches.TryGetValue(user.PrimaryBranchId.Value, out var primary)) return primary;
        var first = (user.BranchId ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return ulong.TryParse(first, out var id) && branches.TryGetValue(id, out var name) ? name : string.Empty;
    }
    private static string Name(IReadOnlyDictionary<ulong, string> values, ulong? id) => id.HasValue && values.TryGetValue(id.Value, out var value) ? value : string.Empty;
    private static void WriteRow(IXLWorksheet sheet, int row, IReadOnlyList<object?> values) { for (var i = 0; i < values.Count; i++) sheet.Cell(row, i + 1).Value = XLCellValue.FromObject(values[i]); }
    private static void WriteTotal(IXLWorksheet sheet, int row, string label, IReadOnlyCollection<AsrPerformanceRow> rows, XLColor color)
    {
        var working = rows.Sum(x => x.WorkingDays); var target = rows.Sum(x => x.VisitTarget); var visited = rows.Sum(x => x.Visited); var productive = rows.Sum(x => x.Productive);
        WriteRow(sheet, row, new object?[] { "", label, "", "", working, target, visited, target > 0 ? $"{Math.Round(visited * 100m / target, 1)} %" : "0 %", productive, visited > 0 ? $"{Math.Round(productive * 100m / visited, 1)} %" : "0 %", rows.Sum(x => x.NewCounters), rows.Sum(x => x.OrderQty), rows.Sum(x => x.OrderValue), rows.Sum(x => x.UniqueSku), rows.Sum(x => x.Cumulative), "", "", "", "" });
        var range = sheet.Range(row, 1, row, 19); range.Style.Fill.BackgroundColor = color; range.Style.Font.Bold = true;
        if (color != XLColor.Yellow) range.Style.Font.FontColor = XLColor.White;
    }
}

public sealed record LoyaltyReportRow(string Branch, string? DealerName, string AsrName, string EmployeeStatus, string ReportingManager, decimal SecondarySales, decimal TotalInvoiceAmount, decimal ApprovedInvoiceValue, int ActiveRetailers, int KycPending);

public sealed class LoyaltyPerformanceFilter
{
    /// <summary>"Y", "N" or nothing at all - see Domain.Services.EmployeeStatus.</summary>
    [FromQuery(Name = "employee_status")] public string? EmployeeStatus { get; set; }
    [FromQuery(Name = "segment_id")] public ulong? SegmentId { get; set; }
    [FromQuery(Name = "zone_id")] public ulong? ZoneId { get; set; }
    [FromQuery(Name = "scheme_id")] public ulong? SchemeId { get; set; }
    [FromQuery(Name = "start_date")] public DateOnly StartDate { get; set; }
    [FromQuery(Name = "end_date")] public DateOnly EndDate { get; set; }
}

public sealed class AsrPerformanceFilter
{
    /// <summary>"Y", "N" or nothing at all - see Domain.Services.EmployeeStatus.</summary>
    [FromQuery(Name = "employee_status")] public string? EmployeeStatus { get; set; }
    [FromQuery(Name = "employee_id")] public ulong? EmployeeId { get; set; }
    [FromQuery(Name = "division_id")] public ulong? DivisionId { get; set; }
    [FromQuery(Name = "branch_id")] public ulong? BranchId { get; set; }
    [FromQuery(Name = "designation_id")] public ulong? DesignationId { get; set; }
    [FromQuery(Name = "start_date")] public DateOnly StartDate { get; set; }
    [FromQuery(Name = "end_date")] public DateOnly EndDate { get; set; }
}

public sealed class ProductivityFilter
{
    /// <summary>"Y", "N" or nothing at all - see Domain.Services.EmployeeStatus.</summary>
    [FromQuery(Name = "employee_status")] public string? EmployeeStatus { get; set; }
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
    /// <summary>"Y", "N" or nothing at all - see Domain.Services.EmployeeStatus.</summary>
    [FromQuery(Name = "employee_status")] public string? EmployeeStatus { get; set; }
    [FromQuery(Name = "designation_id")] public ulong? DesignationId { get; set; }
    [FromQuery(Name = "year")] public int? Year { get; set; }
    [FromQuery(Name = "month")] public int? Month { get; set; }
    [FromQuery(Name = "period")] public string? Period { get; set; }
    [FromQuery(Name = "division_id")] public ulong? DivisionId { get; set; }
    [FromQuery(Name = "zone_id")] public ulong? ZoneId { get => DivisionId; set => DivisionId = value; }
    [FromQuery(Name = "branch_id")] public ulong? BranchId { get; set; }
    [FromQuery(Name = "search")] public string? Search { get; set; }
    [FromQuery(Name = "page")] public int Page { get; set; } = 1;
    [FromQuery(Name = "page_size")] public int PageSize { get; set; } = 10;
}

internal sealed record RatingReportRow(ulong UserId, string Branch, string EmployeeCode, string EmployeeName, string EmployeeStatus, string ReportingManager, string Zone,
    decimal? LastFinalRating, decimal FinalRating, decimal MarketTarget, int MarketDays, decimal MarketRatio, decimal MarketRating, decimal VisitTarget, int Visits, decimal VisitRatio, decimal VisitRating,
    decimal SalesTarget, decimal SalesAchievement, decimal SalesRatio, decimal SalesRating, int Promotional, decimal PromotionalRatio,
    decimal PromotionalRating, decimal PromotionalTarget, int RegisteredRetailers, int ActiveRetailers, decimal ActiveRatio, decimal ActiveRatingRatio, decimal ActiveRating)
{
    public object?[] Values(bool includeLastRating)
    {
        var values = new List<object?> { Branch, EmployeeCode, EmployeeName, EmployeeStatus, ReportingManager, Zone };
        if (includeLastRating) values.Add(LastFinalRating ?? 0m);
        values.AddRange([FinalRating, MarketTarget, MarketDays, MarketRatio, MarketRatio, MarketRating, VisitTarget, Visits, VisitRatio, VisitRatio, VisitRating,
        SalesTarget, SalesAchievement, SalesRatio, SalesRatio, SalesRating, PromotionalTarget, Promotional, PromotionalRatio, PromotionalRatio,
        PromotionalRating, RegisteredRetailers, ActiveRetailers, ActiveRatio, ActiveRatingRatio, ActiveRating]);
        return values.ToArray();
    }
}

internal sealed record RatingReportCalculation(IReadOnlyList<RatingReportRow> Rows, DateTime Start, DateTime End, bool IsWeekly, bool IsYtd,
    decimal MarketWeight, decimal VisitWeight, decimal SalesWeight, decimal PromoWeight, decimal RetailerWeight);

internal sealed record RatingScores(decimal MarketRatio, decimal VisitRatio, decimal SalesRatio, decimal PromoRatio,
    decimal ActiveRatio, decimal ActiveRatingRatio, decimal MarketRating, decimal VisitRating, decimal SalesRating,
    decimal PromoRating, decimal ActiveRating, decimal FinalRating);

internal sealed record RatingTrendRow(ulong UserId, string Branch, string EmployeeCode, string EmployeeName, string EmployeeStatus,
    string ReportingManager, string Zone, decimal AverageRating, DateTime? DateOfJoining, DateTime RatingStartMonth,
    int AverageMonthCount, IReadOnlyDictionary<string, decimal> MonthlyRatings,
    IReadOnlyDictionary<string, RatingTrendMonthDetail> MonthlyDetails);

internal sealed record RatingTrendMonthDetail(decimal FinalRating, IReadOnlyList<RatingComponentDetail> Components);
internal sealed record RatingComponentDetail(string Key, string Label, decimal Percentage, decimal Actual, decimal Target,
    decimal Weight, string Description);
internal sealed record RetailerAssignmentPeriod(ulong UserId, ulong CustomerId, DateTime? AssignedAt, DateTime? UnassignedAt);
internal sealed record RetailerOrderActivity(ulong CustomerId, DateTime OrderDate);

public sealed record DealerPerformanceRow(Domain.Entities.Customer Dealer, Domain.Entities.User User, IReadOnlyDictionary<int, decimal> Monthly, string Zone, string Branch, string Reporting);
public sealed record PerformanceOrder(ulong? BuyerId, ulong? SellerId, ulong? ExecutiveId, ulong? CreatedBy, DateTime? OrderDate, long TotalQty, decimal GrandTotal);

public sealed record AsrPerformanceRow(ulong UserId, string UserName, string EmployeeStatus, int DailyTarget, int WorkingDays, int VisitTarget, int Visited, decimal Adherence, int Productive, decimal Productivity, int NewCounters, long OrderQty, decimal OrderValue, int UniqueSku, int Cumulative, string Zone, string Branch, string Designation, string ReportingManager)
{
    public object?[] Values() => [UserId, UserName, EmployeeStatus, DailyTarget, WorkingDays, VisitTarget, Visited, $"{Adherence} %", Productive, $"{Productivity} %", NewCounters, OrderQty, OrderValue, UniqueSku, Cumulative, Zone, Branch, Designation, ReportingManager];
}
