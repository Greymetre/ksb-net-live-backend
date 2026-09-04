using System.Globalization;
using System.Security.Claims;
using Api.Services;
using Application.Interfaces.Repositories;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Domain.Services;

namespace Api.Controllers;

/// <summary>Ratings for the field app.
///
/// Two different questions, so two endpoints. The listing answers "how are my people
/// doing", and is cut to what the caller may see - a manager's whole downline, a branch
/// manager's branch, an admin everything, and a field user just themselves. The top
/// performers answer "who is leading the country", which is a leaderboard: the same
/// numbers for everybody, or "all India" would mean nothing to the person with nobody
/// under them.
///
/// Gated on authentication alone, like every other field endpoint - CRM module
/// permissions are not how the app decides what a salesperson may open.</summary>
[ApiController]
[Authorize]
[Route("api/ratings")]
public sealed class MobileRatingController : ControllerBase
{
    private const ulong AsrDesignationId = 3;
    private const ulong DsrDesignationId = 6;

    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;
    private readonly RatingTrendService _ratings;

    public MobileRatingController(AppDbContext db, IHrRepository hr, RatingTrendService ratings)
    {
        _db = db;
        _hr = hr;
        _ratings = ratings;
    }

    /// <summary>Six months of ratings for everyone the caller can see.</summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(
        [FromQuery(Name = "designation_id")] ulong? designationId,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var visibleIds = (await _hr.GetVisibleUserIdsAsync(CurrentUserId(), cancellationToken)).Distinct().ToHashSet();
        var users = await LoadUsersAsync(
            x => visibleIds.Contains(x.Id) && (designationId == null || x.DesignationId == designationId),
            cancellationToken);

        var result = await _ratings.CalculateAsync(users, cancellationToken);
        var rows = result.Rows;
        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            rows = rows.Where(x => x.EmployeeName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.EmployeeCode.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Branch.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.Zone.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return Ok(new
        {
            status = true,
            period = Period(result),
            summary = new
            {
                total_employees = rows.Count,
                average_rating = rows.Count == 0 ? 0m : Math.Round(rows.Average(x => x.AverageRating), 2)
            },
            rows = rows.Select(ToListItem)
        });
    }

    /// <summary>Last month's leaders, country-wide and by zone. Not cut to the caller.</summary>
    [HttpGet("top-performers")]
    public async Task<IActionResult> TopPerformers(CancellationToken cancellationToken)
    {
        var users = await LoadUsersAsync(
            x => x.DesignationId == AsrDesignationId || x.DesignationId == DsrDesignationId,
            cancellationToken);
        var result = await _ratings.CalculateAsync(users, cancellationToken);

        // The month just gone, because the one running is only part scored and would rank
        // people on however many days each has had so far.
        var lastMonth = result.CurrentMonth.AddMonths(-1);
        var monthKey = lastMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var designations = users.ToDictionary(x => x.Id, x => x.DesignationId);

        List<RatingTrendRowDto> ForDesignation(ulong designation) => result.Rows
            .Where(row => designations.GetValueOrDefault(row.UserId) == designation
                && row.RatingStartMonth <= lastMonth)
            .OrderByDescending(row => row.MonthlyRatings.GetValueOrDefault(monthKey))
            .ThenBy(row => row.EmployeeName)
            .ToList();

        var asr = ForDesignation(AsrDesignationId);
        var dsr = ForDesignation(DsrDesignationId);

        return Ok(new
        {
            status = true,
            month = new
            {
                key = monthKey,
                label = lastMonth.ToString("MMM", CultureInfo.InvariantCulture),
                full_label = lastMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
            },
            all_india = new
            {
                asr = asr.Take(1).Select(row => ToPerformer(row, monthKey)).FirstOrDefault(),
                dsr = dsr.Take(1).Select(row => ToPerformer(row, monthKey)).FirstOrDefault()
            },
            zones = ZoneOrderedTop(asr, dsr, monthKey)
        });
    }

