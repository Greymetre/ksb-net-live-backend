using System.Data;
using System.Globalization;
using System.Security.Claims;
using Api.Filters;
using Application.Interfaces.Repositories;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Beats Management > Route Optimized.
///
/// Answers one question for one employee on one day: in what order should the counters be
/// visited, and when will each be reached. The beat of the day decides which counters are
/// in play, the employee's own first position that morning decides where the walk starts,
/// and a nearest-neighbour pass decides the order.
///
/// It is a suggestion, not a schedule - it never writes anything.
/// </summary>
[ApiController]
[Authorize]
[Route("api/beat-route")]
public sealed class BeatRouteOptimizerController : ControllerBase
{
    /// <summary>What the clock is moved on by between two stops, and how long a counter is
    /// assumed to hold the employee once reached. Road distance is longer than the straight
    /// line these are applied to, so the pace is deliberately modest.</summary>
    private const double AverageSpeedKmph = 22;
    private const int MinutesPerStop = 20;

    /// <summary>When the day has no punch-in and no ping to start from.</summary>
    private const string DefaultStartTime = "09:00:00";

    /// <summary>How many counters one day's route is allowed to hold. Live data has a
    /// catch-all beat carrying over thirteen thousand counters; sequencing all of them
    /// would answer a question nobody asked. The nearest this many are planned and the
    /// answer says how many the beat actually holds.</summary>
    private const int DayPlanStops = 30;

    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;

    public BeatRouteOptimizerController(AppDbContext db, IHrRepository hr) { _db = db; _hr = hr; }

    /// <summary>The employees the caller may look at who actually carry a beat - assigned to
    /// one, or scheduled on one. A dropdown feed, so ungated like the other option lists; the
    /// route itself carries the permission.</summary>
    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        var visible = (await _hr.GetVisibleUserIdsAsync(CurrentUserId(), ct)).Distinct().ToArray();
        if (visible.Length == 0) return Ok(new { status = true, users = Array.Empty<object>() });

