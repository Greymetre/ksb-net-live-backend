using System.Security.Claims;
using Api.Filters;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api/beats")]
public sealed class BeatsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHrRepository _hrRepository;

    public BeatsController(AppDbContext db, IHrRepository hrRepository)
    {
        _db = db;
        _hrRepository = hrRepository;
    }

    [HttpGet]
    [RequirePermission("beat.view")]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 10, CancellationToken ct = default)
    {
        var query = _db.Beats.AsNoTracking();
        List<BeatUserRow>? visibleBeatUsers = null;
        if (await IsDistributorUserAsync(ct))
        {
            var visibleUserIds = (await _hrRepository.GetVisibleUserIdsAsync(CurrentUserId(), ct)).ToHashSet();
            visibleBeatUsers = (await _db.Database.SqlQueryRaw<BeatUserRow>(
                    "SELECT CAST(beat_id AS bigint) AS BeatId, CAST(user_id AS bigint) AS UserId FROM beat_users WHERE beat_id IS NOT NULL AND user_id IS NOT NULL")
                .ToListAsync(ct))
                .Where(x => x.BeatId > 0 && x.UserId > 0 && visibleUserIds.Contains((ulong)x.UserId))
                .ToList();
            var visibleBeatIds = visibleBeatUsers.Select(x => (ulong)x.BeatId).Distinct().ToArray();
            query = query.Where(x => visibleBeatIds.Contains(x.Id));
        }
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => x.BeatName.Contains(search) || x.Description.Contains(search));

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var total = await query.LongCountAsync(ct);
        var beats = await query.OrderByDescending(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = beats.Select(x => x.Id).ToArray();
        var users = visibleBeatUsers is null
            ? await _db.Database.SqlQueryRaw<BeatCountRow>(
                "SELECT CAST(beat_id AS bigint) AS BeatId, COUNT(*) AS Total FROM beat_users WHERE beat_id IS NOT NULL GROUP BY beat_id").ToListAsync(ct)
            : visibleBeatUsers.GroupBy(x => x.BeatId).Select(x => new BeatCountRow { BeatId = x.Key, Total = x.Count() }).ToList();
        var customers = await _db.Database.SqlQueryRaw<BeatCountRow>(
            "SELECT CAST(beat_id AS bigint) AS BeatId, COUNT(*) AS Total FROM beat_customers WHERE beat_id IS NOT NULL AND customer_id IS NOT NULL GROUP BY beat_id").ToListAsync(ct);
        var schedules = await _db.BeatSchedules.AsNoTracking().Where(x => x.BeatId != null && ids.Contains(x.BeatId.Value))
            .GroupBy(x => x.BeatId!.Value).Select(x => new { BeatId = x.Key, Total = x.Count() }).ToListAsync(ct);

        return Ok(new { beats = beats.Select(x => new {
            x.Id, x.Active, x.BeatName, x.Description, x.CityId, x.CreatedAt, x.UpdatedAt,
            userCount = users.FirstOrDefault(y => y.BeatId == (long)x.Id)?.Total ?? 0,
            customerCount = customers.FirstOrDefault(y => y.BeatId == (long)x.Id)?.Total ?? 0,
            scheduleCount = schedules.FirstOrDefault(y => y.BeatId == x.Id)?.Total ?? 0
        }), total, page, page_size = pageSize });
    }

    // Dropdown values only, so no permission gate.
    // Dropdown feed: beat names fill the filter on the Customers list, which is read by
    // people who hold no beat-master permission. Only id and name leave here; the beat
    // listing below stays gated.
    [HttpGet("names")]
    public async Task<IActionResult> BeatNames([FromQuery] string? search, CancellationToken ct)
    {
        // The beats table has no deleted_at column, so active is what filters the list.
        var query = _db.Beats.AsNoTracking().Where(x => x.Active == "Y");
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.BeatName.Contains(term));
        }

        var beats = await query
            .OrderBy(x => x.BeatName)
            .Select(x => new { id = x.Id, name = x.BeatName })
            .ToListAsync(ct);

        return Ok(new { status = "success", beats });
    }

    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        var visibleUserIds = await _hrRepository.GetVisibleUserIdsAsync(CurrentUserId(), ct);
        var users = await _db.Users.AsNoTracking().Where(x => x.Active == "Y" && !x.IsDeleted && visibleUserIds.Contains(x.Id))
            .OrderBy(x => x.Name).Select(x => new { x.Id, name = x.Name, x.Mobile }).ToListAsync(ct);
        var customers = await _db.Customers.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .OrderBy(x => x.Name).Select(x => new { x.Id, name = x.Name, x.Mobile, x.CustomerType }).ToListAsync(ct);
        var cities = await _db.Cities.AsNoTracking().Where(x => x.Active == "Y" && x.DeletedAt == null)
            .OrderBy(x => x.CityName).Select(x => new { x.Id, name = x.CityName, x.DistrictId, x.StateId }).ToListAsync(ct);
        return Ok(new { users, customers, cities });
    }

    [HttpGet("{id:long}")]
    [RequirePermission("beat.detail")]
    public async Task<IActionResult> Get(ulong id, CancellationToken ct)
    {
        var visibleUserIds = (await _hrRepository.GetVisibleUserIdsAsync(CurrentUserId(), ct)).ToHashSet();
        var isDistributor = await IsDistributorUserAsync(ct);
        var beat = await _db.Beats.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (beat is null) return NotFound(new { message = "Beat not found." });
        var userIds = await LinkIds("beat_users", "user_id", id, ct);
        if (isDistributor && !userIds.Any(visibleUserIds.Contains))
            return Forbid();
        if (isDistributor)
            userIds = userIds.Where(visibleUserIds.Contains).ToList();
        return Ok(new { beat, userIds,
            customerIds = await LinkIds("beat_customers", "customer_id", id, ct),
            schedules = await _db.BeatSchedules.AsNoTracking().Where(x => x.BeatId == id
                    && (!isDistributor || (x.UserId.HasValue && visibleUserIds.Contains(x.UserId.Value))))
                .OrderBy(x => x.BeatDate)
                .Select(x => new { x.Id, x.UserId, x.BeatDate, x.Active }).ToListAsync(ct) });
    }

    [HttpPost]
    [RequirePermission("beat.create")]
    public Task<IActionResult> Create([FromBody] BeatRequest request, CancellationToken ct) => Save(null, request, ct);

    [HttpPut("{id:long}")]
    [RequirePermission("beat.edit")]
    public Task<IActionResult> Update(ulong id, [FromBody] BeatRequest request, CancellationToken ct) => Save(id, request, ct);

    [HttpPatch("{id:long}/status")]
    [RequirePermission("beat.edit")]
    public async Task<IActionResult> Status(ulong id, [FromBody] BeatStatusRequest request, CancellationToken ct)
    {
        var beat = await _db.Beats.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (beat is null) return NotFound(new { message = "Beat not found." });
        beat.Active = request.Active?.Equals("N", StringComparison.OrdinalIgnoreCase) == true ? "N" : "Y";
        beat.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Beat status updated successfully.", beat });
    }

    [HttpDelete("{id:long}")]
    [RequirePermission("beat.delete")]
    public async Task<IActionResult> Delete(ulong id, CancellationToken ct)
    {
        var beat = await _db.Beats.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (beat is null) return NotFound(new { message = "Beat not found." });
        await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beat_users WHERE beat_id={id}", ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beat_customers WHERE beat_id={id}", ct);
        await _db.BeatSchedules.Where(x => x.BeatId == id).ExecuteDeleteAsync(ct);
        _db.Beats.Remove(beat);
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Beat deleted successfully." });
    }

    private async Task<IActionResult> Save(ulong? id, BeatRequest request, CancellationToken ct)
    {
        request.BeatName = request.BeatName?.Trim() ?? string.Empty;
        if (request.BeatName.Length < 2 || request.BeatName.Length > 100)
            return BadRequest(new { message = "Beat name must be between 2 and 100 characters." });
        var userIds = request.UserIds.Distinct().ToArray();
        var customerIds = request.CustomerIds.Distinct().ToArray();
        var cityIds = request.CityIds.Distinct().ToArray();
        if (userIds.Length != await _db.Users.CountAsync(x => userIds.Contains(x.Id) && !x.IsDeleted, ct))
            return BadRequest(new { message = "One or more selected users are invalid." });
        if (await IsDistributorUserAsync(ct))
        {
            var visibleUserIds = (await _hrRepository.GetVisibleUserIdsAsync(CurrentUserId(), ct)).ToHashSet();
            if (userIds.Any(x => !visibleUserIds.Contains(x)))
                return Forbid();
        }
        if (customerIds.Length != await _db.Customers.CountAsync(x => customerIds.Contains(x.Id) && x.DeletedAt == null, ct))
            return BadRequest(new { message = "One or more selected customers are invalid." });
        if (cityIds.Length != await _db.Cities.CountAsync(x => cityIds.Contains(x.Id) && x.DeletedAt == null, ct))
            return BadRequest(new { message = "One or more selected cities are invalid." });
        if (request.Schedules.Any(x => !userIds.Contains(x.UserId)))
            return BadRequest(new { message = "Every scheduled user must also be assigned to the beat." });

        Beat beat;
        if (id.HasValue)
        {
            beat = await _db.Beats.FirstOrDefaultAsync(x => x.Id == id.Value, ct) ?? new Beat();
            if (beat.Id == 0) return NotFound(new { message = "Beat not found." });
        }
        else
        {
            beat = new Beat { CreatedAt = DateTime.UtcNow };
            _db.Beats.Add(beat);
        }
        beat.BeatName = request.BeatName;
        beat.Description = request.Description?.Trim() ?? string.Empty;
        beat.CityId = string.Join(',', cityIds);
        beat.Active = request.Active?.Equals("N", StringComparison.OrdinalIgnoreCase) == true ? "N" : "Y";
        beat.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beat_users WHERE beat_id={beat.Id}", ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beat_customers WHERE beat_id={beat.Id}", ct);
        foreach (var userId in userIds)
            await _db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO beat_users(active,beat_id,user_id,created_at,updated_at) VALUES (N'Y',{beat.Id},{userId},SYSUTCDATETIME(),SYSUTCDATETIME())", ct);
        foreach (var customerId in customerIds)
            await _db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO beat_customers(active,beat_id,customer_id,customer_type,created_at,updated_at) VALUES (N'Y',{beat.Id},{customerId},N'unified',SYSUTCDATETIME(),SYSUTCDATETIME())", ct);
        await _db.BeatSchedules.Where(x => x.BeatId == beat.Id).ExecuteDeleteAsync(ct);
        foreach (var schedule in request.Schedules.Where(x => x.BeatDate != default).DistinctBy(x => new { x.UserId, x.BeatDate }))
            _db.BeatSchedules.Add(new BeatSchedule { Active = "Y", BeatId = beat.Id, UserId = schedule.UserId, BeatDate = schedule.BeatDate.Date, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = id.HasValue ? "Beat updated successfully." : "Beat created successfully.", beat });
    }

    /// <summary>
    /// The beat master as a sheet that can be edited and sent straight back.
    ///
    /// Every link is written as the ids the import reads, with the names beside them so a
    /// person can tell what they are looking at. Clear the Beat Id on a row and the import
    /// makes a new beat out of it - two beats may carry the same name, so nothing stops a
    /// row being copied and renamed.
    /// </summary>
    [HttpGet("export")]
    [RequirePermission("beat.export")]
    public async Task<IActionResult> Export([FromQuery] string? search, CancellationToken ct)
    {
        var query = _db.Beats.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => x.BeatName.Contains(search) || x.Description.Contains(search));
        if (await IsDistributorUserAsync(ct))
        {
            var visibleUserIds = (await _hrRepository.GetVisibleUserIdsAsync(CurrentUserId(), ct)).ToHashSet();
            var allowed = (await _db.Database.SqlQueryRaw<BeatUserRow>(
                    "SELECT CAST(beat_id AS bigint) AS BeatId, CAST(user_id AS bigint) AS UserId FROM beat_users WHERE beat_id IS NOT NULL AND user_id IS NOT NULL").ToListAsync(ct))
                .Where(x => visibleUserIds.Contains((ulong)x.UserId)).Select(x => (ulong)x.BeatId).Distinct().ToArray();
            query = query.Where(x => allowed.Contains(x.Id));
        }

        var beats = await query.OrderByDescending(x => x.Id).ToListAsync(ct);
        var beatIds = beats.Select(x => x.Id).ToArray();

        var userLinks = await LinkRowsAsync("beat_users", "user_id", beatIds, ct);
        var customerLinks = await LinkRowsAsync("beat_customers", "customer_id", beatIds, ct);
        var cityNames = await _db.Cities.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.CityName, ct);
        var userNames = await _db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var scheduleCounts = await _db.BeatSchedules.AsNoTracking().Where(x => x.BeatId != null && beatIds.Contains(x.BeatId.Value))
            .GroupBy(x => x.BeatId!.Value).Select(x => new { BeatId = x.Key, Total = x.Count() }).ToListAsync(ct);

        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var sheet = workbook.Worksheets.Add("Beats");
        var headers = new[] { "Beat Id", "Beat Name", "Description", "Active", "City Ids", "City Names",
            "User Ids", "User Names", "Customer Ids", "Customer Count", "Schedules", "Created At" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];

        var row = 2;
        foreach (var beat in beats)
        {
            var cityIds = CsvIds(beat.CityId);
            var users = userLinks.GetValueOrDefault(beat.Id, []);
            var customers = customerLinks.GetValueOrDefault(beat.Id, []);
            sheet.Cell(row, 1).Value = beat.Id;
            sheet.Cell(row, 2).Value = beat.BeatName;
            sheet.Cell(row, 3).Value = beat.Description;
            sheet.Cell(row, 4).Value = beat.Active;
            sheet.Cell(row, 5).Value = string.Join(',', cityIds);
            sheet.Cell(row, 6).Value = string.Join(", ", cityIds.Select(x => cityNames.GetValueOrDefault(x, string.Empty)).Where(x => x.Length > 0));
            sheet.Cell(row, 7).Value = string.Join(',', users);
            sheet.Cell(row, 8).Value = string.Join(", ", users.Select(x => userNames.GetValueOrDefault(x, string.Empty)).Where(x => x.Length > 0));
            // A beat can carry thousands of counters, past what one Excel cell will hold. The
            // cell then says so instead of holding a half list, and the import leaves that
            // beat's counters alone rather than trimming them to what fitted.
            sheet.Cell(row, 9).Value = customers.Count > CustomerIdsInCell
                ? $"({customers.Count} counters - too many to list)"
                : string.Join(',', customers);
            sheet.Cell(row, 10).Value = customers.Count;
            sheet.Cell(row, 11).Value = scheduleCounts.FirstOrDefault(x => x.BeatId == beat.Id)?.Total ?? 0;
            sheet.Cell(row, 12).Value = beat.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
            row++;
        }

        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("D9E1F2");
        header.Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(8, 45);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Beats_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }

    /// <summary>How many counter ids are still worth writing into one cell.</summary>
    private const int CustomerIdsInCell = 400;

    /// <summary>
    /// The same sheet, read back. A row with a Beat Id updates that beat; a row without one
    /// creates a beat. Nothing is deleted.
    ///
    /// A link column left empty leaves that link alone - it does not clear it. That is the
    /// safe reading: a sheet exported from a beat whose counters were too many to list would
    /// otherwise wipe them, and counters are meant to be attachable later.
    /// </summary>
    [HttpPost("import")]
    [RequirePermission("beat.import")]
    public async Task<IActionResult> Import(CancellationToken ct)
    {
        var file = Request.HasFormContentType ? Request.Form.Files.FirstOrDefault() : null;
        if (file is null || file.Length == 0) return BadRequest(new { message = "Please choose a file to import." });

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, ct);
        stream.Position = 0;

        ClosedXML.Excel.XLWorkbook workbook;
        try { workbook = new ClosedXML.Excel.XLWorkbook(stream); }
        catch (Exception) { return BadRequest(new { message = "That file could not be read as an Excel workbook." }); }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet is null) return BadRequest(new { message = "The workbook has no sheet in it." });

            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in sheet.Row(1).CellsUsed())
            {
                var name = cell.GetString().Trim();
                if (name.Length > 0 && !columns.ContainsKey(name)) columns[name] = cell.Address.ColumnNumber;
            }
            if (!columns.ContainsKey("Beat Name"))
                return BadRequest(new { message = "The sheet needs a \"Beat Name\" column. Export the beats first and edit that file." });

            var cityIdsAll = await _db.Cities.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
            var validCities = cityIdsAll.ToHashSet();
            var validUsers = (await _db.Users.AsNoTracking().Where(x => !x.IsDeleted && x.DeletedAt == null).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
            var validCustomers = (await _db.Customers.AsNoTracking().Where(x => x.DeletedAt == null).Select(x => x.Id).ToListAsync(ct)).ToHashSet();

            int created = 0, updated = 0;
            var problems = new List<string>();
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

            for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
            {
                var sheetRow = sheet.Row(rowNumber);
                string Text(string column) => columns.TryGetValue(column, out var index) ? sheetRow.Cell(index).GetString().Trim() : string.Empty;

                var beatName = Text("Beat Name");
                if (beatName.Length == 0) continue;
                if (beatName.Length < 2 || beatName.Length > 100)
                {
                    problems.Add($"Row {rowNumber}: beat name must be between 2 and 100 characters.");
                    continue;
                }

                var cityIds = CsvIds(Text("City Ids"));
                var userIds = CsvIds(Text("User Ids"));
                var customerIds = CsvIds(Text("Customer Ids"));

                var unknownCity = cityIds.FirstOrDefault(x => !validCities.Contains(x));
                if (unknownCity != 0) { problems.Add($"Row {rowNumber}: city id {unknownCity} does not exist."); continue; }
                var unknownUser = userIds.FirstOrDefault(x => !validUsers.Contains(x));
                if (unknownUser != 0) { problems.Add($"Row {rowNumber}: user id {unknownUser} does not exist."); continue; }
                var unknownCustomer = customerIds.FirstOrDefault(x => !validCustomers.Contains(x));
                if (unknownCustomer != 0) { problems.Add($"Row {rowNumber}: customer id {unknownCustomer} does not exist."); continue; }

                Beat? beat = null;
                var beatIdText = Text("Beat Id");
                if (ulong.TryParse(beatIdText, out var beatId) && beatId > 0)
                {
                    beat = await _db.Beats.FirstOrDefaultAsync(x => x.Id == beatId, ct);
                    if (beat is null) { problems.Add($"Row {rowNumber}: beat {beatId} no longer exists."); continue; }
                }

                if (beat is null)
                {
                    beat = new Beat { CreatedAt = DateTime.UtcNow };
                    _db.Beats.Add(beat);
                    created++;
                }
                else updated++;

                beat.BeatName = beatName;
                var description = Text("Description");
                if (description.Length > 0 || columns.ContainsKey("Description")) beat.Description = description;
                var active = Text("Active");
                beat.Active = active.Equals("N", StringComparison.OrdinalIgnoreCase) ? "N" : "Y";
                if (cityIds.Count > 0) beat.CityId = string.Join(',', cityIds);
                beat.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                if (userIds.Count > 0)
                {
                    await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beat_users WHERE beat_id={beat.Id}", ct);
                    foreach (var userId in userIds.Distinct())
                        await _db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO beat_users(active,beat_id,user_id,created_at,updated_at) VALUES (N'Y',{beat.Id},{userId},SYSUTCDATETIME(),SYSUTCDATETIME())", ct);
                }
                if (customerIds.Count > 0)
                {
                    await _db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beat_customers WHERE beat_id={beat.Id}", ct);
                    foreach (var customerId in customerIds.Distinct())
                        await _db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO beat_customers(active,beat_id,customer_id,customer_type,created_at,updated_at) VALUES (N'Y',{beat.Id},{customerId},N'unified',SYSUTCDATETIME(),SYSUTCDATETIME())", ct);
                }
            }

            return Ok(new
            {
                message = $"{created} beat(s) created, {updated} updated." + (problems.Count > 0 ? $" {problems.Count} row(s) skipped." : string.Empty),
                created,
                updated,
                skipped = problems.Count,
                problems = problems.Take(25),
            });
        }
    }

    /// <summary>
    /// Beat Detail: one row per scheduled visit, which is what a beat actually produces -
    /// the beat master says who covers which towns, a schedule says who goes out on which
    /// day. Rows carry the counter count of their beat so the size of the day is visible.
    /// </summary>
    [HttpGet("schedules")]
    [RequirePermission("beat_detail.view")]
    public async Task<IActionResult> Schedules(
        [FromQuery] string? search,
        [FromQuery(Name = "from_date")] DateTime? fromDate,
        [FromQuery(Name = "to_date")] DateTime? toDate,
        [FromQuery] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 10,
        CancellationToken ct = default)
    {
        var (rows, total) = await ScheduleRowsAsync(search, fromDate, toDate, page, pageSize, ct);
        return Ok(new { schedules = rows, total, page, page_size = pageSize });
    }

    [HttpGet("schedules/export")]
    [RequirePermission("beat_detail.export")]
    public async Task<IActionResult> SchedulesExport(
        [FromQuery] string? search,
        [FromQuery(Name = "from_date")] DateTime? fromDate,
        [FromQuery(Name = "to_date")] DateTime? toDate,
        CancellationToken ct = default)
    {
        var (rows, _) = await ScheduleRowsAsync(search, fromDate, toDate, 1, int.MaxValue, ct);

        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var sheet = workbook.Worksheets.Add("Beat Detail");
        var headers = new[] { "Beat Name", "Beat Date", "Customers", "User Name", "Mobile", "Created At" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];

        var row = 2;
        foreach (var item in rows)
        {
            sheet.Cell(row, 1).Value = item.beat_name;
            sheet.Cell(row, 2).Value = item.beat_date;
            sheet.Cell(row, 3).Value = item.customer_count;
            sheet.Cell(row, 4).Value = item.user_name;
            sheet.Cell(row, 5).Value = item.mobile;
            sheet.Cell(row, 6).Value = item.created_at;
            row++;
        }

        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("D9E1F2");
        header.Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(8, 45);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Beat_Detail_{DateTime.Now:yyyy-MM-dd_HHmmss}.xlsx");
    }

    private async Task<(List<ScheduleRow> Rows, long Total)> ScheduleRowsAsync(
        string? search, DateTime? fromDate, DateTime? toDate, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.BeatSchedules.AsNoTracking().Where(x => x.BeatId != null);
        if (await IsDistributorUserAsync(ct))
        {
            var visibleUserIds = (await _hrRepository.GetVisibleUserIdsAsync(CurrentUserId(), ct)).ToArray();
            query = query.Where(x => x.UserId != null && visibleUserIds.Contains(x.UserId.Value));
        }
        if (fromDate.HasValue) query = query.Where(x => x.BeatDate >= fromDate.Value.Date);
        if (toDate.HasValue) query = query.Where(x => x.BeatDate <= toDate.Value.Date);

        var beatNames = await _db.Beats.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.BeatName, ct);
        var users = await _db.Users.AsNoTracking().Select(x => new { x.Id, x.Name, x.Mobile }).ToListAsync(ct);
        var userById = users.ToDictionary(x => x.Id);
        var customerCounts = (await _db.Database.SqlQueryRaw<BeatCountRow>(
                "SELECT CAST(beat_id AS bigint) AS BeatId, COUNT(*) AS Total FROM beat_customers WHERE beat_id IS NOT NULL AND customer_id IS NOT NULL GROUP BY beat_id").ToListAsync(ct))
            .ToDictionary(x => (ulong)x.BeatId, x => x.Total);

        var all = (await query.OrderByDescending(x => x.BeatDate).ThenByDescending(x => x.Id).ToListAsync(ct))
            .Select(x => new ScheduleRow(
                x.Id,
                x.BeatId!.Value,
                beatNames.GetValueOrDefault(x.BeatId!.Value, string.Empty),
                x.BeatDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                customerCounts.GetValueOrDefault(x.BeatId!.Value, 0),
                x.UserId.HasValue ? userById.GetValueOrDefault(x.UserId.Value)?.Name ?? string.Empty : string.Empty,
                x.UserId.HasValue ? userById.GetValueOrDefault(x.UserId.Value)?.Mobile ?? string.Empty : string.Empty,
                x.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty))
            .ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            all = all.Where(x => x.beat_name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.user_name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.mobile.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var total = all.Count;
        if (pageSize == int.MaxValue) return (all, total);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        return (all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), total);
    }

    private sealed record ScheduleRow(ulong id, ulong beat_id, string beat_name, string beat_date,
        int customer_count, string user_name, string mobile, string created_at);

    private async Task<Dictionary<ulong, List<ulong>>> LinkRowsAsync(string table, string column, ulong[] beatIds, CancellationToken ct)
    {
        if (beatIds.Length == 0) return [];
        var sql = $"SELECT CAST(beat_id AS bigint) AS BeatId, CAST({column} AS bigint) AS UserId FROM {table} WHERE beat_id IS NOT NULL AND {column} IS NOT NULL";
        var rows = await _db.Database.SqlQueryRaw<BeatUserRow>(sql).ToListAsync(ct);
        return rows.Where(x => beatIds.Contains((ulong)x.BeatId))
            .GroupBy(x => (ulong)x.BeatId)
            .ToDictionary(x => x.Key, x => x.Select(y => (ulong)y.UserId).Distinct().ToList());
    }

    /// <summary>Ids out of a comma separated cell. Anything that is not a number is ignored,
    /// which is how the "too many to list" marker passes through without doing harm.</summary>
    private static List<ulong> CsvIds(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => ulong.TryParse(x, out var id) ? id : 0).Where(x => x > 0).Distinct().ToList();

    private async Task<List<ulong>> LinkIds(string table, string column, ulong beatId, CancellationToken ct)
    {
        var sql = $"SELECT CAST({column} AS bigint) AS Value FROM {table} WHERE beat_id={{0}} AND {column} IS NOT NULL";
        var values = await _db.Database.SqlQueryRaw<long>(sql, beatId).ToListAsync(ct);
        return values.Where(x => x > 0).Select(x => (ulong)x).ToList();
    }

    private ulong? CurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return ulong.TryParse(raw, out var id) ? id : null;
    }

    private async Task<bool> IsDistributorUserAsync(CancellationToken ct)
    {
        var userId = CurrentUserId();
        return userId.HasValue && await _db.ModelHasRoles.AsNoTracking()
            .Where(x => x.ModelId == userId.Value)
            .Join(_db.Roles.AsNoTracking(), x => x.RoleId, role => role.Id, (_, role) => role.Name)
            .AnyAsync(name => name == "Distributor", ct);
    }

    public sealed class BeatRequest
    {
        public string? BeatName { get; set; }
        public string? Description { get; set; }
        public string? Active { get; set; } = "Y";
        public List<ulong> CityIds { get; set; } = [];
        public List<ulong> UserIds { get; set; } = [];
        public List<ulong> CustomerIds { get; set; } = [];
        public List<BeatScheduleRequest> Schedules { get; set; } = [];
    }
    public sealed class BeatScheduleRequest { public ulong UserId { get; set; } public DateTime BeatDate { get; set; } }
    public sealed class BeatStatusRequest { public string? Active { get; set; } }
    public sealed class BeatCountRow { public long BeatId { get; set; } public int Total { get; set; } }
    public sealed class BeatUserRow { public long BeatId { get; set; } public long UserId { get; set; } }
}