    /// <summary>One entry per zone, in the order the business reads them, and only zones
    /// that actually have somebody rated.</summary>
    private static IEnumerable<object> ZoneOrderedTop(
        IReadOnlyCollection<RatingTrendRowDto> asr,
        IReadOnlyCollection<RatingTrendRowDto> dsr,
        string monthKey)
    {
        var zones = asr.Concat(dsr)
            .Select(x => x.Zone)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ZoneOrder.Rank)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase);

        return zones.Select(zone => new
        {
            zone,
            asr = asr.Where(x => string.Equals(x.Zone, zone, StringComparison.OrdinalIgnoreCase))
                .Take(1).Select(row => ToPerformer(row, monthKey)).FirstOrDefault(),
            dsr = dsr.Where(x => string.Equals(x.Zone, zone, StringComparison.OrdinalIgnoreCase))
                .Take(1).Select(row => ToPerformer(row, monthKey)).FirstOrDefault()
        }).Where(x => x.asr is not null || x.dsr is not null).ToList();
    }

    private static object ToPerformer(RatingTrendRowDto row, string monthKey) => new
    {
        user_id = row.UserId,
        name = row.EmployeeName,
        employee_code = row.EmployeeCode,
        branch = row.Branch,
        zone = row.Zone,
        // Raw stored path. The app resolves it against its own configured origin, the
        // same way it resolves every other image.
        profile_image = row.ProfileImage,
        rating = row.MonthlyRatings.GetValueOrDefault(monthKey)
    };

    private static object ToListItem(RatingTrendRowDto row) => new
    {
        user_id = row.UserId,
        employee_name = row.EmployeeName,
        employee_code = row.EmployeeCode,
        branch = row.Branch,
        zone = row.Zone,
        reporting_manager = row.ReportingManager,
        average_rating = row.AverageRating,
        average_month_count = row.AverageMonthCount,
        rating_start_month = row.RatingStartMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        monthly_ratings = row.MonthlyRatings,
        // The per-month breakdown the detail popup shows. Sent with the listing rather
        // than fetched per tap: it is already computed, and a second round trip on a
        // phone is a second chance to be offline.
        monthly_details = row.MonthlyDetails.ToDictionary(item => item.Key, item => new
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
    };

    private static object Period(RatingTrendResult result) => new
    {
        label = $"{result.MonthStarts[0]:MMM yyyy} to {result.MonthStarts[^1]:MMM yyyy}",
        months = result.MonthStarts.Select(x => new
        {
            key = x.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            label = x.ToString("MMM", CultureInfo.InvariantCulture),
            full_label = x.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            in_progress = x == result.CurrentMonth
        })
    };

    private async Task<List<RatingTrendUser>> LoadUsersAsync(
        System.Linq.Expressions.Expression<Func<Domain.Entities.User, bool>> predicate,
        CancellationToken cancellationToken)
    {
        var users = await _db.Users.AsNoTracking()
            .Where(x => x.Active == "Y" && !x.IsDeleted && x.DeletedAt == null)
            .Where(predicate)
            .ToListAsync(cancellationToken);
        if (users.Count == 0) return [];

        var userIds = users.Select(x => x.Id).ToArray();
        var divisions = await _db.Divisions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DivisionName, cancellationToken);
        var branches = await _db.Branches.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.BranchName, cancellationToken);
        var names = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var joiningDates = (await _db.UserDetails.AsNoTracking()
            .Where(x => x.UserId.HasValue && userIds.Contains(x.UserId.Value) && x.DeletedAt == null && x.DateOfJoining.HasValue)
            .Select(x => new { UserId = x.UserId!.Value, x.DateOfJoining, x.Id })
            .ToListAsync(cancellationToken))
            .GroupBy(x => x.UserId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(item => item.Id).Select(item => item.DateOfJoining).FirstOrDefault());

        return users.Select(user => new RatingTrendUser(
            user.Id,
            user.Name,
            user.EmployeeCodes ?? string.Empty,
            BranchName(user, branches),
            user.DivisionId.HasValue ? divisions.GetValueOrDefault(user.DivisionId.Value) ?? string.Empty : string.Empty,
            user.ReportingId.HasValue ? names.GetValueOrDefault(user.ReportingId.Value) ?? string.Empty : string.Empty,
            user.DateOfJoining ?? joiningDates.GetValueOrDefault(user.Id),
            user.DesignationId,
            user.ProfileImage ?? string.Empty))
            .ToList();
    }

    private static string BranchName(Domain.Entities.User user, IReadOnlyDictionary<ulong, string> branches)
    {
        var first = (user.BranchId ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => ulong.TryParse(x, out var id) ? id : 0UL)
            .FirstOrDefault(x => x > 0);
        return first == 0 ? string.Empty : branches.GetValueOrDefault(first) ?? string.Empty;
    }

    private ulong? CurrentUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return ulong.TryParse(value, out var id) ? id : null;
    }
}
