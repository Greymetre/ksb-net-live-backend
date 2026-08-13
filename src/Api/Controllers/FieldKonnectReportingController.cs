using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Api.Filters;
using Infrastructure.Data;
using Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class FieldKonnectReportingController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public FieldKonnectReportingController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("user-monitoring/options")]
    public async Task<IActionResult> UserMonitoringOptions(CancellationToken cancellationToken)
    {
        var ids = await VisibleReportingUserIds(CurrentUserId(), cancellationToken);
        if (ids.Count == 0) return Ok(new { users = Array.Empty<object>() });
        var rows = await QueryRows($@"SELECT u.id, u.name, u.branch_id, u.division_id, u.department_id,
b.id AS branch_id_value, b.branch_name, d.id AS division_id_value, d.division_name,
dp.id AS department_id_value, dp.name AS department_name
FROM users u WHERE u.deleted_at IS NULL AND u.active = 'Y' AND u.id IN ({string.Join(',', ids)})
ORDER BY u.name".Replace("FROM users u WHERE", @"FROM users u
LEFT JOIN branches b ON FIND_IN_SET(b.id, u.branch_id) AND b.deleted_at IS NULL
LEFT JOIN divisions d ON d.id=TRY_CONVERT(decimal(20,0), NULLIF(LTRIM(RTRIM(u.division_id)), '')) AND d.deleted_at IS NULL
LEFT JOIN departments dp ON dp.id=TRY_CONVERT(decimal(20,0), NULLIF(LTRIM(RTRIM(u.department_id)), '')) AND dp.deleted_at IS NULL
WHERE"), cancellationToken);
        var users = rows.GroupBy(x => ULong(x, "id")).Select(group =>
        {
            var row = group.First();
            return new { id = group.Key, name = Str(row, "name"), branch_id=Obj(row,"branch_id"), division_id=Obj(row,"division_id"), department_id=Obj(row,"department_id") };
        }).ToList();
        var branches = rows.Where(x=>Obj(x,"branch_id_value") is not null).GroupBy(x=>ULong(x,"branch_id_value")).Select(g=>new { id=g.Key, name=Str(g.First(),"branch_name") }).OrderBy(x=>x.name).ToList();
        var divisions = rows.Where(x=>Obj(x,"division_id_value") is not null).GroupBy(x=>ULong(x,"division_id_value")).Select(g=>new { id=g.Key, name=Str(g.First(),"division_name") }).OrderBy(x=>x.name).ToList();
        var departments = rows.Where(x=>Obj(x,"department_id_value") is not null).GroupBy(x=>ULong(x,"department_id_value")).Select(g=>new { id=g.Key, name=Str(g.First(),"department_name") }).OrderBy(x=>x.name).ToList();
        return Ok(new { users, branches, divisions, departments });
    }

    [HttpGet("user-app-details")]
    [RequirePermission("user_app_details_access")]
    public async Task<IActionResult> UserAppDetails([FromQuery(Name = "user_id")] ulong? userId, [FromQuery] int page=1, [FromQuery(Name="page_size")] int pageSize=10, CancellationToken cancellationToken=default)
    {
        page=Math.Max(1,page);pageSize=Math.Clamp(pageSize,1,100);
        var ids = await VisibleReportingUserIds(CurrentUserId(), cancellationToken);
        if (userId.HasValue) ids = ids.Where(x => x == userId.Value).ToList();
        if (ids.Count == 0) return Ok(new { data = Array.Empty<object>(), pagination=new { current_page=page, last_page=1, page_size=pageSize, total=0 } });
        var idCsv=string.Join(',',ids);
        var total=await QueryScalarLong($@"SELECT COUNT(*) FROM (SELECT m.user_id
FROM mobile_user_login_details m INNER JOIN users u ON u.id=m.user_id AND u.deleted_at IS NULL
WHERE m.app='2' AND m.user_id IN ({idCsv}) GROUP BY m.user_id) q",cancellationToken);
        var offset=(page-1)*pageSize;
        var rows = await QueryRows($@"WITH latest AS (SELECT m.id, m.user_id, u.name AS user_name, u.mobile,
m.app_version, m.device_name, m.device_type, m.unique_id, m.first_login_date,
m.last_login_date, m.login_status, m.login_at,
ROW_NUMBER() OVER (PARTITION BY m.user_id ORDER BY COALESCE(m.updated_at,m.last_login_date,m.created_at) DESC,m.id DESC) AS rn
FROM mobile_user_login_details m
INNER JOIN users u ON u.id=m.user_id AND u.deleted_at IS NULL
WHERE m.app='2' AND m.user_id IN ({idCsv}))
SELECT * FROM latest WHERE rn=1 ORDER BY COALESCE(login_at,last_login_date,first_login_date) DESC,id DESC
OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY", cancellationToken);
        var data=rows.Select(x => new {
            id=ULong(x,"id"), user_id=ULong(x,"user_id"), user_name=Str(x,"user_name"), mobile=Str(x,"mobile"),
            app_version=Str(x,"app_version"), device_name=Str(x,"device_name"), device_type=Str(x,"device_type"),
            unique_id=Str(x,"unique_id"), first_login_date=Obj(x,"first_login_date"), last_login_date=Obj(x,"last_login_date"),
            login_status=Str(x,"login_status"), login_at=Obj(x,"login_at")
        }).ToList();
        var lastPage=Math.Max(1,(long)Math.Ceiling(total/(double)pageSize));
        return Ok(new { data, pagination=new { current_page=page, last_page=lastPage, page_size=pageSize, total } });
    }

    [HttpPost("user-app-details/{userId:long}/force-logout")]
    [RequirePermission("user_app_force_logout")]
    public async Task<IActionResult> ForceLogoutUser(ulong userId, CancellationToken cancellationToken)
    {
        if (!await CanManageAppUser(userId, cancellationToken))
            return StatusCode(403, new { status=false, message="User is outside your reporting hierarchy" });

        await EndMobileSessions(userId, clearUniqueId: false, cancellationToken);
        return Ok(new { status=true, message="User has been logged out from the mobile app." });
    }

    [HttpDelete("user-app-details/{userId:long}/unique-id")]
    [RequirePermission("user_app_uuid_reset")]
    public async Task<IActionResult> ClearUserDeviceUuid(ulong userId, CancellationToken cancellationToken)
    {
        if (!await CanManageAppUser(userId, cancellationToken))
            return StatusCode(403, new { status=false, message="User is outside your reporting hierarchy" });

        await EndMobileSessions(userId, clearUniqueId: true, cancellationToken);
        return Ok(new { status=true, message="Device UUID removed. The user can now sign in on a new device." });
    }

    private async Task<bool> CanManageAppUser(ulong userId, CancellationToken cancellationToken) =>
        (await VisibleReportingUserIds(CurrentUserId(), cancellationToken)).Contains(userId);

    private async Task EndMobileSessions(ulong userId, bool clearUniqueId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var details = _dbContext.MobileUserLoginDetails.Where(x => x.UserId == userId && x.App == "2");
        if (clearUniqueId)
        {
            await details.ExecuteUpdateAsync(updates => updates
                .SetProperty(x => x.LoginStatus, "0")
                .SetProperty(x => x.UniqueId, (string?)null)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        }
        else
        {
            await details.ExecuteUpdateAsync(updates => updates
                .SetProperty(x => x.LoginStatus, "0")
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        }

        // New tokens are provider-qualified. Legacy mobile tokens did not store the
        // provider in this table, so they must also be revoked to guarantee logout.
        await _dbContext.OAuthAccessTokens
            .Where(x => x.UserId == userId && !x.Revoked && x.Name != null && x.Name.Contains("mobile"))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(x => x.Revoked, true)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }

    [HttpGet("user-live-activity")]
    [RequirePermission("user_location")]
    public async Task<IActionResult> UserLiveActivity([FromQuery(Name = "user_id")] ulong userId, [FromQuery] DateTime? date, CancellationToken cancellationToken)
    {
        var ids = await VisibleReportingUserIds(CurrentUserId(), cancellationToken);
        if (!ids.Contains(userId)) return StatusCode(403, new { status=false, message="User is outside your reporting hierarchy" });
        var day = (date ?? IndiaNow().Date).Date;
        var rows = await QueryRows(@"SELECT id, latitude, longitude, time
FROM user_live_locations
WHERE deleted_at IS NULL AND userid=@user_id AND CAST(time AS date)=@date
ORDER BY time,id", cancellationToken, ("@user_id",userId), ("@date",day));
        return Ok(new { status=true, user_id=userId, date=day.ToString("yyyy-MM-dd"), locations=rows.Select(x=>new {
            id=ULong(x,"id"), latitude=Str(x,"latitude"), longitude=Str(x,"longitude"), time=Obj(x,"time")
        })});
    }

    [HttpGet("user-live-activity/map")]
    [RequirePermission("user_location")]
    public async Task<IActionResult> UserLiveActivityMap([FromQuery(Name="user_id")] ulong userId, [FromQuery] string? mode, [FromQuery] DateTime? date, [FromQuery(Name="to_date")] DateTime? toDate, CancellationToken cancellationToken)
    {
        var ids = await VisibleReportingUserIds(CurrentUserId(), cancellationToken);
        if (!ids.Contains(userId)) return StatusCode(403, new { status=false, message="User is outside your reporting hierarchy" });
        var from = (date ?? IndiaNow().Date).Date;
        var until = (toDate ?? from).Date;
        if (until < from) (from, until) = (until, from);
        if ((until-from).TotalDays > 30) return BadRequest(new { status=false, message="The date range must not exceed 30 days." });

        IReadOnlyList<Dictionary<string, object?>> rows;
        if (string.Equals(mode,"track",StringComparison.OrdinalIgnoreCase))
        {
            var today=IndiaNow().Date;
            rows=await QueryRows(@"SELECT id, latitude, longitude, time AS event_time, 'Live Location' AS name, address
FROM user_live_locations WHERE deleted_at IS NULL AND userid=@user_id AND CAST(time AS date)=@date
ORDER BY event_time,id",cancellationToken,("@user_id",userId),("@date",today));
        }
        else
        {
            rows=await QueryRows(@"SELECT * FROM (
SELECT a.id*10+1 AS id,a.punchin_latitude AS latitude,a.punchin_longitude AS longitude,
DATEADD(SECOND,DATEDIFF(SECOND,CAST('00:00:00' AS time),a.punchin_time),CAST(a.punchin_date AS datetime2)) AS event_time,
'Punch In' AS name,COALESCE(a.punchin_address,'') AS address
FROM attendances a WHERE a.deleted_at IS NULL AND a.user_id=@user_id AND a.punchin_date BETWEEN @from AND @until AND a.punchin_latitude IS NOT NULL AND a.punchin_longitude IS NOT NULL
UNION ALL
SELECT a.id*10+2,a.punchout_latitude,a.punchout_longitude,
DATEADD(SECOND,DATEDIFF(SECOND,CAST('00:00:00' AS time),a.punchout_time),CAST(a.punchin_date AS datetime2)),
'Punch Out',COALESCE(a.punchout_address,'')
FROM attendances a WHERE a.deleted_at IS NULL AND a.user_id=@user_id AND a.punchin_date BETWEEN @from AND @until AND a.punchout_time IS NOT NULL AND a.punchout_latitude IS NOT NULL AND a.punchout_longitude IS NOT NULL
UNION ALL
SELECT ci.id*10+3,ci.checkin_latitude,ci.checkin_longitude,
DATEADD(SECOND,DATEDIFF(SECOND,CAST('00:00:00' AS time),ci.checkin_time),CAST(ci.checkin_date AS datetime2)),
COALESCE(c.name,'Customer')+' : Check In',COALESCE(ci.checkin_address,'')
FROM check_in ci LEFT JOIN customers c ON c.id=ci.customer_id AND c.deleted_at IS NULL
WHERE ci.deleted_at IS NULL AND ci.user_id=@user_id AND ci.checkin_date BETWEEN @from AND @until AND ci.checkin_latitude IS NOT NULL AND ci.checkin_longitude IS NOT NULL
UNION ALL
SELECT ci.id*10+4,ci.checkout_latitude,ci.checkout_longitude,
DATEADD(SECOND,DATEDIFF(SECOND,CAST('00:00:00' AS time),ci.checkout_time),CAST(COALESCE(ci.checkout_date,ci.checkin_date) AS datetime2)),
COALESCE(c.name,'Customer')+' : Check Out',COALESCE(ci.checkout_address,'')
FROM check_in ci LEFT JOIN customers c ON c.id=ci.customer_id AND c.deleted_at IS NULL
WHERE ci.deleted_at IS NULL AND ci.user_id=@user_id AND ci.checkin_date BETWEEN @from AND @until AND ci.checkout_time IS NOT NULL AND ci.checkout_latitude IS NOT NULL AND ci.checkout_longitude IS NOT NULL
) q ORDER BY event_time,id",cancellationToken,("@user_id",userId),("@from",from),("@until",until));
        }
        var locations=rows.Select(x=>{
            var latitude=Str(x,"latitude");var longitude=Str(x,"longitude");
            if(double.TryParse(latitude,NumberStyles.Any,CultureInfo.InvariantCulture,out var lat)&&double.TryParse(longitude,NumberStyles.Any,CultureInfo.InvariantCulture,out var lng)&&Math.Abs(lat)>90&&Math.Abs(lng)<=90)
                (latitude,longitude)=(longitude,latitude);
            return new { id=ULong(x,"id"), latitude, longitude, time=Obj(x,"event_time"), name=Str(x,"name"), address=Str(x,"address") };
        }).ToList();
        return Ok(new { status=true, user_id=userId, mode=mode??"complete", from_date=from.ToString("yyyy-MM-dd"), to_date=until.ToString("yyyy-MM-dd"), locations });
    }

    [AcceptVerbs("GET", "POST")]
    [Route("reporting/users")]
    public async Task<IActionResult> ReportingUsers(CancellationToken cancellationToken)
    {
        try
        {
            var request = await RequestValues(cancellationToken);
            var page = Math.Max(1, IntValue(request, "page", 1));
            var pageSize = Math.Clamp(IntValue(request, "pageSize", 20), 1, 100);
            var startDate = DateValue(request, "start_date") ?? DateValue(request, "startdate") ?? IndiaNow().Date;
            var endDate = DateValue(request, "end_date") ?? DateValue(request, "enddate") ?? startDate;
            if (endDate < startDate) (startDate, endDate) = (endDate, startDate);

            var authUserId = CurrentUserId();
            var visibleIds = await VisibleReportingUserIds(authUserId, cancellationToken);
            var dropdownUserIds = await FilterUserIds(visibleIds, request, includeSearchName: false, cancellationToken);
            var filteredUserIds = await FilterUserIds(visibleIds, request, includeSearchName: true, cancellationToken);
            var lookups = await ReportingLookups(dropdownUserIds, cancellationToken);

            if (filteredUserIds.Count == 0)
            {
                var emptyStats = await ActivityStats(new List<ulong>(), startDate, endDate, cancellationToken);
                return Ok(Response(new List<object>(), lookups.Users, lookups.Branches, lookups.Zones, lookups.Designations, page, pageSize, 0, emptyStats));
            }

            var idCsv = string.Join(',', filteredUserIds);
            var total = await QueryScalarLong($@"SELECT COUNT(*)
FROM attendances a
WHERE a.deleted_at IS NULL
AND a.user_id IN ({idCsv})
AND a.punchin_date BETWEEN @start_date AND @end_date", cancellationToken,
                ("@start_date", startDate), ("@end_date", endDate));

            var offset = (page - 1) * pageSize;
            IReadOnlyList<Dictionary<string, object?>> rows = total == 0 ? new List<Dictionary<string, object?>>() : await QueryRows($@"SELECT a.id AS attendance_id,
a.user_id,
u.name,
a.punchin_date AS activity_date,
a.punchin_time,
a.punchout_time,
a.attendance_status,
a.working_type,
	ru.name AS reporting_manager_name,
	ru.mobile AS reporting_manager_mobile,
	(SELECT COUNT(*) FROM customers c WHERE c.deleted_at IS NULL AND c.created_by = a.user_id AND CAST(c.created_at AS date) = a.punchin_date) AS total_customers,
	(SELECT COUNT(*) FROM customers c LEFT JOIN customer_types ct ON ct.id = c.customertype AND ct.deleted_at IS NULL WHERE c.deleted_at IS NULL AND c.created_by = a.user_id AND CAST(c.created_at AS date) = a.punchin_date AND NOT (c.customertype IN (1,3) OR COALESCE(ct.customertype_name, '') LIKE '%Distributor%' OR COALESCE(ct.type_name, '') LIKE '%Distributor%')) AS total_secondary_customers,
	(SELECT COUNT(*) FROM customers c LEFT JOIN customer_types ct ON ct.id = c.customertype AND ct.deleted_at IS NULL WHERE c.deleted_at IS NULL AND c.created_by = a.user_id AND CAST(c.created_at AS date) = a.punchin_date AND (c.customertype IN (1,3) OR COALESCE(ct.customertype_name, '') LIKE '%Distributor%' OR COALESCE(ct.type_name, '') LIKE '%Distributor%')) AS total_master_distributors,
	(SELECT COUNT(*) FROM orders o WHERE o.deleted_at IS NULL AND COALESCE(o.executive_id, o.created_by) = a.user_id AND COALESCE(o.order_date, CAST(o.created_at AS date)) = a.punchin_date) AS total_orders,
	(SELECT COALESCE(SUM(COALESCE(NULLIF(o.grand_total, 0), NULLIF(o.total_amount, 0), NULLIF(o.sub_total, 0), 0)), 0) FROM orders o WHERE o.deleted_at IS NULL AND COALESCE(o.executive_id, o.created_by) = a.user_id AND COALESCE(o.order_date, CAST(o.created_at AS date)) = a.punchin_date) AS total_order_value,
	(SELECT COALESCE(SUM(od.quantity), 0) FROM orders o INNER JOIN order_details od ON od.order_id = o.id WHERE o.deleted_at IS NULL AND COALESCE(o.executive_id, o.created_by) = a.user_id AND COALESCE(o.order_date, CAST(o.created_at AS date)) = a.punchin_date) AS total_quantity,
	(SELECT COUNT(*) FROM check_in ci WHERE ci.deleted_at IS NULL AND ci.user_id = a.user_id AND ci.checkin_date = a.punchin_date) AS total_checkins
FROM attendances a
INNER JOIN users u ON u.id = a.user_id AND u.deleted_at IS NULL
LEFT JOIN users ru ON ru.id = u.reportingid AND ru.deleted_at IS NULL
WHERE a.deleted_at IS NULL
AND a.user_id IN ({idCsv})
AND a.punchin_date BETWEEN @start_date AND @end_date
ORDER BY a.punchin_date DESC, a.id DESC
LIMIT {pageSize} OFFSET {offset}", cancellationToken,
                ("@start_date", startDate), ("@end_date", endDate));

            var data = rows.Select(ActivityRow).ToList();
            var stats = await ActivityStats(filteredUserIds, startDate, endDate, cancellationToken);
            return Ok(Response(data, lookups.Users, lookups.Branches, lookups.Zones, lookups.Designations, page, pageSize, total, stats));
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = ExceptionMessage(exception) });
        }
    }

    [HttpGet("reporting/users/stats")]
    public async Task<IActionResult> ReportingUsersStats(CancellationToken cancellationToken)
    {
        try
        {
            var request = await RequestValues(cancellationToken);
            var startDate = DateValue(request, "start_date") ?? DateValue(request, "startdate") ?? IndiaNow().Date;
            var endDate = DateValue(request, "end_date") ?? DateValue(request, "enddate") ?? startDate;
            if (endDate < startDate) (startDate, endDate) = (endDate, startDate);
            if (!request.ContainsKey("search_name") && request.TryGetValue("user_id", out var userId)) request["search_name"] = userId;

            var visibleIds = await VisibleReportingUserIds(CurrentUserId(), cancellationToken);
            var filteredUserIds = await FilterUserIds(visibleIds, request, includeSearchName: true, cancellationToken);
            var stats = await ActivityStats(filteredUserIds, startDate, endDate, cancellationToken);
            return Ok(new { status = "success", data = stats });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = ExceptionMessage(exception) });
        }
    }

    [HttpGet("getHierarchyOrderStats")]
    public async Task<IActionResult> GetHierarchyOrderStats(CancellationToken cancellationToken)
    {
        try
        {
            var request = await RequestValues(cancellationToken);
            var startDate = DateValue(request, "startdate") ?? DateValue(request, "start_date") ?? IndiaNow().Date;
            var endDate = DateValue(request, "enddate") ?? DateValue(request, "end_date") ?? startDate;
            if (endDate < startDate) (startDate, endDate) = (endDate, startDate);
            if (!request.ContainsKey("search_name") && request.TryGetValue("user_id", out var userId)) request["search_name"] = userId;

            var visibleIds = await VisibleReportingUserIds(CurrentUserId(), cancellationToken);
            var filteredUserIds = await FilterUserIds(visibleIds, request, includeSearchName: true, cancellationToken);
            var stats = await ActivityStats(filteredUserIds, startDate, endDate, cancellationToken);
            return Ok(new { status = "success", data = stats });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = ExceptionMessage(exception) });
        }
    }

    [AcceptVerbs("GET", "POST")]
    [Route("user/activity")]
    public async Task<IActionResult> UserActivity(CancellationToken cancellationToken)
    {
        try
        {
            var request = await RequestValues(cancellationToken);
            var userId = ULongValue(request, "user_id");
            var date = ActivityDate(Value(request, "date"));
            if (!userId.HasValue) return BadRequest(new { status = "error", message = "user_id is required", data = Array.Empty<object>() });
            if (!date.HasValue) return BadRequest(new { status = "error", message = "Invalid date. Use YYYY-MM-DD.", data = Array.Empty<object>() });

            var visibleIds = await VisibleReportingUserIds(CurrentUserId(), cancellationToken);
            if (!visibleIds.Contains(userId.Value)) return StatusCode(403, new { status = "error", message = "You do not have permission to view this user's activity", data = Array.Empty<object>() });

            var activities = new List<(DateTime Sort, Dictionary<string, object?> Item)>();
            await AddPunchActivities(activities, userId.Value, date.Value, cancellationToken);
            await AddCheckInActivities(activities, userId.Value, date.Value, cancellationToken);
            await AddOrderActivities(activities, userId.Value, date.Value, cancellationToken);
            await AddCustomerActivities(activities, userId.Value, date.Value, cancellationToken);
            await AddLoggedActivities(activities, userId.Value, date.Value, cancellationToken);

            var data = activities
                .OrderBy(x => x.Sort)
                .ThenBy(x => Convert.ToString(x.Item["id"], CultureInfo.InvariantCulture))
                .Select(x => x.Item)
                .ToList();

            return Ok(new { status = true, message = data.Count == 0 ? "No activity found" : "User activity fetched successfully", data });
        }
        catch (Exception exception)
        {
            return StatusCode(500, new { status = "error", message = ExceptionMessage(exception), data = Array.Empty<object>() });
        }
    }

    private object Response(List<object> data, List<object> users, List<object> branches, List<object> zones, List<object> designations, int page, int pageSize, long total, object stats)
    {
        var lastPage = total == 0 ? 1 : (long)Math.Ceiling(total / (double)pageSize);
        return new
        {
            status = "success",
            message = "User activity fetched successfully",
            users,
            branches,
            zones,
            designations,
            data,
            pagination = new
            {
                current_page = page,
                last_page = lastPage,
                per_page = pageSize,
                total,
                has_more = page < lastPage
            },
            stats
        };
    }

    private object ActivityRow(Dictionary<string, object?> row)
    {
        var orders = Long(row, "total_orders");
        var checkins = Long(row, "total_checkins");
        var manager = new { name = Str(row, "reporting_manager_name"), mobile = Str(row, "reporting_manager_mobile") };
        return new
        {
            user_id = ULong(row, "user_id"),
            name = Str(row, "name"),
            date = DateOnlyString(row, "activity_date"),
            reporting = manager,
            reporting_manager = manager,
            reporting_manager_name = manager.name,
            reporting_manager_mobile = manager.mobile,
            punch_in_time = TimeString(row, "punchin_time"),
            punch_out_time = TimeString(row, "punchout_time"),
            attendance_status = AttendanceStatus(row),
            working_type = FirstNonEmpty(Str(row, "working_type"), "Market Visit"),
            total_customers = Long(row, "total_customers"),
            total_orders = orders,
            total_order_value = Dec(row, "total_order_value"),
            total_quantity = Long(row, "total_quantity"),
            total_secondary_customers = Long(row, "total_secondary_customers"),
            total_master_distributors = Long(row, "total_master_distributors"),
            total_checkins = checkins,
            productive_calls = orders,
            non_productive_calls = Math.Max(0, checkins - orders)
        };
    }

    private async Task AddPunchActivities(List<(DateTime Sort, Dictionary<string, object?> Item)> activities, ulong userId, DateTime date, CancellationToken cancellationToken)
    {
        var rows = await QueryRows(@"SELECT id, punchin_date, punchin_time, punchin_latitude, punchin_longitude, punchin_address, punchin_summary, working_type,
punchout_date, punchout_time, punchout_latitude, punchout_longitude, punchout_address, punchout_summary
FROM attendances
WHERE deleted_at IS NULL AND user_id = @user_id AND punchin_date = @date", cancellationToken, ("@user_id", userId), ("@date", date));

        foreach (var row in rows)
        {
            var attendanceId = ULong(row, "id");
            if (Obj(row, "punchin_time") is not null)
            {
                var sort = CombineDateTime(date, Obj(row, "punchin_time"));
                var item = Activity("punchin-" + attendanceId, "Punchin", sort, FirstNonEmpty(Str(row, "punchin_summary"), "User punched in"),
                    FirstNonEmpty(Str(row, "punchin_address"), "No Location"), Str(row, "punchin_latitude"), Str(row, "punchin_longitude"), "punch_in");
                item["working_type"] = FirstNonEmpty(Str(row, "working_type"), "Regular");
                activities.Add((sort, item));
            }

            if (Obj(row, "punchout_time") is not null)
            {
                var punchoutDate = Obj(row, "punchout_date") is null ? date : Convert.ToDateTime(Obj(row, "punchout_date"), CultureInfo.InvariantCulture).Date;
                var sort = CombineDateTime(punchoutDate, Obj(row, "punchout_time"));
                activities.Add((sort, Activity("punchout-" + attendanceId, "Punchout", sort, FirstNonEmpty(Str(row, "punchout_summary"), "User punched out"),
                    FirstNonEmpty(Str(row, "punchout_address"), "No Location"), Str(row, "punchout_latitude"), Str(row, "punchout_longitude"), "punch_out")));
            }
        }
    }

    private async Task AddCheckInActivities(List<(DateTime Sort, Dictionary<string, object?> Item)> activities, ulong userId, DateTime date, CancellationToken cancellationToken)
    {
        var rows = await QueryRows(@"SELECT ci.id, ci.entity_type, ci.entity_id, ci.customer_id, ci.checkin_date, ci.checkin_time, ci.checkin_latitude, ci.checkin_longitude,
ci.checkin_address, ci.checkout_date, ci.checkout_time, ci.checkout_latitude, ci.checkout_longitude, ci.checkout_address,
COALESCE(NULLIF(JSON_VALUE(c.custom_fields, '$.shop_name'), ''), NULLIF(JSON_VALUE(c.custom_fields, '$.owner_name'), ''), NULLIF(c.name, '')) AS customer_name,
COALESCE(NULLIF(ci.checkin_address, ''), NULLIF(a.address1, '')) AS customer_location,
COALESCE(NULLIF(ctype.customertype_name, ''), NULLIF(ctype.type_name, ''), ci.entity_type, 'Customer') AS customer_type
FROM check_in ci
LEFT JOIN customers c ON c.id = COALESCE(ci.entity_id, ci.customer_id) AND c.deleted_at IS NULL
LEFT JOIN addresses a ON a.customer_id = c.id AND a.deleted_at IS NULL
LEFT JOIN customer_types ctype ON ctype.id = c.customertype AND ctype.deleted_at IS NULL
WHERE ci.deleted_at IS NULL AND ci.user_id = @user_id AND ci.checkin_date = @date", cancellationToken, ("@user_id", userId), ("@date", date));

        foreach (var row in rows)
        {
            var checkinId = ULong(row, "id");
            var entityId = ULong(row, "entity_id");
            if (entityId == 0) entityId = ULong(row, "customer_id");
            var customerName = FirstNonEmpty(Str(row, "customer_name"), "Unknown Customer");
            var entityType = FirstNonEmpty(Str(row, "entity_type"), Str(row, "customer_type"), "customer");
            if (Obj(row, "checkin_time") is not null)
            {
                var sort = CombineDateTime(date, Obj(row, "checkin_time"));
                var item = Activity("checkin-" + checkinId, "Checkin", sort, "Checked in at customer", FirstNonEmpty(Str(row, "customer_location"), Str(row, "checkin_address"), "No Location"),
                    Str(row, "checkin_latitude"), Str(row, "checkin_longitude"), "check_in");
                item["customer_name"] = customerName;
                item["customer"] = customerName;
                item["customer_type"] = entityType;
                item["entity_id"] = entityId;
                item["checkin_id"] = checkinId;
                activities.Add((sort, item));
            }

            if (Obj(row, "checkout_time") is not null)
            {
                var checkoutDate = Obj(row, "checkout_date") is null ? date : Convert.ToDateTime(Obj(row, "checkout_date"), CultureInfo.InvariantCulture).Date;
                var sort = CombineDateTime(checkoutDate, Obj(row, "checkout_time"));
                var item = Activity("checkout-" + checkinId, "Checkout", sort, "Checked out from customer", FirstNonEmpty(Str(row, "checkout_address"), Str(row, "customer_location"), "No Location"),
                    Str(row, "checkout_latitude"), Str(row, "checkout_longitude"), "check_out");
                item["customer_name"] = customerName;
                item["customer"] = customerName;
                item["remark"] = "";
                item["customer_type"] = entityType;
                item["entity_id"] = entityId;
                item["checkin_id"] = checkinId;
                activities.Add((sort, item));
            }
        }
    }

    private async Task AddOrderActivities(List<(DateTime Sort, Dictionary<string, object?> Item)> activities, ulong userId, DateTime date, CancellationToken cancellationToken)
    {
        var rows = await QueryRows(@"SELECT o.id, o.orderno, o.created_at, o.order_date,
COALESCE(NULLIF(JSON_VALUE(buyer.custom_fields, '$.shop_name'), ''), NULLIF(JSON_VALUE(buyer.custom_fields, '$.owner_name'), ''), NULLIF(buyer.name, '')) AS buyer_name,
COALESCE(NULLIF(o.grand_total, 0), NULLIF(o.total_amount, 0), NULLIF(o.sub_total, 0), 0) AS total_amount,
COALESCE(SUM(od.quantity), 0) AS total_quantity
FROM orders o
LEFT JOIN customers buyer ON buyer.id = o.buyer_id AND buyer.deleted_at IS NULL
LEFT JOIN order_details od ON od.order_id = o.id
WHERE o.deleted_at IS NULL AND COALESCE(o.executive_id, o.created_by) = @user_id
AND COALESCE(o.order_date, CAST(o.created_at AS date)) = @date
GROUP BY o.id, o.orderno, o.created_at, o.order_date, buyer.name, buyer.custom_fields, o.grand_total, o.total_amount, o.sub_total
ORDER BY o.created_at ASC, o.id ASC", cancellationToken, ("@user_id", userId), ("@date", date));

        foreach (var row in rows)
        {
            var createdAt = Obj(row, "created_at") is null ? date : Convert.ToDateTime(Obj(row, "created_at"), CultureInfo.InvariantCulture);
            var sort = createdAt.Date == date.Date ? createdAt : date.Date;
            var orderId = ULong(row, "id");
            var item = Activity("order-" + orderId, "Order", sort, "Order placed", string.Empty, string.Empty, string.Empty, "order");
            item["order_id"] = orderId;
            item["order_no"] = FirstNonEmpty(Str(row, "orderno"), "ORD-" + orderId);
            item["customer_name"] = FirstNonEmpty(Str(row, "buyer_name"), "Unknown Customer");
            item["customer"] = item["customer_name"];
            item["seller"] = "";
            item["total_amount"] = Dec(row, "total_amount");
            item["total_quantity"] = Long(row, "total_quantity");
            item["value"] = item["total_amount"];
            item["qty"] = item["total_quantity"];
            activities.Add((sort, item));
        }
    }

    private async Task AddCustomerActivities(List<(DateTime Sort, Dictionary<string, object?> Item)> activities, ulong userId, DateTime date, CancellationToken cancellationToken)
    {
        var rows = await QueryRows(@"SELECT c.id, c.created_at, c.updated_at, c.latitude, c.longitude, cd.visit_status, cd.updated_at AS detail_updated_at,
COALESCE(NULLIF(JSON_VALUE(c.custom_fields, '$.shop_name'), ''), NULLIF(JSON_VALUE(c.custom_fields, '$.owner_name'), ''), NULLIF(c.name, '')) AS customer_name,
COALESCE(NULLIF(JSON_VALUE(c.custom_fields, '$.address_line'), ''), NULLIF(a.address1, '')) AS address_line,
COALESCE(NULLIF(ctype.customertype_name, ''), NULLIF(ctype.type_name, ''), 'Customer') AS customer_type,
c.created_by, c.updated_by
FROM customers c
LEFT JOIN addresses a ON a.customer_id = c.id AND a.deleted_at IS NULL
LEFT JOIN customer_details cd ON cd.customer_id = c.id AND cd.deleted_at IS NULL
LEFT JOIN customer_types ctype ON ctype.id = c.customertype AND ctype.deleted_at IS NULL
WHERE c.deleted_at IS NULL
AND ((c.created_by = @user_id AND CAST(c.created_at AS date) = @date)
OR (c.updated_by = @user_id AND CAST(c.updated_at AS date) = @date)
OR (c.updated_by = @user_id AND CAST(cd.updated_at AS date) = @date AND cd.visit_status IN ('APPROVED', 'REJECTED')))", cancellationToken, ("@user_id", userId), ("@date", date));

        foreach (var row in rows)
        {
            var customerId = ULong(row, "id");
            var customerName = FirstNonEmpty(Str(row, "customer_name"), "Unknown Customer");
            var customerType = FirstNonEmpty(Str(row, "customer_type"), "Customer");
            if (ULong(row, "created_by") == userId && SameDate(row, "created_at", date))
            {
                var sort = Convert.ToDateTime(Obj(row, "created_at"), CultureInfo.InvariantCulture);
                var item = Activity("customer-add-" + customerId, "New Customer Registration", sort, "Customer added", FirstNonEmpty(Str(row, "address_line"), "No Location"), Str(row, "latitude"), Str(row, "longitude"), "customer_added");
                item["customer_name"] = customerName;
                item["customer"] = customerName;
                item["customer_type"] = customerType;
                item["entity_id"] = customerId;
                activities.Add((sort, item));
            }

            if (ULong(row, "updated_by") == userId && SameDate(row, "updated_at", date) && Obj(row, "updated_at") is not null && Obj(row, "created_at") is not null && Convert.ToDateTime(Obj(row, "updated_at"), CultureInfo.InvariantCulture) > Convert.ToDateTime(Obj(row, "created_at"), CultureInfo.InvariantCulture))
            {
                var sort = Convert.ToDateTime(Obj(row, "updated_at"), CultureInfo.InvariantCulture);
                var item = Activity("customer-edit-" + customerId, "Customer Edit", sort, "Customer updated", FirstNonEmpty(Str(row, "address_line"), "No Location"), Str(row, "latitude"), Str(row, "longitude"), "customer_edit");
                item["customer_name"] = customerName;
                item["customer"] = customerName;
                item["customer_type"] = customerType;
                item["entity_id"] = customerId;
                activities.Add((sort, item));
            }

            var status = Str(row, "visit_status").ToUpperInvariant();
            if (ULong(row, "updated_by") == userId && SameDate(row, "detail_updated_at", date) && (status == "APPROVED" || status == "REJECTED"))
            {
                var sort = Convert.ToDateTime(Obj(row, "detail_updated_at"), CultureInfo.InvariantCulture);
                var item = Activity("customer-status-" + customerId + "-" + status.ToLowerInvariant(), status == "APPROVED" ? "Customer Approved" : "Customer Rejected", sort,
                    status == "APPROVED" ? "Customer approved" : "Customer rejected", FirstNonEmpty(Str(row, "address_line"), "No Location"), Str(row, "latitude"), Str(row, "longitude"), status == "APPROVED" ? "customer_approved" : "customer_rejected");
                item["customer_name"] = customerName;
                item["customer"] = customerName;
                item["customer_type"] = customerType;
                item["entity_id"] = customerId;
                activities.Add((sort, item));
            }
        }
    }

    private async Task AddLoggedActivities(List<(DateTime Sort, Dictionary<string, object?> Item)> activities, ulong userId, DateTime date, CancellationToken cancellationToken)
    {
        var rows = await QueryRows(@"SELECT ua.id, ua.customerid, ua.latitude, ua.longitude, ua.time, ua.address, ua.description, ua.type,
COALESCE(NULLIF(JSON_VALUE(c.custom_fields, '$.shop_name'), ''), NULLIF(JSON_VALUE(c.custom_fields, '$.owner_name'), ''), NULLIF(c.name, '')) AS customer_name
FROM user_activities ua
LEFT JOIN customers c ON c.id = ua.customerid AND c.deleted_at IS NULL
WHERE ua.deleted_at IS NULL AND ua.userid = @user_id AND CAST(ua.time AS date) = @date", cancellationToken, ("@user_id", userId), ("@date", date));

        foreach (var row in rows)
        {
            var sort = Obj(row, "time") is null ? date : Convert.ToDateTime(Obj(row, "time"), CultureInfo.InvariantCulture);
            var type = FirstNonEmpty(Str(row, "type"), "activity");
            var item = Activity("activity-" + ULong(row, "id"), ActivityTitle(type), sort, FirstNonEmpty(Str(row, "description"), type), FirstNonEmpty(Str(row, "address"), "No Location"), Str(row, "latitude"), Str(row, "longitude"), NormalizeType(type));
            item["customer_name"] = Str(row, "customer_name");
            item["entity_id"] = ULong(row, "customerid");
            activities.Add((sort, item));
        }
    }

    private async Task<(List<object> Users, List<object> Branches, List<object> Zones, List<object> Designations)> ReportingLookups(List<ulong> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return (new List<object>(), new List<object>(), new List<object>(), new List<object>());
        var userCsv = string.Join(',', userIds);
        var userRows = await QueryRows($@"SELECT u.id, u.name, u.branch_id, b.id AS branch_id_value, b.branch_name, d.id AS zone_id, d.division_name AS zone_name, des.id AS designation_id, des.designation_name
FROM users u
LEFT JOIN branches b ON FIND_IN_SET(b.id, u.branch_id) AND b.deleted_at IS NULL
LEFT JOIN divisions d ON d.id = u.division_id AND d.deleted_at IS NULL
LEFT JOIN designations des ON des.id = u.designation_id AND des.deleted_at IS NULL
WHERE u.deleted_at IS NULL AND u.id IN ({userCsv})
ORDER BY u.name ASC", cancellationToken);

        var users = userRows.GroupBy(row => ULong(row, "id"))
            .Select(group =>
            {
                var first = group.First();
                return new
                {
                    id = ULong(first, "id"),
                    name = Str(first, "name"),
                    zone = Str(first, "zone_name"),
                    zone_id = NullableULong(first, "zone_id"),
                    branch = Str(group.FirstOrDefault(row => Obj(row, "branch_id_value") is not null) ?? first, "branch_name"),
                    branch_id = NullableULong(group.FirstOrDefault(row => Obj(row, "branch_id_value") is not null) ?? first, "branch_id_value")
                };
            })
            .Cast<object>()
            .ToList();

        var branches = userRows
            .Where(row => Obj(row, "branch_id_value") is not null)
            .GroupBy(row => ULong(row, "branch_id_value"))
            .Select(group =>
            {
                var first = group.First();
                return new
                {
                    id = ULong(first, "branch_id_value"),
                    name = Str(first, "branch_name"),
                    branch_name = Str(first, "branch_name"),
                    zone = Str(first, "zone_name"),
                    zone_id = NullableULong(first, "zone_id")
                };
            })
            .OrderBy(x => x.name)
            .Cast<object>()
            .ToList();

        var zones = userRows
            .Where(row => Obj(row, "zone_id") is not null)
            .GroupBy(row => ULong(row, "zone_id"))
            .Select(group =>
            {
                var first = group.First();
                return new { id = ULong(first, "zone_id"), name = Str(first, "zone_name"), zone_name = Str(first, "zone_name") };
            })
            .OrderBy(x => ZoneSortOrder(x.name))
            .ThenBy(x => x.name)
            .Cast<object>()
            .ToList();

        var designations = userRows
            .Where(row => Obj(row, "designation_id") is not null)
            .GroupBy(row => ULong(row, "designation_id"))
            .Select(group =>
            {
                var first = group.First();
                return new { id = ULong(first, "designation_id"), designation_name = Str(first, "designation_name") };
            })
            .OrderBy(x => x.designation_name)
            .Cast<object>()
            .ToList();

        return (users, branches, zones, designations);
    }

    private async Task<object> ActivityStats(List<ulong> userIds, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new { total_users = 0, total_secondary_customers = 0, total_master_distributors = 0, total_orders = 0, total_order_value = 0m, total_quantity = 0, total_checkins = 0 };
        }

        var userCsv = string.Join(',', userIds);
        var row = (await QueryRows($@"SELECT
(SELECT COUNT(*) FROM users u WHERE u.deleted_at IS NULL AND u.id IN ({userCsv})) AS total_users,
(SELECT COUNT(*) FROM customers c LEFT JOIN customer_types ct ON ct.id = c.customertype AND ct.deleted_at IS NULL WHERE c.deleted_at IS NULL AND c.created_by IN ({userCsv}) AND CAST(c.created_at AS date) BETWEEN @start_date AND @end_date AND NOT (c.customertype IN (1,3) OR COALESCE(ct.customertype_name, '') LIKE '%Distributor%' OR COALESCE(ct.type_name, '') LIKE '%Distributor%')) AS total_secondary_customers,
	(SELECT COUNT(*) FROM customers c LEFT JOIN customer_types ct ON ct.id = c.customertype AND ct.deleted_at IS NULL WHERE c.deleted_at IS NULL AND c.created_by IN ({userCsv}) AND CAST(c.created_at AS date) BETWEEN @start_date AND @end_date AND (c.customertype IN (1,3) OR COALESCE(ct.customertype_name, '') LIKE '%Distributor%' OR COALESCE(ct.type_name, '') LIKE '%Distributor%')) AS total_master_distributors,
	(SELECT COUNT(*) FROM orders o WHERE o.deleted_at IS NULL AND COALESCE(o.executive_id, o.created_by) IN ({userCsv}) AND COALESCE(o.order_date, CAST(o.created_at AS date)) BETWEEN @start_date AND @end_date) AS total_orders,
	(SELECT COALESCE(SUM(COALESCE(NULLIF(o.grand_total, 0), NULLIF(o.total_amount, 0), NULLIF(o.sub_total, 0), 0)), 0) FROM orders o WHERE o.deleted_at IS NULL AND COALESCE(o.executive_id, o.created_by) IN ({userCsv}) AND COALESCE(o.order_date, CAST(o.created_at AS date)) BETWEEN @start_date AND @end_date) AS total_order_value,
	(SELECT COALESCE(SUM(od.quantity), 0) FROM orders o INNER JOIN order_details od ON od.order_id = o.id WHERE o.deleted_at IS NULL AND COALESCE(o.executive_id, o.created_by) IN ({userCsv}) AND COALESCE(o.order_date, CAST(o.created_at AS date)) BETWEEN @start_date AND @end_date) AS total_quantity,
	(SELECT COUNT(*) FROM check_in ci WHERE ci.deleted_at IS NULL AND ci.user_id IN ({userCsv}) AND ci.checkin_date BETWEEN @start_date AND @end_date) AS total_checkins", cancellationToken,
            ("@start_date", startDate), ("@end_date", endDate))).First();

        return new
        {
            total_users = Long(row, "total_users"),
            total_secondary_customers = Long(row, "total_secondary_customers"),
            total_master_distributors = Long(row, "total_master_distributors"),
            total_orders = Long(row, "total_orders"),
            total_order_value = Dec(row, "total_order_value"),
            total_quantity = Long(row, "total_quantity"),
            total_checkins = Long(row, "total_checkins")
        };
    }

    private async Task<List<ulong>> FilterUserIds(List<ulong> userIds, Dictionary<string, string> request, bool includeSearchName, CancellationToken cancellationToken)
    {
        var ids = userIds.Where(id => id > 0).Distinct().ToList();
        var selectedUserId = ULongValue(request, "search_name") ?? ULongValue(request, "user_id");
        if (includeSearchName && selectedUserId.HasValue) ids = ids.Where(id => id == selectedUserId.Value).ToList();
        if (ids.Count == 0) return new List<ulong>();

        var where = new List<string> { $"u.id IN ({string.Join(',', ids)})", "u.deleted_at IS NULL" };
        var parameters = new List<(string, object?)>();
        var designationIds = ParseIds(Value(request, "designation"));
        if (designationIds.Count > 0) where.Add($"u.designation_id IN ({string.Join(',', designationIds)})");
        if (ULongValue(request, "zone_id") is { } zoneId)
        {
            where.Add("u.division_id = @zone_id");
            parameters.Add(("@zone_id", zoneId));
        }
        else if (!string.IsNullOrWhiteSpace(Value(request, "zone")))
        {
            where.Add("d.division_name LIKE @zone");
            parameters.Add(("@zone", "%" + Value(request, "zone") + "%"));
        }

        var branchIds = ParseIds(FirstNonEmpty(Value(request, "branch_id"), Value(request, "search_branches")));
        if (branchIds.Count > 0)
        {
            where.Add("(" + string.Join(" OR ", branchIds.Select(id => $"FIND_IN_SET({id}, u.branch_id)")) + ")");
        }
        else if (!string.IsNullOrWhiteSpace(Value(request, "branch")))
        {
            where.Add("b.branch_name LIKE @branch");
            parameters.Add(("@branch", "%" + Value(request, "branch") + "%"));
        }

        var rows = await QueryRows($@"SELECT DISTINCT u.id, u.name
FROM users u
LEFT JOIN divisions d ON d.id = u.division_id AND d.deleted_at IS NULL
LEFT JOIN branches b ON FIND_IN_SET(b.id, u.branch_id) AND b.deleted_at IS NULL
WHERE {string.Join(" AND ", where)}
ORDER BY u.name ASC", cancellationToken, parameters.ToArray());
        return rows.Select(row => ULong(row, "id")).Where(id => id > 0).ToList();
    }

    private async Task<List<ulong>> VisibleReportingUserIds(ulong userId, CancellationToken cancellationToken)
    {
        if (await HasAdminRole(userId, cancellationToken))
        {
            return await NonCustomerUserIds(cancellationToken);
        }

        if (await HasRoleId(userId, RoleIds.BranchManager, cancellationToken))
        {
            var actor = (await QueryRows("SELECT branch_id FROM users WHERE id=@user_id AND deleted_at IS NULL", cancellationToken, ("@user_id", userId))).FirstOrDefault();
            var branches = actor is null ? string.Empty : Str(actor, "branch_id");
            if (string.IsNullOrWhiteSpace(branches)) return [userId];
            return await NonCustomerUserIdsByBranches(branches, cancellationToken);
        }

        var rows = await QueryRows(@"SELECT u.id, u.reportingid
FROM users u
WHERE u.deleted_at IS NULL
AND NOT EXISTS (
    SELECT 1 FROM model_has_roles m
    INNER JOIN roles r ON r.id = m.role_id
    WHERE m.model_id = u.id
    AND (m.role_id = 61 OR r.name = 'Distributor')
)", cancellationToken);
        var visible = new HashSet<ulong> { userId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var row in rows)
            {
                var id = ULong(row, "id");
                var reportingId = ULong(row, "reportingid");
                if (reportingId > 0 && visible.Contains(reportingId) && visible.Add(id)) changed = true;
            }
        }
        return visible.ToList();
    }

    private async Task<List<ulong>> NonCustomerUserIds(CancellationToken cancellationToken)
    {
        var rows = await QueryRows(@"SELECT u.id
FROM users u
WHERE u.deleted_at IS NULL
AND NOT EXISTS (
    SELECT 1 FROM model_has_roles m
    INNER JOIN roles r ON r.id = m.role_id
    WHERE m.model_id = u.id
    AND (m.role_id = 61 OR r.name = 'Distributor')
)", cancellationToken);
        return rows.Select(row => ULong(row, "id")).Where(id => id > 0).ToList();
    }

    private async Task<List<ulong>> NonCustomerUserIdsByBranches(string branches, CancellationToken cancellationToken)
    {
        var branchIds = ParseIds(branches);
        if (branchIds.Count == 0) return [];
        var branchWhere = string.Join(" OR ", branchIds.Select(id => $"FIND_IN_SET({id}, u.branch_id)"));
        var rows = await QueryRows($@"SELECT u.id FROM users u
WHERE u.deleted_at IS NULL AND ({branchWhere})
AND NOT EXISTS (SELECT 1 FROM model_has_roles m INNER JOIN roles r ON r.id=m.role_id
WHERE m.model_id=u.id AND (m.role_id={RoleIds.Customer} OR r.name='Distributor'))", cancellationToken);
        return rows.Select(row => ULong(row, "id")).Where(id => id > 0).Distinct().ToList();
    }

    private async Task<bool> HasRoleId(ulong userId, ulong roleId, CancellationToken cancellationToken) =>
        await QueryScalarLong("SELECT COUNT(*) FROM model_has_roles WHERE model_id=@user_id AND role_id=@role_id", cancellationToken,
            ("@user_id", userId), ("@role_id", roleId)) > 0;

    private async Task<bool> HasAnyRole(ulong userId, CancellationToken cancellationToken, params string[] roles)
    {
        var quoted = string.Join(',', roles.Select(role => $"'{role.Replace("'", "''")}'"));
        return await QueryScalarLong($@"SELECT COUNT(*)
FROM model_has_roles m
INNER JOIN roles r ON r.id = m.role_id
WHERE m.model_id = @user_id AND r.name IN ({quoted})", cancellationToken, ("@user_id", userId)) > 0;
    }

    private async Task<bool> HasAdminRole(ulong userId, CancellationToken cancellationToken) =>
        await QueryScalarLong(@"SELECT COUNT(*)
FROM model_has_roles m
INNER JOIN roles r ON r.id = m.role_id
WHERE m.model_id = @user_id AND LOWER(r.name) LIKE '%admin%'", cancellationToken, ("@user_id", userId)) > 0;

    private async Task<Dictionary<string, string>> RequestValues(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in Request.Query) values[query.Key] = query.Value.ToString();
        if (Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var document = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
            }
        }
        return values;
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> QueryRows(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerSql.Normalize(sql);
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

    private async Task<long> QueryScalarLong(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerSql.Normalize(sql);
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddParameters(IDbCommand command, IEnumerable<(string Name, object? Value)> parameters)
    {
        foreach (var parameter in parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = parameter.Name;
            dbParameter.Value = SqlServerSql.ParameterValue(parameter.Value);
            command.Parameters.Add(dbParameter);
        }
    }

    private static string AttendanceStatus(Dictionary<string, object?> row)
    {
        var raw = Str(row, "attendance_status");
        return raw switch
        {
            "1" => "Present",
            "2" => "Absent",
            "3" => "Leave",
            "4" => "Half Day",
            _ => string.IsNullOrWhiteSpace(raw) || raw == "0" ? "Present" : raw
        };
    }

    private static int ZoneSortOrder(string zoneName)
    {
        var lower = zoneName.ToLowerInvariant();
        var order = new[] { "north", "east", "west", "south" };
        for (var i = 0; i < order.Length; i++) if (lower.Contains(order[i], StringComparison.Ordinal)) return i;
        return order.Length;
    }

    private static DateTime IndiaNow() => DateTime.UtcNow.AddHours(5).AddMinutes(30);
    private ulong CurrentUserId() => ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : throw new InvalidOperationException("Unauthenticated.");
    private static string? Value(IReadOnlyDictionary<string, string> body, string key)
    {
        if (!body.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("undefined", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }
    private static int IntValue(IReadOnlyDictionary<string, string> body, string key, int fallback) => int.TryParse(Value(body, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static ulong? ULongValue(IReadOnlyDictionary<string, string> body, string key) => ulong.TryParse(Value(body, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static DateTime? DateValue(IReadOnlyDictionary<string, string> body, string key) => DateTime.TryParse(Value(body, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value) ? value.Date : null;
    private static DateTime? ActivityDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy" };
        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed.Date;

        // Mobile builds have sent values like "undefined-undefined-2026-07-03".
        // Laravel silently turns that into 1970-01-01; keeping the real trailing date is safer.
        if (trimmed.Length >= 10)
        {
            var tail = trimmed[^10..];
            if (DateTime.TryParseExact(tail, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) return parsed.Date;
        }

        return null;
    }

    private static List<ulong> ParseIds(string? csv) => (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => ulong.TryParse(x, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    private static object? Obj(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not DBNull ? value : null;
    private static string Str(Dictionary<string, object?> row, string key) => Convert.ToString(Obj(row, key), CultureInfo.InvariantCulture) ?? string.Empty;
    private static ulong ULong(Dictionary<string, object?> row, string key) => Obj(row, key) is null ? 0 : Convert.ToUInt64(Obj(row, key), CultureInfo.InvariantCulture);
    private static ulong? NullableULong(Dictionary<string, object?> row, string key) => Obj(row, key) is null ? null : Convert.ToUInt64(Obj(row, key), CultureInfo.InvariantCulture);
    private static long Long(Dictionary<string, object?> row, string key) => Obj(row, key) is null ? 0 : Convert.ToInt64(Obj(row, key), CultureInfo.InvariantCulture);
    private static decimal Dec(Dictionary<string, object?> row, string key) => Obj(row, key) is null ? 0m : Convert.ToDecimal(Obj(row, key), CultureInfo.InvariantCulture);
    private static string DateOnlyString(Dictionary<string, object?> row, string key) => Obj(row, key) is null ? string.Empty : Convert.ToDateTime(Obj(row, key), CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static bool SameDate(Dictionary<string, object?> row, string key, DateTime date) => Obj(row, key) is not null && Convert.ToDateTime(Obj(row, key), CultureInfo.InvariantCulture).Date == date.Date;
    private static DateTime CombineDateTime(DateTime date, object? time)
    {
        if (time is null) return date.Date;
        return time is TimeSpan span ? date.Date.Add(span) : date.Date.Add(Convert.ToDateTime(time, CultureInfo.InvariantCulture).TimeOfDay);
    }

    private static Dictionary<string, object?> Activity(string id, string title, DateTime sort, string? description, string? location, string? latitude, string? longitude, string type)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = id,
            ["title"] = title,
            ["time"] = sort.ToString("hh:mm tt", CultureInfo.InvariantCulture),
            ["date"] = sort.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["description"] = description ?? string.Empty,
            ["location"] = location ?? string.Empty,
            ["latitude"] = latitude ?? string.Empty,
            ["longitude"] = longitude ?? string.Empty,
            ["type"] = type
        };
    }

    private static string ActivityTitle(string type)
    {
        var normalized = type.Replace('_', ' ').Replace('-', ' ').Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return "Activity";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.ToLowerInvariant());
    }

    private static string NormalizeType(string type)
    {
        var normalized = type.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return string.IsNullOrWhiteSpace(normalized) ? "activity" : normalized;
    }

    private static string? TimeString(Dictionary<string, object?> row, string key)
    {
        var value = Obj(row, key);
        if (value is null) return null;
        return value is TimeSpan span
            ? DateTime.Today.Add(span).ToString("hh:mm tt", CultureInfo.InvariantCulture)
            : Convert.ToDateTime(value, CultureInfo.InvariantCulture).ToString("hh:mm tt", CultureInfo.InvariantCulture);
    }
    private static string ExceptionMessage(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null) current = current.InnerException;
        return current.Message;
    }
}
