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
    private readonly ICustomerRepository _customers;
    public ReportManagementController(AppDbContext db, IHrRepository hr, ICustomerRepository customers) { _db = db; _hr = hr; _customers = customers; }

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
        else if (isYtd) { sheet.Cell("A1").Value = $"YTD {start:MMM yyyy} to {end.AddDays(-1):MMM yyyy}"; sheet.Cell("A1").Style.DateFormat.Format = "General"; }
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        var period = isWeekly ? $"Weekly_{start:yyyy-MM-dd}_to_{end.AddDays(-1):yyyy-MM-dd}"
            : isYtd ? $"YTD_{start:MMM-yyyy}_to_{end.AddDays(-1):MMM-yyyy}"
            : start.ToString("MMM_yyyy", CultureInfo.InvariantCulture);
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

    // ---------- Reports > Customers > RFM Report ----------
    // Recency, Frequency and Monetary value for every active retailer that has ordered at
    // least once. There is no listing - the file is the report.

    /// <summary>The RFM filter bar: Zone, Branch, State and District. A dropdown feed, so
    /// ungated like the other report options above - the download carries the permission.
    /// Each branch names its zone and each district its state, so the second list follows
    /// whatever the first is set to.</summary>
    [HttpGet("rfm/options")]
    public async Task<IActionResult> RfmOptions(CancellationToken ct)
    {
        var zones = (await _db.Divisions.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .Select(x => new { id = x.Id, name = x.DivisionName }).ToListAsync(ct)).ByZone(x => x.name).ToList();
        var branches = await BranchOptionRowsAsync(ct);
        var states = await _db.States.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .OrderBy(x => x.StateName).Select(x => new { id = x.Id, name = x.StateName }).ToListAsync(ct);
        var districts = await _db.Districts.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .OrderBy(x => x.DistrictName).Select(x => new { id = x.Id, name = x.DistrictName, state_id = x.StateId }).ToListAsync(ct);
        return Ok(new { zones, branches, states, districts });
    }

    /// <summary>
    /// The RFM report.
    ///
    /// One row per active retailer that has placed at least one order - a retailer with no
    /// order has no recency, frequency or monetary value to score, so it is not in the file.
    /// Every order the retailer has ever placed counts; the three figures are measured over
    /// the whole history, not over a period.
    ///
    /// Each of R, F and M is scored 1 to 5 by splitting the retailers that made it into the
    /// report into five equal groups of 20%: ordered most recent first, most orders first
    /// and highest value first, the leading fifth scores 5 and the trailing fifth 1. Rating
    /// Code is the three digits side by side (a "555" is the best customer on all three),
    /// Total Rating their sum, and Rating % what that sum is worth out of the 15 on offer.
    ///
    /// Zone and Branch come from the retailer's assigned employee, State from the retailer's
    /// own address, and the dealer from the customer master - the dealer the retailer is
    /// mapped to, never the seller on an order.
    /// </summary>
    [HttpGet("rfm/retailer-export")]
    [RequirePermission("rfm_report.export_retailer")]
    public async Task<IActionResult> ExportRfmRetailerWise([FromQuery] RfmFilter filter, CancellationToken ct)
    {
        var (rows, _) = await RfmRetailersAsync(filter, ct);
        var scored = ScoreRfm(rows, x => x.CustomerId, x => x.RecencyDays, x => x.Frequency, x => x.Monetary);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("RFM Retailer Wise");
        var headers = new[] { "Retailer ID", "Retailer Name", "Number", "Dealer Code", "Dealer Name", "State", "Dealer City",
            "ASR / DSR", "Recency (Days)", "R Rating", "Frequency (Orders)", "F Rating", "Monetary Value", "M Rating",
            "Total Rating", "Rating %", "Category" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var rowNumber = 2;
        foreach (var item in scored)
            WriteRow(sheet, rowNumber++, new object?[] { item.Row.CustomerId, item.Row.Name, item.Row.Mobile, item.Row.DealerCode,
                item.Row.DealerName, item.Row.State, item.Row.DealerCity, item.Row.AsrDsr, item.Row.RecencyDays, item.R,
                item.Row.Frequency, item.F, item.Row.Monetary, item.M, item.Total, item.Percent, RfmCategory(item.Percent) });
        // Orders and rupees add up; a rating, a percentage and a category do not.
        var totalRow = rowNumber;
        WriteTotalRow(sheet, totalRow, headers.Length, new object?[] { "TOTAL", $"{scored.Count:N0} retailers", "", "", "", "", "", "",
            "", "", scored.Sum(x => x.Row.Frequency), "", scored.Sum(x => x.Row.Monetary), "", "", "", "" });
        StyleSimpleExport(sheet, headers.Length, totalRow, "D9E1F2");
        sheet.Range(2, 13, totalRow, 13).Style.NumberFormat.Format = "0.00";   // Monetary Value
        if (rowNumber > 2) sheet.Range(2, 15, rowNumber - 1, 15).Style.NumberFormat.Format = "0.0";    // Total Rating
        WriteRfmLogicSheet(workbook, dealerWise: false, scored);
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"RFM_Report_Retailer_Wise_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }

    /// <summary>
    /// The same retailers read a level up: one row per dealer, saying how its book is doing.
    ///
    /// There is no rating at the dealer level. Each retailer keeps the category it earned on
    /// the retailer-wise sheet - scored against every other retailer in the file - and this
    /// sheet counts how many of a dealer's retailers landed in each one. So the five category
    /// columns always add up to Active Retailers.
    ///
    /// Total Registered counts every retailer on the dealer's book, switched off or never
    /// ordered included; Active Retailers counts only the ones this report scores.
    ///
    /// A retailer that names no dealer in the customer master cannot be attributed to one and
    /// is left out of this sheet; it is still on the retailer-wise one.
    /// </summary>
    [HttpGet("rfm/dealer-export")]
    [RequirePermission("rfm_report.export_dealer")]
    public async Task<IActionResult> ExportRfmDealerWise([FromQuery] RfmFilter filter, CancellationToken ct)
    {
        var (retailers, registered) = await RfmRetailersAsync(filter, ct);
        var scored = ScoreRfm(retailers, x => x.CustomerId, x => x.RecencyDays, x => x.Frequency, x => x.Monetary);

        var rows = scored.Where(x => x.Row.DealerId.HasValue)
            .GroupBy(x => x.Row.DealerId!.Value)
            .Select(group =>
            {
                var first = group.First().Row;
                int InCategory(string name) => group.Count(x => RfmCategory(x.Percent) == name);
                return new
                {
                    DealerId = group.Key,
                    first.DealerCode,
                    Name = first.DealerName,
                    first.DealerState,
                    first.DealerCity,
                    AsrDsr = first.DealerAsrDsr,
                    Registered = registered.GetValueOrDefault(group.Key),
                    Active = group.Count(),
                    OrderValue = group.Sum(x => x.Row.Monetary),
                    Platinum = InCategory("Platinum"),
                    Diamond = InCategory("Diamond"),
                    Gold = InCategory("Gold"),
                    Silver = InCategory("Silver"),
                    Bronze = InCategory("Bronze"),
                };
            })
            .OrderByDescending(x => x.OrderValue).ThenBy(x => x.Name).ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("RFM Dealer Wise");
        var headers = new[] { "Dealer Code", "Dealer Name", "State", "City", "ASR / DSR", "Total Registered Retailers",
            "Active Retailers", "Order Value (Lac)", "Platinum", "Diamond", "Gold", "Silver", "Bronze" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var rowNumber = 2;
        foreach (var item in rows)
            WriteRow(sheet, rowNumber++, new object?[] { item.DealerCode, item.Name, item.DealerState, item.DealerCity,
                item.AsrDsr, item.Registered, item.Active, ToLakh(item.OrderValue),
                item.Platinum, item.Diamond, item.Gold, item.Silver, item.Bronze });
        // Every column here is a count or a value, so the whole row adds up. The order
        // value is totalled in rupees and converted once, not summed from rounded lakhs.
        var totalRow = rowNumber;
        WriteTotalRow(sheet, totalRow, headers.Length, new object?[] { "TOTAL", $"{rows.Count:N0} dealers", "", "", "",
            rows.Sum(x => x.Registered), rows.Sum(x => x.Active), ToLakh(rows.Sum(x => x.OrderValue)),
            rows.Sum(x => x.Platinum), rows.Sum(x => x.Diamond), rows.Sum(x => x.Gold), rows.Sum(x => x.Silver), rows.Sum(x => x.Bronze) });
        StyleSimpleExport(sheet, headers.Length, totalRow, "D9E1F2");
        sheet.Range(2, 8, totalRow, 8).Style.NumberFormat.Format = "0.00";   // Order Value, in lakhs
        WriteRfmLogicSheet(workbook, dealerWise: true, scored, rows.Count);
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"RFM_Report_Dealer_Wise_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }

    /// <summary>
    /// The retailers both sheets are built from.
    ///
    /// <c>Rows</c> is the report's population - switched on, not deleted, and with at least
    /// one order - each with its recency, order count, order value and dealer. <c>Registered</c>
    /// counts every retailer on a dealer's book that got through the same filters and the
    /// same visibility scope, whether or not it is switched on or has ever ordered, which is
    /// what the dealer sheet's Total Registered column reports.
    /// </summary>

    /// <summary>
    /// RFM Movement: how each retailer's standing changed over the chosen month.
    ///
    /// Both sides are read from the beginning, not month by month. Pick August and the
    /// report scores everything up to the end of July, scores everything up to the end of
    /// August, and compares the two.
    ///
    /// A running total can only grow, so nothing here falls in rupees or in order count.
    /// What falls is the retailer's place among the others: stand still while the rest of
    /// the market keeps ordering and the 1-to-5 rating slides, and the category with it.
    /// That is what the report is for - it says who is being left behind.
    ///
    /// A retailer whose first order ever lands in the chosen month reads "No Order Yet" on
    /// the earlier side. One that has never ordered at all is not in the file.
    /// </summary>
    [HttpGet("rfm/movement-export")]
    [RequirePermission("rfm_movement_report.export")]
    public async Task<IActionResult> ExportRfmMovement([FromQuery] RfmMovementFilter filter, CancellationToken ct)
    {
        if (filter.Year is null or < 2000 or > 2100) return BadRequest(new { status = false, message = "Please select a year." });
        if (filter.Month is null or < 1 or > 12) return BadRequest(new { status = false, message = "Please select a month." });

        var currentStart = new DateTime(filter.Year.Value, filter.Month.Value, 1);
        var currentEnd = currentStart.AddMonths(1);
        var previousStart = currentStart.AddMonths(-1);

        // Everything up to the end of the chosen month, and everything up to the end of the
        // month before it - both counted from the first order the system holds.
        var current = await RfmSnapshotAsync(filter, currentEnd, ct);
        var previous = await RfmSnapshotAsync(filter, currentStart, ct);

        var rows = current.Keys.Union(previous.Keys).Select(id =>
        {
            current.TryGetValue(id, out var now);
            previous.TryGetValue(id, out var before);
            var name = now?.Row.Name ?? before!.Row.Name;
            var mobile = now?.Row.Mobile ?? before!.Row.Mobile;
            var dealer = now?.Row.DealerName ?? before!.Row.DealerName;
            var state = now?.Row.State ?? before!.Row.State;
            var dealerCity = now?.Row.DealerCity ?? before!.Row.DealerCity;
            var asrDsr = FirstFilled(now?.Row.AsrDsr, before?.Row.AsrDsr);
            var beforeCategory = before is null ? NoOrder : RfmCategory(before.Percent);
            var nowCategory = now is null ? NoOrder : RfmCategory(now.Percent);
            return new
            {
                Id = id, Name = name, Mobile = mobile, Dealer = dealer, State = state, DealerCity = dealerCity, AsrDsr = asrDsr,
                Previous = beforeCategory, Current = nowCategory,
                R = Movement(before?.R, now?.R), F = Movement(before?.F, now?.F), M = Movement(before?.M, now?.M),
                Direction = CategoryMovement(beforeCategory, nowCategory),
                Reason = MovementReason(before, now),
                Rank = CategoryRank(nowCategory),
                // How far it moved, in category steps. A Platinum that has gone quiet has
                // fallen five; a Gold that slipped to Silver has fallen one.
                Swing = CategoryRank(nowCategory) - CategoryRank(beforeCategory),
            };
        })
        .OrderBy(x => x.Direction == "Downgraded" ? 0 : x.Direction == "Upgraded" ? 1 : 2)
        // Biggest fall at the top of the downgrades, biggest climb at the top of the
        // upgrades, and the weakest first among the rows that did not move.
        .ThenBy(x => x.Direction == "Upgraded" ? -x.Swing : x.Swing)
        .ThenBy(x => x.Rank).ThenBy(x => x.Name).ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("RFM Movement");
        // The two snapshot columns carry their month, so the file reads on its own once it
        // has been mailed on and the screen that made it is forgotten.
        var headers = new[] { "Retailer ID", "Retailer Name", "Number", "Dealer Name", "State", "Dealer City", "ASR / DSR",
            $"Previous ({previousStart:MMM-yy})", $"Current ({currentStart:MMM-yy})",
            "R Change", "F Change", "M Change", "Movement", "Reason" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var rowNumber = 2;
        foreach (var item in rows)
            WriteRow(sheet, rowNumber++, new object?[] { item.Id, item.Name, item.Mobile, item.Dealer, item.State, item.DealerCity,
                item.AsrDsr, item.Previous, item.Current, item.R, item.F, item.M, item.Direction, item.Reason });
        // Nothing on this sheet is a quantity, so the total row counts instead of summing:
        // how many retailers are in the file, and how the month went for them.
        var upgraded = rows.Count(x => x.Direction == "Upgraded");
        var downgraded = rows.Count(x => x.Direction == "Downgraded");
        var totalRow = rowNumber;
        WriteTotalRow(sheet, totalRow, headers.Length, new object?[] { "TOTAL", $"{rows.Count:N0} retailers", "", "", "", "", "",
            "", "", "", "", "", $"{upgraded:N0} upgraded, {downgraded:N0} downgraded, {rows.Count - upgraded - downgraded:N0} no change", "" });
        StyleSimpleExport(sheet, headers.Length, totalRow, "D9E1F2");
        WriteRfmMovementLogicSheet(workbook, previousStart, currentStart, currentEnd, rows.Count,
            previous.Count, current.Count);
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"RFM_Movement_{currentStart:MMM-yyyy}_vs_{previousStart:MMM-yyyy}_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }


    /// <summary>
    /// RFM Activation, ASR wise: how much of each ASR's book has been activated by the end
    /// of the chosen month, and how many of those were activated during it.
    ///
    /// Read the same way as the Movement sheet - from the beginning, not month by month. A
    /// retailer counts as active once it has placed an order, and stays active after that.
    /// </summary>
    [HttpGet("rfm/activation-asr-export")]
    [RequirePermission("rfm_activation_report.export_asr")]
    public Task<IActionResult> ExportRfmActivationAsrWise([FromQuery] RfmMovementFilter filter, CancellationToken ct)
        => ExportRfmActivation(filter, byDealer: false, ct);

    /// <summary>The same count read against the dealer each retailer is mapped to.</summary>
    [HttpGet("rfm/activation-dealer-export")]
    [RequirePermission("rfm_activation_report.export_dealer")]
    public Task<IActionResult> ExportRfmActivationDealerWise([FromQuery] RfmMovementFilter filter, CancellationToken ct)
        => ExportRfmActivation(filter, byDealer: true, ct);

    private async Task<IActionResult> ExportRfmActivation(RfmMovementFilter filter, bool byDealer, CancellationToken ct)
    {
        if (filter.Year is null or < 2000 or > 2100) return BadRequest(new { status = false, message = "Please select a year." });
        if (filter.Month is null or < 1 or > 12) return BadRequest(new { status = false, message = "Please select a month." });

        var currentStart = new DateTime(filter.Year.Value, filter.Month.Value, 1);
        var currentEnd = currentStart.AddMonths(1);
        var previousStart = currentStart.AddMonths(-1);

        var retailers = await RfmFilteredRetailersAsync(filter, ct);
        // Everyone who had ordered by the end of the chosen month, and everyone who had
        // ordered by the end of the month before it. The difference is who was won during it.
        var activatedByNow = await BuyersUpToAsync(currentEnd, ct);
        var activatedBefore = await BuyersUpToAsync(currentStart, ct);

        // Whose book the retailer sits on: the employee it is assigned to, or the dealer the
        // customer master maps it to.
        var employees = (await _db.Users.AsNoTracking().Where(x => x.DeletedAt == null)
            .Select(x => new { x.Id, x.Name, x.Active }).ToListAsync(ct))
            .ToDictionary(x => x.Id);
        // A switched-off ASR keeps the retailers still filed under them, so the row stays and
        // says so - the same rule the ASR and rating reports follow.
        var employeeStatus = Domain.Services.EmployeeStatus.Read(filter.EmployeeStatus);
        var dealers = (await _db.Customers.AsNoTracking().Where(x => x.CustomerType == 1 && x.DeletedAt == null)
            .Select(x => new { x.Id, x.Name, x.CustomerCode, x.CustomFields, x.ExecutiveId }).ToListAsync(ct))
            .ToDictionary(x => x.Id);
        var states = byDealer ? await _db.States.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.StateName, ct) : [];
        var cities = byDealer ? await _db.Cities.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.CityName, ct) : [];
        // The dealer sheet names the field person each dealer sits under; the ASR sheet is
        // already keyed by one, so it needs none.
        var dealerFieldNames = byDealer
            ? await AsrOrDsrNamesAsync(dealers.Values.Select(x => (x.Id, x.CustomFields, x.ExecutiveId)), ct)
            : [];

        (ulong? Id, string Name, string Status, string State, string City, string AsrDsr) Owner(Domain.Entities.Customer customer)
        {
            if (byDealer)
            {
                var dealerId = JsonULong(customer.CustomFields, "distributor_name");
                if (!dealerId.HasValue || !dealers.TryGetValue(dealerId.Value, out var dealer)) return (null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
                return (dealerId,
                    FirstFilled(JsonString(dealer.CustomFields, "legal_name"), JsonString(dealer.CustomFields, "shop_name"), dealer.Name),
                    string.Empty,
                    // Read the way the customer master reads them - the billing pair only as a fallback.
                    Name(states, JsonULong(dealer.CustomFields, "state_id") ?? JsonULong(dealer.CustomFields, "billing_state")),
                    Name(cities, JsonULong(dealer.CustomFields, "city_id") ?? JsonULong(dealer.CustomFields, "billing_city")),
                    dealerFieldNames.GetValueOrDefault(dealerId.Value, string.Empty));
            }
            var employeeId = AssignedEmployee(customer);
            if (!employeeId.HasValue || !employees.TryGetValue(employeeId.Value, out var employee)) return (null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
            if (!Domain.Services.EmployeeStatus.Matches(employeeStatus, employee.Active)) return (null, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
            return (employeeId, employee.Name, Domain.Services.EmployeeStatus.Of(employee.Active), string.Empty, string.Empty, string.Empty);
        }

        var rows = retailers
            .Select(customer => new { Customer = customer, Owner = Owner(customer) })
            .Where(x => x.Owner.Id.HasValue)
            .GroupBy(x => x.Owner.Id!.Value)
            .Select(group =>
            {
                var total = group.Count();
                var active = group.Count(x => activatedByNow.Contains(x.Customer.Id));
                var newlyActivated = group.Count(x => activatedByNow.Contains(x.Customer.Id) && !activatedBefore.Contains(x.Customer.Id));
                return new
                {
                    Name = group.First().Owner.Name,
                    Status = group.First().Owner.Status,
                    group.First().Owner.State,
                    group.First().Owner.City,
                    group.First().Owner.AsrDsr,
                    Total = total,
                    Active = active,
                    Inactive = total - active,
                    NewActivated = newlyActivated,
                    // Written as a fraction and shown as a percentage by the cell format, so
                    // the column can be sorted and averaged rather than only read.
                    Activation = total == 0 ? 0m : (decimal)active / total,
                };
            })
            .OrderByDescending(x => x.Total).ThenBy(x => x.Name).ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(byDealer ? "Activation Dealer Wise" : "Activation ASR Wise");
        var headers = byDealer
            ? new[] { "Dealer", "State", "City", "ASR / DSR", "Total Retailers", $"Active ({currentStart:MMM-yy})", $"Inactive ({currentStart:MMM-yy})", $"New Activated ({currentStart:MMM-yy})", "Activation %" }
            : new[] { "ASR", "Employee Status", "Total Retailers", $"Active ({currentStart:MMM-yy})", $"Inactive ({currentStart:MMM-yy})", $"New Activated ({currentStart:MMM-yy})", "Activation %" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        var rowNumber = 2;
        foreach (var item in rows)
            WriteRow(sheet, rowNumber++, byDealer
                ? new object?[] { item.Name, item.State, item.City, item.AsrDsr, item.Total, item.Active, item.Inactive, item.NewActivated, item.Activation }
                : new object?[] { item.Name, item.Status, item.Total, item.Active, item.Inactive, item.NewActivated, item.Activation });

        // The counts add up; the percentage does not. The total line is the whole book's
        // activation - every active retailer over every retailer - not the average of the
        // rows above it, which would weigh a book of five the same as a book of five
        // hundred.
        var totalRetailers = rows.Sum(x => x.Total);
        var totalActive = rows.Sum(x => x.Active);
        var overall = totalRetailers == 0 ? 0m : (decimal)totalActive / totalRetailers;
        var totalRow = rowNumber;
        WriteTotalRow(sheet, totalRow, headers.Length, byDealer
            ? new object?[] { "TOTAL", $"{rows.Count:N0} dealers", "", "", totalRetailers, totalActive, rows.Sum(x => x.Inactive), rows.Sum(x => x.NewActivated), overall }
            : new object?[] { "TOTAL", $"{rows.Count:N0} ASRs", totalRetailers, totalActive, rows.Sum(x => x.Inactive), rows.Sum(x => x.NewActivated), overall });
        StyleSimpleExport(sheet, headers.Length, totalRow, "D9E1F2");
        sheet.Range(2, headers.Length, totalRow, headers.Length).Style.NumberFormat.Format = "0.0%";
        WriteRfmActivationLogicSheet(workbook, byDealer, previousStart, currentStart, currentEnd, rows.Count,
            rows.Sum(x => x.Total), rows.Sum(x => x.Active), rows.Sum(x => x.NewActivated));
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        var who = byDealer ? "Dealer" : "ASR";
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"RFM_Activation_{who}_Wise_{currentStart:MMM-yyyy}_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }


    /// <summary>The Logic sheet the activation report carries.</summary>
    private static void WriteRfmActivationLogicSheet(XLWorkbook workbook, bool byDealer, DateTime previousStart,
        DateTime currentStart, DateTime currentEnd, int rowCount, int totalRetailers, int activeRetailers, int newlyActivated)
    {
        var sheet = workbook.Worksheets.Add("Logic");
        var row = 1;
        void Title(string text)
        {
            sheet.Cell(row, 1).Value = text;
            sheet.Range(row, 1, row, 2).Merge().Style.Font.SetBold().Font.SetFontSize(14);
            row++;
        }
        void Section(string text)
        {
            row++;
            sheet.Cell(row, 1).Value = text;
            var range = sheet.Range(row, 1, row, 2).Merge();
            range.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("D9E1F2"));
            row++;
        }
        void Line(string label, string text)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 1).Style.Font.SetBold().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            sheet.Cell(row, 2).Value = text;
            sheet.Cell(row, 2).Style.Alignment.SetWrapText(true).Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            row++;
        }

        var owner = byDealer ? "dealer" : "ASR";
        var current = currentStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var previous = previousStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        Title("RFM Activation - how this file is built");
        Line("Sheet", byDealer ? "Dealer Wise - one row per dealer." : "ASR Wise - one row per ASR.");
        Line("Counted up to", $"The end of {current}. New Activated compares that with the end of {previous}.");
        Line("Downloaded", DateTime.Now.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture));

        Section("1. Whose book each row is");
        Line(byDealer ? "Dealer" : "ASR", byDealer
            ? "The dealer the retailer is mapped to in the customer master. A retailer that names no dealer cannot be attributed to one and is left out of this sheet."
            : "The employee the retailer is assigned to. A retailer with nobody assigned is left out of this sheet.");
        if (byDealer)
            Line("State, City", "The dealer's own state and city, read from its record in the customer master. A dealer that still carries an old billing city is shown by the city on its record, which is what the CRM screen shows.");
        Line("Filters", "If a Zone, Branch, State or District was chosen on the screen, only the matching retailers are counted - in every column.");
        if (!byDealer)
            Line("Employee Status", "Y for an ASR still switched on, N for one switched off. A switched-off ASR keeps the retailers still filed under them, so the row stays and says so - otherwise a book of 255 retailers would quietly vanish from the report. The Employee Status filter on the screen narrows to one or the other; it shows all by default.");
        Line("Who may see what", "The file only ever contains the customers the person downloading it is allowed to see: an admin sees all, a branch manager their branch, everyone else their own reporting line.");

        Section("2. What each column counts");
        Line("Total Retailers", $"Every retailer on this {owner}'s book that got through the filters - switched off ones and ones that have never ordered included. This is the book, not the working part of it.");
        Line("Active", $"How many of them had placed at least one order by the end of {current} - counted from the beginning, not from the start of the month. Once a retailer orders it stays active. This is not the switch on the customer master.");
        Line("Inactive", $"The rest: Total Retailers minus Active. They are on the book and have never placed an order. The two always add back to Total.");
        Line("New Activated", $"Of the Active ones, how many were still at nil at the end of {previous}. In other words, retailers whose very first order landed in {current}.");
        Line("Activation %", "Active out of Total Retailers. 92 of 120 reads 76.7%.");
        if (byDealer) Line("ASR / DSR", "The field person the dealer is assigned to in the customer master. An assignment is a list, not one name, so a dealer can carry an ASR and a manager at once: the ASR is the one shown. Where there is no ASR on the list the DSR is shown instead, and a dealer assigned to neither is left blank rather than filled with whoever happens to be first.");
        Line("TOTAL row", "The last row of the sheet. The four counts add up straight down. The Activation % on that row is the whole book's - every active retailer over every retailer - not the average of the rows above it, which would weigh a book of five the same as a book of five hundred.");

        Section("3. Reading it");
        Line("A high Activation %", $"Most of the {owner}'s book has been opened. A low one with a large book is where the work is - those retailers were registered and never bought anything.");
        Line("New Activated on its own", "How many first-time buyers the month brought in. Activation % can only climb over time; New Activated says whether it climbed this month.");
        Line("Row order", "Largest book first. Sort the sheet on any column in Excel to read it another way.");

        Section("4. This file in numbers");
        Line(byDealer ? "Dealers listed" : "ASRs listed", rowCount.ToString("N0", CultureInfo.InvariantCulture));
        Line("Retailers on their books", totalRetailers.ToString("N0", CultureInfo.InvariantCulture));
        Line($"Activated by end of {current}", activeRetailers.ToString("N0", CultureInfo.InvariantCulture));
        Line($"Of those, first ordered in {current}", newlyActivated.ToString("N0", CultureInfo.InvariantCulture));
        Line("Overall activation", totalRetailers == 0 ? "-" : $"{(decimal)activeRetailers * 100 / totalRetailers:0.0}%");

        sheet.Column(1).Width = 26;
        sheet.Column(2).Width = 105;
        sheet.Rows().AdjustToContents();
    }

    /// <summary>Every retailer that had placed an order by <paramref name="upTo"/>.</summary>
    private async Task<HashSet<ulong>> BuyersUpToAsync(DateTime upTo, CancellationToken ct) =>
        (await _db.Orders.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.BuyerId.HasValue && x.OrderDate.HasValue && x.OrderDate < upTo)
            .Select(x => x.BuyerId!.Value).Distinct().ToListAsync(ct))
        .ToHashSet();

    private const string NoOrder = "No Order Yet";

    /// <summary>The Logic sheet the movement report carries: what the two months are, how a
    /// month is scored on its own, and what each of the change columns is saying.</summary>
    private static void WriteRfmMovementLogicSheet(XLWorkbook workbook, DateTime previousStart,
        DateTime currentStart, DateTime currentEnd, int rowCount, int previousCount, int currentCount)
    {
        var sheet = workbook.Worksheets.Add("Logic");
        var row = 1;
        void Title(string text)
        {
            sheet.Cell(row, 1).Value = text;
            sheet.Range(row, 1, row, 2).Merge().Style.Font.SetBold().Font.SetFontSize(14);
            row++;
        }
        void Section(string text)
        {
            row++;
            sheet.Cell(row, 1).Value = text;
            var range = sheet.Range(row, 1, row, 2).Merge();
            range.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("D9E1F2"));
            row++;
        }
        void Line(string label, string text)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 1).Style.Font.SetBold().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            sheet.Cell(row, 2).Value = text;
            sheet.Cell(row, 2).Style.Alignment.SetWrapText(true).Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            row++;
        }

        var current = currentStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var previous = previousStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        Title("RFM Movement - how this file is built");
        Line("Comparing", $"Where each retailer stood at the end of {current} against where it stood at the end of {previous}.");
        Line("Downloaded", DateTime.Now.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture));

        Section("1. The two snapshots");
        Line("Current", $"Every order placed up to {currentEnd.AddDays(-1):dd MMM yyyy} - the end of {current}. Counted from the very first order the system holds, not from the start of the month.");
        Line("Previous", $"Every order placed up to {currentStart.AddDays(-1):dd MMM yyyy} - the end of {previous}, read the same way.");
        Line("Why from the beginning", $"The question is where the retailer stands, not what it did in one month. So {current} is scored against the whole history behind it and compared with where the same retailer stood a month earlier.");
        Line("Filters", "If a Zone, Branch, State or District was chosen on the screen, both snapshots are narrowed the same way, and the ratings are worked out inside that smaller group.");

        Section("2. Who gets a row");
        Line("Has ordered before", "The retailer is scored in both snapshots and the two are compared.");
        Line("First order this month", $"It still gets a row, reading \"{NoOrder}\" on the earlier side - there was nothing to score it on yet.");
        Line("Never ordered", "Not in this file at all - there is nothing to compare.");

        Section("3. How a snapshot is scored");
        Line("The three figures", "Up to that date: how long since the retailer's last order, how many orders it has placed in all, and what they have come to in all.");
        Line("Rating 1 to 5", "Every retailer in the snapshot is lined up on each figure, best first, and cut into five equal groups of 20%. The leading fifth scores 5, the last fifth 1.");
        Line("A rating is a place, not a total", "This is the part worth understanding. A running total never falls - nobody's order count or value goes down. What falls is the retailer's place in the line. Keep ordering at the same pace while the market speeds up and the rating slides anyway, and that is a real warning.");
        Line("Category", $"Total Rating is R x {RecencyWeight:0.00} + F x {FrequencyWeight:0.00} + M x {MonetaryWeight:0.00} out of {RfmMaxRating:0.0}, turned into a percentage: 80 and above Platinum, 60 to 79 Diamond, 40 to 59 Gold, 20 to 39 Silver, below that Bronze. The same ladder the other two RFM sheets use.");

        Section("4. What the change columns say");
        Line("R Change, F Change, M Change", "Up, Down or Same - which way that one rating moved between the two snapshots. Down means the retailer lost ground against the others on that figure, not that its total fell. A dash means it had nothing to be scored on a month ago.");
        Line("ASR / DSR", "The field person the retailer is assigned to in the customer master. An assignment is a list, not one name, so a retailer can carry an ASR and a manager at once: the ASR is the one shown. Where there is no ASR on the list the DSR is shown instead, and a retailer assigned to neither is left blank rather than filled with whoever happens to be first.");
        Line("Movement", "Where the category went: Upgraded, Downgraded or No Change. It follows the category, not the decimals, so a retailer can slip a little inside Gold and still read No Change.");
        Line("Reason", "Only the parts that actually moved, in plain words - so a row that reads \"Order frequency dropped\" moved for that reason and not for the other two.");
        Line("TOTAL row", "The last row of the sheet. Nothing on this sheet is a quantity, so it counts rather than adds: how many retailers are in the file, and how many of them were upgraded, downgraded or stood still over the month.");

        Section("5. Row order");
        Line("Worst news first", "Downgraded rows first, then Upgraded, then No Change; inside each, the weakest category first. The retailers needing a call are at the top.");

        Section("6. This file in numbers");
        Line("Rows", rowCount.ToString("N0", CultureInfo.InvariantCulture));
        Line($"Scored at end of {current}", currentCount.ToString("N0", CultureInfo.InvariantCulture));
        Line($"Scored at end of {previous}", previousCount.ToString("N0", CultureInfo.InvariantCulture));

        sheet.Column(1).Width = 26;
        sheet.Column(2).Width = 105;
        sheet.Rows().AdjustToContents();
    }

    /// <summary>Every order up to <paramref name="upTo"/>, scored - the retailers as they
    /// stood on that date.</summary>
    private async Task<Dictionary<ulong, RfmScore<RfmRow>>> RfmSnapshotAsync(RfmFilter filter, DateTime upTo, CancellationToken ct)
    {
        var (rows, _) = await RfmRetailersAsync(filter, ct, null, upTo);
        return ScoreRfm(rows, x => x.CustomerId, x => x.RecencyDays, x => x.Frequency, x => x.Monetary)
            .ToDictionary(x => x.Row.CustomerId);
    }

    /// <summary>Which way one of the three ratings went. A month the retailer sat out has no
    /// rating to compare, so it reads as a dash rather than as a fall to nothing.</summary>
    private static string Movement(int? before, int? now) =>
        before is null || now is null ? "-" : now > before ? "Up" : now < before ? "Down" : "Same";

    private static int CategoryRank(string category) => category switch
    {
        "Platinum" => 5, "Diamond" => 4, "Gold" => 3, "Silver" => 2, "Bronze" => 1, _ => 0
    };

    private static string CategoryMovement(string before, string now)
    {
        var (from, to) = (CategoryRank(before), CategoryRank(now));
        return to > from ? "Upgraded" : to < from ? "Downgraded" : "No Change";
    }

    /// <summary>Why the row moved, in the words a field manager would use. Only the parts
    /// that actually changed are named, so the reason reads as the cause and not as a
    /// restatement of the three columns beside it.</summary>
    private static string MovementReason(RfmScore<RfmRow>? before, RfmScore<RfmRow>? now)
    {
        if (before is null) return "First order ever came in this month - nothing to compare against";
        if (now is null) return "No order yet";

        var fell = new List<string>();
        var rose = new List<string>();
        void Note(int was, int isNow, string down, string up)
        {
            if (isNow < was) fell.Add(down);
            else if (isNow > was) rose.Add(up);
        }
        // A running total never falls, so a falling rating means the market moved and this
        // retailer did not. The wording says that rather than claiming the figure dropped.
        Note(before.R, now.R, "gone longer without ordering", "ordered more recently");
        Note(before.F, now.F, "order frequency slipped behind others", "gained ground on order frequency");
        Note(before.M, now.M, "order value slipped behind others", "gained ground on order value");

        if (fell.Count == 0 && rose.Count == 0) return "No change in any of the three";
        var parts = new List<string>();
        if (fell.Count > 0) parts.Add(Join(fell));
        if (rose.Count > 0) parts.Add(Join(rose));
        var sentence = string.Join(", but ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..];

        static string Join(IReadOnlyList<string> items) => items.Count switch
        {
            1 => items[0],
            2 => $"{items[0]} and {items[1]}",
            _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}"
        };
    }

    /// <summary>
    /// Every retailer the screen's filters and the caller's own scope leave standing -
    /// switched off ones and ones that have never ordered included. What each report does
    /// with them after that is its own business: the RFM sheets narrow to the ones that
    /// order, the activation sheet counts the whole book against them.
    /// </summary>
    private async Task<List<Domain.Entities.Customer>> RfmFilteredRetailersAsync(RfmFilter filter, CancellationToken ct)
    {
        var actor = CurrentUserId();
        var retailers = await _db.Customers.AsNoTracking()
            .Where(x => x.CustomerType == 2 && x.DeletedAt == null)
            .ToListAsync(ct);

        // The same scope the Customers screen applies - admin everything, a branch manager
        // their branch, everyone else their own downline - read through the one method that
        // owns that rule rather than restated here.
        var visible = (await _customers.FilterVisibleCustomerIdsAsync(actor, retailers.Select(x => x.Id).ToArray(), ct)).ToHashSet();
        retailers = retailers.Where(x => visible.Contains(x.Id)).ToList();

        if (filter.ZoneId.HasValue || filter.BranchId.HasValue)
        {
            var employees = (await _db.Users.AsNoTracking().Where(x => x.DeletedAt == null)
                .Select(x => new { x.Id, x.DivisionId, x.PrimaryBranchId, x.BranchId }).ToListAsync(ct))
                .ToDictionary(x => x.Id);
            retailers = retailers.Where(customer =>
            {
                var employeeId = AssignedEmployee(customer);
                if (!employeeId.HasValue || !employees.TryGetValue(employeeId.Value, out var employee)) return false;
                if (filter.ZoneId.HasValue && employee.DivisionId != filter.ZoneId) return false;
                return !filter.BranchId.HasValue || EmployeeBranchIds(employee.PrimaryBranchId, employee.BranchId).Contains(filter.BranchId.Value);
            }).ToList();
        }

        if (filter.StateId.HasValue || filter.DistrictId.HasValue)
        {
            var places = await AddressPlacesAsync(ct);
            ulong? StateOf(Domain.Entities.Customer customer) => JsonULong(customer.CustomFields, "state_id")
                ?? JsonULong(customer.CustomFields, "billing_state")
                ?? (places.TryGetValue(customer.Id, out var place) ? place.StateId : null);
            ulong? DistrictOf(Domain.Entities.Customer customer) => JsonULong(customer.CustomFields, "district_id")
                ?? JsonULong(customer.CustomFields, "billing_district")
                ?? (places.TryGetValue(customer.Id, out var place) ? place.DistrictId : null);
            if (filter.StateId.HasValue) retailers = retailers.Where(x => StateOf(x) == filter.StateId).ToList();
            if (filter.DistrictId.HasValue) retailers = retailers.Where(x => DistrictOf(x) == filter.DistrictId).ToList();
        }

        return retailers;
    }

    /// <param name="from">Start of the window to measure, or null for every order ever.</param>
    /// <param name="to">One past the end of that window.</param>
    private async Task<(List<RfmRow> Rows, Dictionary<ulong, int> Registered)> RfmRetailersAsync(
        RfmFilter filter, CancellationToken ct, DateTime? from = null, DateTime? to = null)
    {
        var actor = CurrentUserId();

        // Aggregated in the database in one pass: on live this is the whole orders table.
        var orderStats = (await _db.Orders.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.BuyerId.HasValue && x.OrderDate.HasValue
                && (from == null || x.OrderDate >= from) && (to == null || x.OrderDate < to))
            .GroupBy(x => x.BuyerId!.Value)
            .Select(g => new { BuyerId = g.Key, Orders = g.Count(), LastOrder = g.Max(x => x.OrderDate!.Value), Value = g.Sum(x => x.GrandTotal) })
            .ToListAsync(ct))
            .ToDictionary(x => x.BuyerId);

        var retailers = await RfmFilteredRetailersAsync(filter, ct);

        var states = await _db.States.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.StateName, ct);
        var cities = await _db.Cities.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.CityName, ct);
        // The dealer's code and name are read from the customer master - the dealer this
        // retailer is mapped to - exactly as the customer export reads them.
        var dealers = (await _db.Customers.AsNoTracking().Where(x => x.CustomerType == 1 && x.DeletedAt == null)
            .Select(x => new { x.Id, x.Name, x.CustomerCode, x.CustomFields, x.ExecutiveId }).ToListAsync(ct))
            .ToDictionary(x => x.Id);

        static ulong? DealerOf(Domain.Entities.Customer customer) => JsonULong(customer.CustomFields, "distributor_name");
        var registered = retailers
            .Select(DealerOf)
            .Where(id => id.HasValue && dealers.ContainsKey(id.Value))
            .GroupBy(id => id!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        // The report itself is only the switched-on retailers that have actually ordered.
        retailers = retailers.Where(x => x.Active == "Y" && orderStats.ContainsKey(x.Id)).ToList();

        // The retailer's own state, for its column - read the same way the state filter
        // reads it, custom fields first and the saved address behind them.
        var places = await AddressPlacesAsync(ct);
        ulong? StateOf(Domain.Entities.Customer customer) => JsonULong(customer.CustomFields, "state_id")
            ?? JsonULong(customer.CustomFields, "billing_state")
            ?? (places.TryGetValue(customer.Id, out var place) ? place.StateId : null);

        // Who each row is reported under. Both sides are needed: the retailer-wise sheet
        // names the retailer's own ASR, the dealer-wise sheet names the dealer's.
        var fieldNames = await AsrOrDsrNamesAsync(
            retailers.Select(x => (x.Id, x.CustomFields, x.ExecutiveId))
                .Concat(dealers.Values.Select(x => (x.Id, x.CustomFields, x.ExecutiveId))), ct);

        // Recency is counted back from the end of the window being read: for a past month
        // that is the last day of that month, never today, or every row in it would read as
        // months stale.
        var today = DateTime.Today;
        var asOf = to.HasValue && to.Value.AddDays(-1) < today ? to.Value.AddDays(-1) : today;
        var rows = retailers.Select(customer =>
        {
            var stats = orderStats[customer.Id];
            var dealerId = JsonULong(customer.CustomFields, "distributor_name");
            var hasDealer = dealerId.HasValue && dealers.TryGetValue(dealerId.Value, out _);
            dealers.TryGetValue(dealerId ?? 0, out var dealer);
            return new RfmRow(
                customer.Id,
                FirstFilled(customer.Name, JsonString(customer.CustomFields, "shop_name")),
                FirstFilled(customer.Mobile, customer.ContactNumber),
                hasDealer ? dealerId : null,
                dealer is null ? string.Empty : FirstFilled(dealer.CustomerCode, JsonString(dealer.CustomFields, "distributor_code")),
                dealer is null ? string.Empty : FirstFilled(JsonString(dealer.CustomFields, "legal_name"), JsonString(dealer.CustomFields, "shop_name"), dealer.Name),
                // The dealer's own state and city, read the way the customer master reads
                // them - state_id and city_id first, the billing pair only as a fallback.
                // Some dealers carry a stale billing_city: Balia Traders holds Nalanda there
                // and Patna in city_id, and Patna is what its record shows.
                dealer is null ? string.Empty : Name(states, JsonULong(dealer.CustomFields, "state_id") ?? JsonULong(dealer.CustomFields, "billing_state")),
                dealer is null ? string.Empty : Name(cities, JsonULong(dealer.CustomFields, "city_id") ?? JsonULong(dealer.CustomFields, "billing_city")),
                Name(states, StateOf(customer)),
                Math.Max(0, (int)(asOf - stats.LastOrder.Date).TotalDays),
                stats.Orders,
                stats.Value,
                fieldNames.GetValueOrDefault(customer.Id, string.Empty),
                dealer is null ? string.Empty : fieldNames.GetValueOrDefault(dealer.Id, string.Empty));
        }).ToList();
        return (rows, registered);
    }

    /// <summary>What each of the three is worth in the Total Rating. Monetary value counts
    /// for half, frequency for a third and recency for a fifth, so two retailers on the same
    /// 1-to-5 ratings are separated by where those ratings sit.</summary>
    private const decimal RecencyWeight = 0.20m, FrequencyWeight = 0.30m, MonetaryWeight = 0.50m;

    /// <summary>The best anything can score: a 5 on all three, weighted - 5x0.2 + 5x0.3 +
    /// 5x0.5. Rating % is what a Total Rating is worth out of this.</summary>
    private const decimal RfmMaxRating = 5m;

    /// <summary>
    /// The "Logic" sheet both downloads carry.
    ///
    /// Written for whoever opens the file rather than for a developer: it says who is in
    /// the report, what each figure measures, how a 1 to 5 rating is arrived at and how
    /// that becomes a category - so a row can be checked without asking anyone. The last
    /// section counts this particular file, so the explanation and the numbers cannot
    /// drift apart.
    /// </summary>
    private static void WriteRfmLogicSheet<T>(XLWorkbook workbook, bool dealerWise, IReadOnlyList<RfmScore<T>> scored, int dealerCount = 0)
    {
        var sheet = workbook.Worksheets.Add("Logic");
        // Both sheets are scored over retailers; the dealer sheet only groups them.
        const string subject = "retailer";
        const string subjects = "retailers";
        var row = 1;

        void Title(string text)
        {
            sheet.Cell(row, 1).Value = text;
            sheet.Range(row, 1, row, 2).Merge().Style.Font.SetBold().Font.SetFontSize(14);
            row++;
        }
        void Section(string text)
        {
            row++;
            sheet.Cell(row, 1).Value = text;
            var range = sheet.Range(row, 1, row, 2).Merge();
            range.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("D9E1F2"));
            range.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Center);
            row++;
        }
        void Line(string label, string text)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 1).Style.Font.SetBold().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            sheet.Cell(row, 2).Value = text;
            sheet.Cell(row, 2).Style.Alignment.SetWrapText(true).Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            row++;
        }

        Title("RFM Report - how this file is built");
        Line("Sheet", dealerWise ? "Dealer Wise - one row per dealer, counting how its retailers scored." : "Retailer Wise - one row per retailer.");
        Line("Downloaded", DateTime.Now.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture));

        Section("1. Who is in this report");
        Line("Retailers counted", "Every retailer that is switched on in the customer master, has not been deleted, and has placed at least one order. A retailer with no order has no recency, no frequency and no value to measure, so it is not listed at all.");
        if (dealerWise)
            Line("Dealers listed", "Every dealer that has at least one such retailer mapped to it. A dealer whose retailers have all gone quiet does not appear. A retailer whose customer master names no dealer cannot be attributed to one, so it is left out of this sheet - it is still on the Retailer Wise sheet.");
        Line("Filters", "If a Zone, Branch, State or District was chosen on the screen, only the matching retailers are in the file, and every rating below is worked out inside that smaller group - not against the whole country.");
        Line("Who may see what", "The file only ever contains the customers the person downloading it is allowed to see: an admin sees all, a branch manager their branch, everyone else their own reporting line.");

        Section("2. The three figures");
        Line("Measured per retailer", dealerWise
            ? "Even on this sheet the three figures belong to the retailer. Nothing is rated at dealer level - the dealer's row only counts how its retailers came out."
            : "Each row's three figures are that retailer's own.");
        Line("Recency (Days)", "Days between today and the retailer's most recent order. Fewer days is better.");
        Line("Frequency (Orders)", "How many orders the retailer has placed. More is better.");
        Line("Monetary Value", "The total value of those orders in rupees. More is better.");
        Line("Period covered", "Every order ever placed counts - the report is not tied to a month or a year. Cancelled and deleted orders are not counted.");

        Section("3. How a rating of 1 to 5 is given");
        Line("The method", $"Take all {subjects} in this file and stand them in a line on one figure, best first. Cut that line into five equal groups of 20%. The first group scores 5, the next 4, and so on down to 1 for the last group. This is done three times - once for Recency, once for Frequency, once for Monetary Value.");
        Line("What a 5 means", $"A 5 is a position, not a target. It means this {subject} is in the leading fifth of the {subjects} in this file on that figure. Change the filters and the same {subject} can score differently, because it is being compared with a different set.");
        Line("Rows on a cut line", "Because the five groups are kept equal in size, two rows with exactly the same figure can occasionally fall either side of a cut and differ by one rating point. This happens only at the four cut lines, nowhere else.");

        Section("4. Total Rating and Rating %");
        Line("The three do not count equally", $"Money counts for half, how often for a third, how recent for a fifth: R is worth {RecencyWeight * 100:0}%, F {FrequencyWeight * 100:0}% and M {MonetaryWeight * 100:0}%. Two rows on the same three ratings therefore land in the same place, but a strong M lifts a row further than a strong R does.");
        Line("What each is worth", $"A rating of 5 is worth {5 * RecencyWeight:0.0} on R, {5 * FrequencyWeight:0.0} on F and {5 * MonetaryWeight:0.0} on M. A rating of 1 is worth {RecencyWeight:0.0#}, {FrequencyWeight:0.0#} and {MonetaryWeight:0.0#}. These parts are not shown as columns on the sheet - only the total they add up to.");
        Line("Total Rating", $"Those three added up: R x {RecencyWeight:0.00} + F x {FrequencyWeight:0.00} + M x {MonetaryWeight:0.00}. The lowest possible is {1 * RecencyWeight + 1 * FrequencyWeight + 1 * MonetaryWeight:0.0} (a 1 on each) and the highest is {RfmMaxRating:0.0} (a 5 on each).");
        Line("Rating %", $"Total Rating out of the {RfmMaxRating:0.0} on offer, rounded to a whole number. A Total Rating of 4.8 reads {(int)Math.Round(4.8m * 100m / RfmMaxRating, MidpointRounding.AwayFromZero)}%, a 4.6 reads {(int)Math.Round(4.6m * 100m / RfmMaxRating, MidpointRounding.AwayFromZero)}%.");
        Line("Worked example", $"R 3, F 5, M 5 becomes {3 * RecencyWeight:0.0} + {5 * FrequencyWeight:0.0} + {5 * MonetaryWeight:0.0} = {3 * RecencyWeight + 5 * FrequencyWeight + 5 * MonetaryWeight:0.0}, which is {(int)Math.Round((3 * RecencyWeight + 5 * FrequencyWeight + 5 * MonetaryWeight) * 100m / RfmMaxRating, MidpointRounding.AwayFromZero)}% - {RfmCategory((int)Math.Round((3 * RecencyWeight + 5 * FrequencyWeight + 5 * MonetaryWeight) * 100m / RfmMaxRating, MidpointRounding.AwayFromZero))}.");

        Section("5. How the Category is decided");
        Line("Rating %", "Category");
        sheet.Cell(row - 1, 2).Style.Font.SetBold();
        foreach (var (band, name) in new[] { ("80 to 100", "Platinum"), ("60 to 79", "Diamond"), ("40 to 59", "Gold"), ("20 to 39", "Silver"), ("1 to 19", "Bronze") })
        {
            sheet.Cell(row, 1).Value = band;
            sheet.Cell(row, 2).Value = name;
            row++;
        }
        Line("Worth knowing", $"The lowest Rating % anything can reach is {(int)Math.Round((1 * RecencyWeight + 1 * FrequencyWeight + 1 * MonetaryWeight) * 100m / RfmMaxRating, MidpointRounding.AwayFromZero)}% - a 1 on each of the three figures - so nothing falls into Bronze while the ratings are scored this way. The weakest {subjects} sit in Silver.");

        Section("6. Where the other columns come from");
        if (dealerWise)
        {
            Line("Dealer Code, Dealer Name", "From the dealer's own record in the customer master.");
            Line("State, City", "The dealer's own state and city - its billing address first, its plain address where no billing one is set. Not the retailers'.");
            Line("Total Registered Retailers", "Every retailer on this dealer's book: switched off ones and ones that have never ordered are counted too. Only the screen's filters and what the person downloading may see narrow it.");
            Line("Active Retailers", "How many of those retailers this report actually scores - switched on, not deleted, and with at least one order.");
            Line("Order Value (Lac)", "The order value of those active retailers added together, shown in lakhs - rupees divided by 1,00,000. The registered-but-quiet ones contribute nothing.");
            Line("Platinum to Bronze", "How many of the dealer's active retailers landed in each category on the Retailer Wise sheet. These five always add up to Active Retailers - a dealer with 20 in Platinum and 2 in Silver has a strong book; the other way round does not.");
            Line("ASR / DSR", "The field person the dealer is assigned to in the customer master. An assignment is a list, not one name, so a dealer can carry an ASR and a manager at once: the ASR is the one shown. Where there is no ASR on the list the DSR is shown instead, and a dealer assigned to neither is left blank rather than filled with whoever happens to be first.");
            Line("TOTAL row", "The last row of the sheet. Every column on this sheet is a count or a value, so all of them add up; the order value is totalled in rupees and converted to lakhs once, so it will not drift from the column above it the way adding rounded lakhs would.");
            Line("Row order", "Highest Order Value first.");
        }
        else
        {
            Line("Dealer Code, Dealer Name", "The dealer this retailer is mapped to in the customer master. It is not read from who sold the order.");
            Line("State", "The retailer's own address.");
            Line("Dealer City", "The city on the dealer's own record in the customer master - its billing city, or its city where no billing city is set. It is the dealer's city, not the retailer's.");
            Line("Number", "The retailer's mobile number from the customer master.");
            Line("ASR / DSR", "The field person the retailer is assigned to in the customer master. An assignment is a list, not one name, so a retailer can carry an ASR and a manager at once: the ASR is the one shown. Where there is no ASR on the list the DSR is shown instead, and a retailer assigned to neither is left blank rather than filled with whoever happens to be first.");
            Line("TOTAL row", "The last row of the sheet: how many retailers are in the file, their orders added up and their order value added up. A rating, a Rating % and a category cannot be added down a column, so those cells are left empty rather than carrying a figure that would be read as meaning something.");
        }
        Line("Zone and Branch filters", "Read from the employee the retailer is assigned to - their zone, and their branch.");

        Section("7. This file in numbers");
        if (dealerWise) Line("Dealers listed", dealerCount.ToString("N0", CultureInfo.InvariantCulture));
        Line(dealerWise ? "Retailers behind them" : "Retailers listed", scored.Count.ToString("N0", CultureInfo.InvariantCulture));
        Line("R Rating spread", Spread(scored.Select(x => x.R)));
        Line("F Rating spread", Spread(scored.Select(x => x.F)));
        Line("M Rating spread", Spread(scored.Select(x => x.M)));
        Line("Category spread", string.Join(", ", new[] { "Platinum", "Diamond", "Gold", "Silver", "Bronze" }
            .Select(name => new { name, count = scored.Count(x => RfmCategory(x.Percent) == name) })
            .Where(x => x.count > 0)
            .Select(x => $"{x.name} {x.count:N0}")));

        sheet.Column(1).Width = 26;
        sheet.Column(2).Width = 105;
        sheet.Rows().AdjustToContents();
    }

    private static string Spread(IEnumerable<int> ratings)
    {
        var counted = ratings.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        return string.Join(", ", Enumerable.Range(1, 5).Reverse().Select(rating => $"{rating} = {counted.GetValueOrDefault(rating):N0}"));
    }

    /// <summary>What a Rating % is called. The lowest anything on this report can score is
    /// 20% - a 1 on each of the three - so Bronze only appears if the rating scale itself
    /// changes.</summary>
    private static string RfmCategory(int percent) => percent switch
    {
        >= 80 => "Platinum",
        >= 60 => "Diamond",
        >= 40 => "Gold",
        >= 20 => "Silver",
        _ => "Bronze"
    };

    /// <summary>R, F and M scored over whatever the sheet counts - retailers on one,
    /// dealers on the other - and the three put together into a code, a total and a
    /// percentage. Rows come back best first.</summary>
    private static List<RfmScore<T>> ScoreRfm<T>(IReadOnlyList<T> rows, Func<T, ulong> key,
        Func<T, int> recencyDays, Func<T, int> frequency, Func<T, decimal> monetary)
    {
        var recency = RfmQuintiles(rows.OrderBy(recencyDays).ThenBy(key).Select(key).ToList());
        var orders = RfmQuintiles(rows.OrderByDescending(frequency).ThenBy(key).Select(key).ToList());
        var value = RfmQuintiles(rows.OrderByDescending(monetary).ThenBy(key).Select(key).ToList());
        return rows.Select(row =>
        {
            var id = key(row);
            var (r, f, m) = (recency[id], orders[id], value[id]);
            var (rWeighted, fWeighted, mWeighted) = (r * RecencyWeight, f * FrequencyWeight, m * MonetaryWeight);
            var total = rWeighted + fWeighted + mWeighted;
            // Out of the 5 on offer, so a 4.8 reads 96%.
            return new RfmScore<T>(row, r, f, m, rWeighted, fWeighted, mWeighted, total,
                (int)Math.Round(total * 100m / RfmMaxRating, MidpointRounding.AwayFromZero));
        }).OrderByDescending(x => x.Total).ThenByDescending(x => monetary(x.Row)).ToList();
    }

    /// <summary>1 to 5 over keys already ordered best first: the leading fifth scores 5,
    /// the trailing fifth 1. Ties can fall either side of a 20% line - the groups are equal
    /// in size, which is what an RFM quintile is.</summary>
    private static Dictionary<ulong, int> RfmQuintiles(IReadOnlyList<ulong> ordered)
    {
        var scores = new Dictionary<ulong, int>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++) scores[ordered[i]] = 5 - (int)((long)i * 5 / ordered.Count);
        return scores;
    }

    /// <summary>Who a customer is assigned to, read from the indexed computed columns: the
    /// employee named in custom_fields, then the sales executive, then executive_id.</summary>
    /// <summary>
    /// The field person a customer is reported under: its ASR, and where it has none, its DSR.
    ///
    /// An assignment is a list, not one name - custom_fields.employee_id holds the whole
    /// chain, so a retailer can carry an ASR and a TM at once, and a few carry a DSR
    /// instead. The ASR is who the field is read by, so it wins wherever both are present.
    /// The DSR stands in only where there is no ASR, and a customer with neither is left
    /// blank rather than filled with whoever happens to be first on the list - on the
    /// current book that is 46 retailers out of 16,079 and 73 dealers out of 482.
    /// </summary>
    private async Task<Dictionary<ulong, string>> AsrOrDsrNamesAsync(
        IEnumerable<(ulong Id, string? CustomFields, ulong? ExecutiveId)> customers, CancellationToken ct)
    {
        var assigned = new Dictionary<ulong, List<ulong>>();
        foreach (var customer in customers)
        {
            if (assigned.ContainsKey(customer.Id)) continue;
            var fields = CustomFieldsJson.Read(customer.CustomFields);
            var ids = ReadIdList(fields.GetValueOrDefault("employee_id"))
                .Concat(ReadIdList(fields.GetValueOrDefault("sales_executive_id")))
                .Distinct().ToList();
            // executive_id is the fallback the customer master itself falls back to, and
            // only when custom_fields names nobody at all.
            if (ids.Count == 0 && customer.ExecutiveId is > 0) ids.Add(customer.ExecutiveId.Value);
            assigned[customer.Id] = ids;
        }

        var listed = assigned.Values.SelectMany(x => x).Distinct().ToArray();
        if (listed.Length == 0) return assigned.ToDictionary(x => x.Key, _ => string.Empty);

        var designations = await _db.Designations.AsNoTracking()
            .Select(x => new { x.Id, x.DesignationName }).ToListAsync(ct);
        static bool Named(string? value, string title) => string.Equals(value?.Trim(), title, StringComparison.OrdinalIgnoreCase);
        var asrDesignations = designations.Where(x => Named(x.DesignationName, "ASR")).Select(x => x.Id).ToHashSet();
        var dsrDesignations = designations.Where(x => Named(x.DesignationName, "DSR")).Select(x => x.Id).ToHashSet();

        var users = (await _db.Users.AsNoTracking().Where(x => listed.Contains(x.Id))
            .Select(x => new { x.Id, x.Name, x.DesignationId }).ToListAsync(ct))
            .ToDictionary(x => x.Id);

        string FirstWith(List<ulong> ids, IReadOnlySet<ulong> wanted) => ids
            .Select(id => users.GetValueOrDefault(id))
            .Where(user => user?.DesignationId is not null && wanted.Contains(user.DesignationId.Value))
            .Select(user => user!.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty;

        return assigned.ToDictionary(
            x => x.Key,
            x => FirstFilled(FirstWith(x.Value, asrDesignations), FirstWith(x.Value, dsrDesignations)));
    }

    /// <summary>
    /// The bold row that closes a report sheet.
    ///
    /// Only the columns that genuinely add up carry a number. A rating, a percentage or a
    /// category cannot be summed down a column, so those cells are left empty rather than
    /// filled with a figure that would be read as meaning something.
    /// </summary>
    private static void WriteTotalRow(IXLWorksheet sheet, int row, int columns, IReadOnlyList<object?> values)
    {
        WriteRow(sheet, row, values);
        var range = sheet.Range(row, 1, row, columns);
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("FFF2CC");
        range.Style.Font.Bold = true;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
    }

    private static ulong? AssignedEmployee(Domain.Entities.Customer customer) =>
        customer.AssignedEmployeeId ?? customer.AssignedSalesExecutiveId ?? customer.AssignedFallbackEmployeeId ?? customer.ExecutiveId;

    private static IEnumerable<ulong> EmployeeBranchIds(ulong? primaryBranchId, string? branchId)
    {
        if (primaryBranchId.HasValue) return [primaryBranchId.Value];
        return (branchId ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => ulong.TryParse(x, out var id) ? id : 0).Where(x => x > 0);
    }

    /// <summary>State and district from the customer's first saved address, for the
    /// retailers whose custom_fields carry neither.</summary>
    private async Task<Dictionary<ulong, (ulong? StateId, ulong? DistrictId)>> AddressPlacesAsync(CancellationToken ct)
    {
        var rows = await Query(@"SELECT a.customer_id, a.state_id, a.district_id
FROM addresses a
INNER JOIN (SELECT customer_id, MIN(id) AS id FROM addresses WHERE deleted_at IS NULL AND customer_id IS NOT NULL GROUP BY customer_id) first
        ON first.id = a.id", ct);
        return rows.ToDictionary(
            row => ULong(row, "customer_id"),
            row => (Obj(row, "state_id") is null ? (ulong?)null : ULong(row, "state_id"),
                    Obj(row, "district_id") is null ? (ulong?)null : ULong(row, "district_id")));
    }

    private static string FirstFilled(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

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
        var isFinancialYear = string.Equals(filter.Period, "fy", StringComparison.OrdinalIgnoreCase);
        if (!filter.DesignationId.HasValue) return "Designation is required.";
        if (!string.IsNullOrWhiteSpace(filter.Period) && !isWeekly && !isFinancialYear) return "A valid period is required.";
        if (isFinancialYear && filter.Month.HasValue) return "A financial year covers every month, so a month cannot be chosen with it.";
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
        // Two ways to read a whole year: the calendar one, January to December, and the
        // financial one, April of the chosen year to the following March.
        var isFinancialYear = string.Equals(filter.Period, "fy", StringComparison.OrdinalIgnoreCase);
        var isYtd = !isWeekly && !filter.Month.HasValue;
        var yearStart = isFinancialYear
            ? new DateTime(filter.Year!.Value, 4, 1)
            : new DateTime(filter.Year!.Value, 1, 1);
        var yearEnd = yearStart.AddYears(1);
        var start = isWeekly ? indiaToday.AddDays(-7) : (filter.Month.HasValue ? new DateTime(filter.Year!.Value, filter.Month.Value, 1) : yearStart);
        // A month that has not finished yet ends today, so it is scored on the days that
        // have actually happened rather than on a whole month the employee has not had. A
        // year still running is read the same way, up to today; a year already over is read whole.
        var monthEndsToday = filter.Month.HasValue && filter.Year == indiaToday.Year && filter.Month == indiaToday.Month;
        var yearIsRunning = indiaToday >= yearStart && indiaToday < yearEnd;
        var end = isWeekly ? indiaToday
            : filter.Month.HasValue ? (monthEndsToday ? indiaToday.AddDays(1) : start.AddMonths(1))
            : yearIsRunning ? indiaToday.AddDays(1) : yearEnd;
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

/// <summary>One retailer on the RFM report, before its three figures are scored.</summary>
public sealed record RfmRow(ulong CustomerId, string Name, string Mobile, ulong? DealerId, string DealerCode,
    string DealerName, string DealerState, string DealerCity, string State, int RecencyDays, int Frequency, decimal Monetary,
    /// <summary>The retailer's own ASR, or its DSR where it has no ASR.</summary>
    string AsrDsr = "",
    /// <summary>The same, read for the dealer this retailer is mapped to.</summary>
    string DealerAsrDsr = "");

/// <summary>A report row with its three ratings and what they add up to.</summary>
public sealed record RfmScore<T>(T Row, int R, int F, int M,
    decimal RWeighted, decimal FWeighted, decimal MWeighted, decimal Total, int Percent);

public class RfmFilter
{
    [FromQuery(Name = "zone_id")] public ulong? ZoneId { get; set; }
    [FromQuery(Name = "branch_id")] public ulong? BranchId { get; set; }
    [FromQuery(Name = "state_id")] public ulong? StateId { get; set; }
    [FromQuery(Name = "district_id")] public ulong? DistrictId { get; set; }
}

/// <summary>The movement report needs a month to stand on as well; both are required.</summary>
public sealed class RfmMovementFilter : RfmFilter
{
    [FromQuery(Name = "year")] public int? Year { get; set; }
    [FromQuery(Name = "month")] public int? Month { get; set; }
    /// <summary>"Y", "N" or nothing at all - see Domain.Services.EmployeeStatus. Only the
    /// ASR-wise activation sheet lists employees, so only that one reads it.</summary>
    [FromQuery(Name = "employee_status")] public string? EmployeeStatus { get; set; }
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