        var withBeats = (await QueryAsync($@"SELECT DISTINCT CAST(user_id AS bigint) user_id FROM beat_users WHERE user_id IN ({Join(visible)})
UNION SELECT DISTINCT CAST(user_id AS bigint) FROM beat_schedules WHERE user_id IN ({Join(visible)})", ct))
            .Select(row => ULong(row, "user_id")).Where(id => id > 0).ToHashSet();

        var users = await _db.Users.AsNoTracking()
            .Where(x => withBeats.Contains(x.Id) && !x.IsDeleted && x.DeletedAt == null)
            .OrderBy(x => x.Name)
            .Select(x => new { id = x.Id, name = x.Name, code = x.EmployeeCodes })
            .ToListAsync(ct);

        return Ok(new
        {
            status = true,
            users = users.Select(x => new { x.id, name = string.IsNullOrWhiteSpace(x.code) ? x.name : $"{x.name} ({x.code})" })
        });
    }

    /// <summary>
    /// Which beat the day should be planned from, for one employee on one date.
    ///
    /// A beat scheduled on that date settles it - that is the day's work, and the screen
    /// asks nothing further. With nothing scheduled the employee's assigned beats come
    /// back instead, the first of them marked as the default, so a manager can look at any
    /// one of them. A dropdown feed, so ungated like the other option lists.
    /// </summary>
    [HttpGet("beats")]
    public async Task<IActionResult> Beats([FromQuery(Name = "user_id")] ulong? userId, [FromQuery] DateTime? date, CancellationToken ct)
    {
        if (!userId.HasValue) return Ok(new { status = true, has_schedule = false, beats = Array.Empty<object>() });

        var visible = (await _hr.GetVisibleUserIdsAsync(CurrentUserId(), includeInactive: true, ct)).ToHashSet();
        if (!visible.Contains(userId.Value))
            return StatusCode(403, new { status = false, message = "You are not allowed to view this employee." });

        var day = (date ?? DateTime.Today).Date;
        var scheduled = await ScheduledBeatIdsAsync(userId.Value, day, ct);
        var ids = scheduled.Count > 0 ? scheduled : await AssignedBeatIdsAsync(userId.Value, ct);

        var beats = await _db.Beats.AsNoTracking().Where(x => ids.Contains(x.Id))
            .OrderBy(x => x.BeatName).Select(x => new { id = x.Id, name = x.BeatName }).ToListAsync(ct);

        return Ok(new
        {
            status = true,
            has_schedule = scheduled.Count > 0,
            // With a schedule the beat is not the manager's to pick, so the name is sent for
            // the screen to show as plain text.
            scheduled_beat_name = scheduled.Count > 0 ? string.Join(", ", beats.Select(x => x.name)) : null,
            default_beat_id = beats.Count > 0 ? beats[0].id : (ulong?)null,
            beats,
        });
    }

    private async Task<List<ulong>> ScheduledBeatIdsAsync(ulong userId, DateTime day, CancellationToken ct) =>
        (await QueryAsync($@"SELECT DISTINCT CAST(beat_id AS bigint) beat_id FROM beat_schedules
WHERE user_id = {userId} AND CAST(beat_date AS date) = '{day:yyyy-MM-dd}' AND beat_id IS NOT NULL", ct))
            .Select(row => ULong(row, "beat_id")).Where(id => id > 0).Distinct().ToList();

    private async Task<List<ulong>> AssignedBeatIdsAsync(ulong userId, CancellationToken ct) =>
        (await QueryAsync($"SELECT DISTINCT CAST(beat_id AS bigint) beat_id FROM beat_users WHERE user_id = {userId} AND beat_id IS NOT NULL", ct))
            .Select(row => ULong(row, "beat_id")).Where(id => id > 0).Distinct().ToList();

    /// <summary>The optimized visit order for one employee on one date.</summary>
    [HttpGet]
    [RequirePermission("beat_route_optimizer.build")]
    public async Task<IActionResult> Build([FromQuery(Name = "user_id")] ulong? userId, [FromQuery] DateTime? date,
        [FromQuery(Name = "beat_id")] ulong? beatId, CancellationToken ct)
    {
        if (!userId.HasValue) return BadRequest(new { status = false, message = "Please select an employee." });
        if (!date.HasValue) return BadRequest(new { status = false, message = "Please select a date." });

        var visible = (await _hr.GetVisibleUserIdsAsync(CurrentUserId(), includeInactive: true, ct)).ToHashSet();
        if (!visible.Contains(userId.Value))
            return StatusCode(403, new { status = false, message = "You are not allowed to view this employee." });

        var user = await (from u in _db.Users.AsNoTracking()
                          where u.Id == userId.Value
                          join d in _db.Designations.AsNoTracking() on u.DesignationId equals d.Id into designations
                          from designation in designations.DefaultIfEmpty()
                          select new { u.Id, u.Name, Designation = designation != null ? designation.DesignationName : null })
                         .FirstOrDefaultAsync(ct);
        if (user is null) return NotFound(new { status = false, message = "Employee not found." });

        var day = date.Value.Date;

        // The beat of the day. A beat scheduled on this date is the day's work and settles
        // it. With nothing scheduled the manager picks from the employee's assigned beats,
        // and with no pick the first of them is planned - the same one the dropdown opens
        // on. The answer says which of the two it used.
        var scheduled = await ScheduledBeatIdsAsync(userId.Value, day, ct);
        var assigned = await AssignedBeatIdsAsync(userId.Value, ct);

        var planSource = scheduled.Count > 0 ? "scheduled" : "assigned";
        List<ulong> beatIds;
        if (scheduled.Count > 0)
        {
            beatIds = scheduled;
        }
        else if (beatId.HasValue && beatId.Value > 0)
        {
            // Only a beat this employee actually carries, so a hand-typed id cannot be used
            // to read a beat the caller was never shown.
            if (!assigned.Contains(beatId.Value))
                return StatusCode(403, new { status = false, message = "That beat is not assigned to this employee." });
            beatIds = [beatId.Value];
        }
        else
        {
            var first = await _db.Beats.AsNoTracking().Where(x => assigned.Contains(x.Id))
                .OrderBy(x => x.BeatName).Select(x => x.Id).FirstOrDefaultAsync(ct);
            beatIds = first > 0 ? [first] : [];
        }

        if (beatIds.Count == 0)
            return Ok(Empty($"No beat is scheduled or assigned to {user.Name} for {day:dd MMM yyyy}."));

        var beatNames = await _db.Beats.AsNoTracking().Where(x => beatIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.BeatName, ct);

        var stops = await StopsOnBeatsAsync(beatIds, beatNames, ct);
        if (stops.Count == 0)
            return Ok(Empty($"No counter is aligned to {string.Join(", ", beatNames.Values)}."));

        var start = await StartPointAsync(userId.Value, day, ct);

        // Counters with no coordinates cannot be placed in a walk, so they are listed after
        // the route rather than dropped - somebody still has to visit them.
        var mapped = stops.Where(x => x.Latitude.HasValue && x.Longitude.HasValue).ToList();
        var unmappedAll = stops.Where(x => !x.Latitude.HasValue || !x.Longitude.HasValue).ToList();

        // Only a day's worth is planned, and the walk is cut as it is built rather than
        // after - ordering thirteen thousand counters to then throw almost all of them away
        // is the expensive half of this request.
        var ordered = OrderByNearestNeighbour(mapped, start, DayPlanStops);
        var unmapped = unmappedAll.Take(Math.Max(0, DayPlanStops - ordered.Count)).ToList();

        // The history is only wanted for the counters actually on the plan.
        await AttachVisitsAsync(ordered.Concat(unmapped).ToList(), userId.Value, day, ct);

        var clock = DateTime.Parse($"{day:yyyy-MM-dd} {start?.Time ?? DefaultStartTime}", CultureInfo.InvariantCulture);
        var startTime = clock;
        double totalKm = 0;
        double? previousLat = start?.Latitude, previousLng = start?.Longitude;

        for (var index = 0; index < ordered.Count; index++)
        {
            var stop = ordered[index];
            var legKm = previousLat.HasValue && previousLng.HasValue
                ? Math.Round(DistanceKm(previousLat.Value, previousLng.Value, stop.Latitude!.Value, stop.Longitude!.Value), 1)
                : 0;
            totalKm += legKm;
            clock = clock.AddMinutes(Math.Round(legKm / AverageSpeedKmph * 60));
            stop.Sequence = index + 1;
            stop.LegKm = legKm;
            stop.Eta = clock.ToString("h:mm tt", CultureInfo.InvariantCulture);
            clock = clock.AddMinutes(MinutesPerStop);
            previousLat = stop.Latitude;
            previousLng = stop.Longitude;
        }

        for (var index = 0; index < unmapped.Count; index++) unmapped[index].Sequence = ordered.Count + index + 1;

        var route = ordered.Concat(unmapped).ToList();
        return Ok(new
        {
            status = true,
            plan_source = planSource,
            beat_ids = beatIds,
            user = new { id = user.Id, name = user.Name, designation = FirstFilled(user.Designation, "Field employee") },
            beat_name = string.Join(", ", beatNames.Values),
            date = day.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            start,
            summary = new
            {
                stops = route.Count,
                mapped_stops = ordered.Count,
                unmapped_stops = unmapped.Count,
                // What the beat holds, against what this day's plan covers.
                beat_counters = stops.Count,
                beat_unmapped = unmappedAll.Count,
                day_plan_limit = DayPlanStops,
                capped = stops.Count > route.Count,
                visited = route.Count(x => x.Visited),
                route_km = Math.Round(totalKm, 1),
                start_time = startTime.ToString("h:mm tt", CultureInfo.InvariantCulture),
                finish_time = ordered.Count > 0 ? clock.ToString("h:mm tt", CultureInfo.InvariantCulture) : null,
            },
            stops = route,
        });
    }

    private static object Empty(string message) => new { status = true, empty = true, message, stops = Array.Empty<object>() };

    /// <summary>The counters aligned to these beats. A counter on two beats is one stop.</summary>
    private async Task<List<RouteStop>> StopsOnBeatsAsync(IReadOnlyCollection<ulong> beatIds, IReadOnlyDictionary<ulong, string> beatNames, CancellationToken ct)
    {
        var rows = await QueryAsync($@"SELECT bc.beat_id, c.id customer_id, c.customertype, c.name, c.mobile, c.customer_code, c.custom_fields,
    COALESCE(city.city_name, '') city_name
FROM beat_customers bc
INNER JOIN customers c ON c.id = COALESCE(bc.customer_id, bc.distributor_id) AND c.deleted_at IS NULL AND c.active = 'Y'
OUTER APPLY (SELECT TOP (1) a.city_id FROM addresses a WHERE a.customer_id = c.id AND a.deleted_at IS NULL ORDER BY a.id DESC) addr
LEFT JOIN cities city ON city.id = COALESCE(addr.city_id,
    TRY_CONVERT(decimal(20,0), NULLIF(JSON_VALUE(c.custom_fields, '$.city_id'), '')),
    TRY_CONVERT(decimal(20,0), NULLIF(JSON_VALUE(c.custom_fields, '$.billing_city'), '')))
WHERE bc.beat_id IN ({Join(beatIds)}) AND (bc.active = 'Y' OR bc.active IS NULL)", ct);

        var stops = new Dictionary<ulong, RouteStop>();
        foreach (var row in rows)
        {
            var id = ULong(row, "customer_id");
            if (id == 0 || stops.ContainsKey(id)) continue;
            var fields = Str(row, "custom_fields");
            var (latitude, longitude) = SplitGps(JsonString(fields, "gps_location"));
            var isDealer = ULong(row, "customertype") == 1;
            stops[id] = new RouteStop
            {
                EntityId = id,
                EntityType = isDealer ? "dealer" : "retailer",
                Name = FirstFilled(JsonString(fields, "shop_name"), Str(row, "name")),
                Code = FirstFilled(Str(row, "customer_code"), JsonString(fields, "distributor_code")),
                Category = isDealer ? "Dealer" : FirstFilled(JsonString(fields, "sub_type"), "Retailer"),
                Mobile = FirstFilled(JsonString(fields, "mobile_number"), Str(row, "mobile")),
                Address = JsonString(fields, "address_line"),
                City = Str(row, "city_name"),
                BeatName = beatNames.TryGetValue(ULong(row, "beat_id"), out var beat) ? beat : string.Empty,
                Latitude = latitude,
                Longitude = longitude,
            };
        }
        return stops.Values.ToList();
    }

    /// <summary>
    /// When each counter was last seen by this employee, and whether it has been seen today.
    ///
    /// A check-in records the counter under entity_id, and older rows under customer_id, so
    /// both are matched - otherwise a counter visited last month reads as never visited.
    /// The bands are the ones the field team already knows: over 30 days overdue, 15 days
    /// and up a follow-up, anything newer a routine call.
    /// </summary>
    private async Task AttachVisitsAsync(List<RouteStop> stops, ulong userId, DateTime day, CancellationToken ct)
    {
        if (stops.Count == 0) return;
        var ids = Join(stops.Select(x => x.EntityId).ToList());

        var today = await QueryAsync($@"SELECT CAST(COALESCE(entity_id, customer_id) AS bigint) entity_id, MIN(checkin_time) checkin_time
FROM check_in
WHERE deleted_at IS NULL AND user_id = {userId} AND CAST(checkin_date AS date) = '{day:yyyy-MM-dd}'
  AND (entity_id IN ({ids}) OR customer_id IN ({ids}))
GROUP BY COALESCE(entity_id, customer_id)", ct);
        var seenToday = today.ToDictionary(row => ULong(row, "entity_id"));

        var previous = await QueryAsync($@"SELECT CAST(COALESCE(entity_id, customer_id) AS bigint) entity_id, MAX(CAST(checkin_date AS date)) last_date
FROM check_in
WHERE deleted_at IS NULL AND user_id = {userId} AND CAST(checkin_date AS date) < '{day:yyyy-MM-dd}'
  AND (entity_id IN ({ids}) OR customer_id IN ({ids}))
GROUP BY COALESCE(entity_id, customer_id)", ct);
        var lastSeen = previous.ToDictionary(row => ULong(row, "entity_id"));

        foreach (var stop in stops)
        {
            if (seenToday.TryGetValue(stop.EntityId, out var todayRow))
            {
                stop.Visited = true;
                stop.VisitedAt = TimeLabel(Obj(todayRow, "checkin_time"));
            }

            int? days = null;
            if (lastSeen.TryGetValue(stop.EntityId, out var lastRow) && Obj(lastRow, "last_date") is DateTime last)
            {
                stop.LastVisited = last.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
                days = (int)(day - last.Date).TotalDays;
                stop.DaysSinceVisit = days;
            }

            (stop.Priority, stop.PriorityLabel) =
                stop.Visited ? ("visited", "VISITED")
                : days is null ? ("new", "NEW COUNTER")
                : days > 30 ? ("overdue", "OVERDUE VISIT")
                : days >= 15 ? ("followup", "FOLLOW-UP DUE")
                : ("routine", "ROUTINE CHECK-IN");
        }
    }

    /// <summary>Where the employee's day began: the punch-in, and failing that the first ping
    /// the phone sent. Without either, the route starts from its own first counter.</summary>
    private async Task<RouteStart?> StartPointAsync(ulong userId, DateTime day, CancellationToken ct)
    {
        var punch = (await QueryAsync($@"SELECT TOP (1) punchin_latitude, punchin_longitude, punchin_time, punchin_address
FROM attendances WHERE deleted_at IS NULL AND user_id = {userId} AND CAST(punchin_date AS date) = '{day:yyyy-MM-dd}'
  AND punchin_latitude IS NOT NULL AND punchin_longitude IS NOT NULL ORDER BY id DESC", ct)).FirstOrDefault();
        if (punch is not null)
        {
            // The app writes these the wrong way round: punchin_latitude holds the longitude.
            // Measured on live data - 5,898 of 5,899 values in that column are longitudes.
            // Reading them as named would start every route in the wrong country.
            var (latitude, longitude) = ParsePair(Str(punch, "punchin_longitude"), Str(punch, "punchin_latitude"));
            if (latitude.HasValue)
                return new RouteStart("Punch in", latitude, longitude, TimeText(Obj(punch, "punchin_time")), Str(punch, "punchin_address"));
        }

        var ping = (await QueryAsync($@"SELECT TOP (1) latitude, longitude, [time], address
FROM user_live_locations WHERE deleted_at IS NULL AND userid = {userId} AND CAST([time] AS date) = '{day:yyyy-MM-dd}'
ORDER BY [time] ASC", ct)).FirstOrDefault();
        if (ping is not null)
        {
            var (latitude, longitude) = ParsePair(Str(ping, "latitude"), Str(ping, "longitude"));
            if (latitude.HasValue)
                return new RouteStart("First location", latitude, longitude, TimeText(Obj(ping, "time")), Str(ping, "address"));
        }
        return null;
    }

    /// <summary>Nearest neighbour from the start point: take the closest counter, stand there,
    /// take the closest of what is left. Not the shortest possible route - the shortest is a
    /// travelling-salesman problem - but it is the order a person would actually walk.</summary>
    private static List<RouteStop> OrderByNearestNeighbour(List<RouteStop> stops, RouteStart? start, int limit)
    {
        var remaining = new List<RouteStop>(stops);
        var ordered = new List<RouteStop>(Math.Min(stops.Count, limit));
        double? currentLat = start?.Latitude, currentLng = start?.Longitude;

        while (remaining.Count > 0 && ordered.Count < limit)
        {
            var bestIndex = 0;
            if (currentLat.HasValue && currentLng.HasValue)
            {
                var best = double.MaxValue;
                for (var index = 0; index < remaining.Count; index++)
                {
                    var distance = DistanceKm(currentLat.Value, currentLng.Value, remaining[index].Latitude!.Value, remaining[index].Longitude!.Value);
                    if (distance >= best) continue;
                    best = distance;
                    bestIndex = index;
                }
            }
            var next = remaining[bestIndex];
            remaining.RemoveAt(bestIndex);
            ordered.Add(next);
            currentLat = next.Latitude;
            currentLng = next.Longitude;
        }
        return ordered;
    }

    /// <summary>Great-circle distance in kilometres.</summary>
    private static double DistanceKm(double fromLat, double fromLng, double toLat, double toLng)
    {
        const double earthRadiusKm = 6371;
        var dLat = (toLat - fromLat) * Math.PI / 180;
        var dLng = (toLng - fromLng) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(fromLat * Math.PI / 180) * Math.Cos(toLat * Math.PI / 180) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static (double? Latitude, double? Longitude) SplitGps(string? value)
    {
        var parts = (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? ParsePair(parts[0], parts[1]) : (null, null);
    }

    private static (double? Latitude, double? Longitude) ParsePair(string? latitude, string? longitude)
    {
        if (!double.TryParse(latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return (null, null);
        if (!double.TryParse(longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)) return (null, null);
        // 0,0 is the Atlantic, not a counter - live data uses it where the phone had no fix.
        if (Math.Abs(lat) < 0.0001 && Math.Abs(lng) < 0.0001) return (null, null);
        return (lat, lng);
    }

    private static string? TimeLabel(object? value)
    {
        var text = TimeText(value);
        return text is not null && DateTime.TryParse($"2000-01-01 {text}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
            ? moment.ToString("h:mm tt", CultureInfo.InvariantCulture) : text;
    }

    private static string? TimeText(object? value) => value switch
    {
        null => null,
        TimeSpan span => DateTime.Today.Add(span).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        DateTime moment => moment.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string Join<T>(IEnumerable<T> values) => string.Join(',', values);
    private ulong CurrentUserId() => ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : throw new UnauthorizedAccessException();

    private async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static object? Obj(IReadOnlyDictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) && value is not DBNull ? value : null;
    private static string Str(IReadOnlyDictionary<string, object?> row, string key) => Convert.ToString(Obj(row, key), CultureInfo.InvariantCulture) ?? string.Empty;
    private static ulong ULong(IReadOnlyDictionary<string, object?> row, string key) => Obj(row, key) is null ? 0 : Convert.ToUInt64(Obj(row, key), CultureInfo.InvariantCulture);
    private static string FirstFilled(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string JsonString(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(key, out var value) ? value.ToString() : string.Empty;
        }
        catch (System.Text.Json.JsonException) { return string.Empty; }
    }
}

public sealed record RouteStart(string Source, double? Latitude, double? Longitude, string? Time, string? Address);

public sealed class RouteStop
{
    public ulong EntityId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Mobile { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string BeatName { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int Sequence { get; set; }
    public double? LegKm { get; set; }
    public string? Eta { get; set; }
    public bool Visited { get; set; }
    public string? LastVisited { get; set; }
    public int? DaysSinceVisit { get; set; }
    public string Priority { get; set; } = "new";
    public string PriorityLabel { get; set; } = "NEW COUNTER";
    public string? VisitedAt { get; set; }
}
