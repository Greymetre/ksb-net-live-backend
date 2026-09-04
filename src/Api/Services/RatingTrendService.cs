using System.Data;
using System.Globalization;
using Domain.Services;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>The six-month rating trend, computed once for everyone who shows it.
///
/// The CRM report and the field app both display these ratings. Working them out in two
/// places is how the visit count ended up meaning one thing in the rating report and
/// another in the ASR performance report, so the calculation lives here and both callers
/// read it from the same method.</summary>
public sealed class RatingTrendService
{
    private readonly AppDbContext _db;

    public RatingTrendService(AppDbContext db) => _db = db;

    /// <summary>Ratings for the given users over the six months ending with the current one.
    /// The caller decides who those users are - that is where role scope is applied, or
    /// deliberately not applied for the all-India leaderboard.</summary>
    public async Task<RatingTrendResult> CalculateAsync(
        IReadOnlyCollection<RatingTrendUser> users,
        CancellationToken cancellationToken)
    {
        var indiaToday = DateTime.UtcNow.AddHours(5).AddMinutes(30).Date;
        var currentMonth = new DateTime(indiaToday.Year, indiaToday.Month, 1);
        var monthStarts = Enumerable.Range(0, 6).Select(index => currentMonth.AddMonths(index - 5)).ToArray();
        var daysInCurrentMonth = DateTime.DaysInMonth(indiaToday.Year, indiaToday.Month);
        // The month in progress is scored on the days that have actually happened, the same
        // way the weekly report spreads a month target across its seven days.
        var currentMonthShare = (decimal)indiaToday.Day / daysInCurrentMonth;
        var rangeStart = monthStarts[0];
        var rangeEnd = currentMonth.AddMonths(1);

        if (users.Count == 0)
        {
            return new RatingTrendResult([], monthStarts, currentMonth, indiaToday, rangeStart, rangeEnd);
        }

        var userIds = users.Select(x => x.Id).ToArray();
        var targetYears = monthStarts.Select(x => x.Year).Distinct().ToArray();
        var targetMonths = monthStarts
            .SelectMany(x => new[] { x.ToString("MMM", CultureInfo.InvariantCulture), x.ToString("MMMM", CultureInfo.InvariantCulture) })
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var attendance = await _db.Attendances.AsNoTracking()
            .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value)
                && x.PunchinDate >= rangeStart && x.PunchinDate < rangeEnd && x.DeletedAt == null)
            .Select(x => new { UserId = x.UserId!.Value, x.PunchinDate, x.WorkingType })
            .ToListAsync(cancellationToken);

        var targets = await _db.SalesTargetUsers.AsNoTracking()
            .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value)
                && x.Year.HasValue && targetYears.Contains(x.Year.Value) && x.Month != null && targetMonths.Contains(x.Month))
            .Select(x => new { UserId = x.UserId!.Value, x.Year, x.Month, x.Target })
            .ToListAsync(cancellationToken);

        var orders = await _db.Orders.AsNoTracking()
            .Where(x => x.CreatedBy.HasValue && userIds.Contains(x.CreatedBy.Value)
                && x.OrderDate >= rangeStart && x.OrderDate < rangeEnd && x.DeletedAt == null)
            .Select(x => new { UserId = x.CreatedBy!.Value, x.OrderDate, Value = x.GrandTotal })
            .ToListAsync(cancellationToken);

        var visitRows = await QueryAsync($@"SELECT CAST(user_id AS bigint) user_id,
YEAR(checkin_date) visit_year, MONTH(checkin_date) visit_month, COUNT_BIG(*) visit_count
FROM check_in WHERE deleted_at IS NULL AND checkin_date >= '{rangeStart:yyyy-MM-dd}' AND checkin_date < '{rangeEnd:yyyy-MM-dd}'
AND user_id IN ({string.Join(',', userIds)}) GROUP BY user_id, YEAR(checkin_date), MONTH(checkin_date)", cancellationToken);
        var visitCounts = visitRows.ToDictionary(
            x => (ULong(x, "user_id"), Convert.ToInt32(Obj(x, "visit_year"), CultureInfo.InvariantCulture), Convert.ToInt32(Obj(x, "visit_month"), CultureInfo.InvariantCulture)),
            x => Convert.ToInt32(Obj(x, "visit_count"), CultureInfo.InvariantCulture));

        var assignmentPeriods = await RetailerAssignmentPeriodsAsync(userIds, rangeEnd, cancellationToken);
        var assignedRetailerIds = assignmentPeriods.Select(x => x.CustomerId).Distinct().ToArray();
        var retailerOrders = assignedRetailerIds.Length == 0 ? [] : await _db.Orders.AsNoTracking()
            .Where(x => x.BuyerId.HasValue && assignedRetailerIds.Contains(x.BuyerId.Value)
                && x.OrderDate >= rangeStart && x.OrderDate < rangeEnd && x.DeletedAt == null)
            .Select(x => new { CustomerId = x.BuyerId!.Value, OrderDate = x.OrderDate!.Value })
            .ToListAsync(cancellationToken);

        var rows = users.Select(user =>
        {
            var ratingStartMonth = RatingAverageStartMonth(user.DateOfJoining, rangeStart);
            var monthlyRatings = new Dictionary<string, decimal>();
            var monthlyDetails = new Dictionary<string, RatingMonthDetail>();
            var cumulativeAssigned = new HashSet<ulong>();
            var cumulativeActive = new HashSet<ulong>();
            var carriedAssigned = 0;
            var carriedActive = 0;

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
                    var assigned = AssignmentsForPeriod(assignmentPeriods, user.Id, monthStart, monthEnd);
                    cumulativeAssigned.UnionWith(assigned);
                    // A retailer counts as active from the first month it was both assigned to
                    // this user and ordered, and stays active afterwards.
                    cumulativeActive.UnionWith(assigned.Where(activeRetailerIds.Contains));
                    registeredRetailers = Math.Max(carriedAssigned, cumulativeAssigned.Count);
                    active = Math.Max(carriedActive, cumulativeActive.Count);
                    carriedAssigned = registeredRetailers;
                    carriedActive = active;
                }

                var userAttendance = attendance.Where(x => x.UserId == user.Id && x.PunchinDate >= monthStart && x.PunchinDate < monthEnd).ToList();
                var marketDays = userAttendance.Where(x => !IsLeaveOrOffice(x.WorkingType)).Select(x => x.PunchinDate.Date).Distinct().Count();
                var promotional = userAttendance.Sum(x => PromotionalActivityCount(x.WorkingType));
                var visits = visitCounts.GetValueOrDefault((user.Id, monthStart.Year, monthStart.Month));
                var target = targets.Where(x => x.UserId == user.Id && x.Year == monthStart.Year
                        && (string.Equals(x.Month, monthStart.ToString("MMM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                            || string.Equals(x.Month, monthStart.ToString("MMMM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)))
                    .Sum(x => x.Target ?? 0m);
                var orderValue = orders.Where(x => x.UserId == user.Id && x.OrderDate >= monthStart && x.OrderDate < monthEnd).Sum(x => x.Value);
                var achievement = orderValue > 1m ? Math.Round((orderValue - orderValue / 100m) / 100000m, 2) : 0m;

                var share = monthStart == currentMonth ? currentMonthShare : 1m;
                var marketTarget = Math.Round(20m * share, 2);
                var visitTarget = Math.Round(200m * share, 2);
                var promotionalTarget = Math.Round(4m * share, 2);
                var salesTarget = Math.Round(target * share, 2);

                var score = CalculateScores(marketDays, visits, achievement, salesTarget, promotional,
                    registeredRetailers, active, marketTarget, visitTarget, promotionalTarget);
                var monthKey = monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                monthlyRatings[monthKey] = score.FinalRating;
                monthlyDetails[monthKey] = new RatingMonthDetail(score.FinalRating,
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

            var eligibleKeys = monthStarts.Where(x => x >= ratingStartMonth)
                .Select(x => x.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var eligible = monthlyRatings.Where(x => eligibleKeys.Contains(x.Key)).Select(x => x.Value).ToArray();

            return new RatingTrendRowDto(user.Id, user.Branch, user.EmployeeCode, user.Name, user.ReportingManager,
                user.Zone, Math.Round(eligible.DefaultIfEmpty(0m).Average(), 2), user.DateOfJoining, ratingStartMonth,
                eligible.Length, monthlyRatings, monthlyDetails, user.ProfileImage);
        }).OrderByDescending(x => x.AverageRating).ThenBy(x => x.EmployeeName).ToList();

        return new RatingTrendResult(rows, monthStarts, currentMonth, indiaToday, rangeStart, rangeEnd);
    }

    private static DateTime RatingAverageStartMonth(DateTime? joiningDate, DateTime defaultStart)
    {
        if (!joiningDate.HasValue) return new DateTime(defaultStart.Year, defaultStart.Month, 1);
        var joiningMonth = new DateTime(joiningDate.Value.Year, joiningDate.Value.Month, 1);
        return joiningDate.Value.Day <= 15 ? joiningMonth : joiningMonth.AddMonths(1);
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

    private static RatingScoreSet CalculateScores(int marketDays, int visits, decimal salesAchievement, decimal salesTarget,
        int promotional, int registeredRetailers, int activeRetailers,
        decimal marketTarget, decimal visitTarget, decimal promotionalTarget)
    {
        var marketRatio = marketTarget <= 0 ? 0m : Math.Min(1m, marketDays / marketTarget);
        var visitRatio = visitTarget <= 0 ? 0m : Math.Min(1m, visits / visitTarget);
        var salesRatio = salesTarget <= 0 ? 0m : Math.Min(1m, salesAchievement / salesTarget);
        var promoRatio = promotionalTarget <= 0 ? 0m : Math.Min(1m, promotional / promotionalTarget);
        var activeRatio = registeredRetailers <= 0 ? 0m : (decimal)activeRetailers / registeredRetailers;
        // Thirty per cent of a user's retailers being active is treated as full marks.
        var activeRatingRatio = Math.Min(1m, activeRatio / 0.3m);

        var final = marketRatio * 5m + visitRatio * 30m + salesRatio * 40m + promoRatio * 10m + activeRatingRatio * 15m;
        return new RatingScoreSet(marketRatio, visitRatio, salesRatio, promoRatio, activeRatio, activeRatingRatio, Math.Round(final, 2));
    }

    private static List<ulong> AssignmentsForPeriod(IReadOnlyCollection<RetailerAssignmentWindow> assignments,
        ulong userId, DateTime periodStart, DateTime periodEnd) => assignments
        .Where(x => x.UserId == userId
            && (!x.AssignedAt.HasValue || x.AssignedAt.Value < periodEnd)
            && (!x.UnassignedAt.HasValue || x.UnassignedAt.Value > periodStart))
        .Select(x => x.CustomerId).Distinct().ToList();

    private async Task<List<RetailerAssignmentWindow>> RetailerAssignmentPeriodsAsync(ulong[] userIds, DateTime rangeEnd, CancellationToken cancellationToken)
    {
        if (userIds.Length == 0) return [];
        var ids = string.Join(',', userIds);
        var rows = await QueryAsync($@"SELECT CAST(user_id AS bigint) user_id, CAST(customer_id AS bigint) customer_id,
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
    WHERE ed.user_id IN ({ids})
) assignments", cancellationToken);

        return rows.Select(row => new RetailerAssignmentWindow(
            ULong(row, "user_id"), ULong(row, "customer_id"),
            NullableDateTime(row, "assigned_at"), NullableDateTime(row, "unassigned_at"))).ToList();
    }

    private async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }
            rows.Add(row);
        }
        return rows;
    }

    private static object? Obj(IReadOnlyDictionary<string, object?> row, string key) => row.GetValueOrDefault(key);

    private static ulong ULong(IReadOnlyDictionary<string, object?> row, string key)
    {
        var value = Obj(row, key);
        return value is null ? 0UL : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
    }

    private static DateTime? NullableDateTime(IReadOnlyDictionary<string, object?> row, string key)
    {
        var value = Obj(row, key);
        return value is null ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
    }
}

public sealed record RatingTrendUser(ulong Id, string Name, string EmployeeCode, string Branch, string Zone,
    string ReportingManager, DateTime? DateOfJoining, ulong? DesignationId = null, string ProfileImage = "");

public sealed record RatingTrendRowDto(ulong UserId, string Branch, string EmployeeCode, string EmployeeName,
    string ReportingManager, string Zone, decimal AverageRating, DateTime? DateOfJoining, DateTime RatingStartMonth,
    int AverageMonthCount, IReadOnlyDictionary<string, decimal> MonthlyRatings,
    IReadOnlyDictionary<string, RatingMonthDetail> MonthlyDetails, string ProfileImage = "");

public sealed record RatingMonthDetail(decimal FinalRating, IReadOnlyList<RatingComponentBreakdown> Components);

/// <summary>One scored component of a month's rating. Deliberately its own type rather
/// than the report controller's internal one, so the service does not depend on a
/// controller to be usable.</summary>
public sealed record RatingComponentBreakdown(string Key, string Label, decimal Percentage, decimal Actual,
    decimal Target, decimal Weight, string Description);

public sealed record RatingScoreSet(decimal MarketRatio, decimal VisitRatio, decimal SalesRatio, decimal PromoRatio,
    decimal ActiveRatio, decimal ActiveRatingRatio, decimal FinalRating);

public sealed record RetailerAssignmentWindow(ulong UserId, ulong CustomerId, DateTime? AssignedAt, DateTime? UnassignedAt);

public sealed record RatingTrendResult(IReadOnlyList<RatingTrendRowDto> Rows, DateTime[] MonthStarts,
    DateTime CurrentMonth, DateTime IndiaToday, DateTime RangeStart, DateTime RangeEnd);
