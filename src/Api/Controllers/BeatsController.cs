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
    [RequirePermission("beat_access")]
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

    [HttpGet("options")]
    [RequirePermission("beat_access", "beat_create", "beat_edit")]
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
    [RequirePermission("beat_show")]
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
    [RequirePermission("beat_create")]
    public Task<IActionResult> Create([FromBody] BeatRequest request, CancellationToken ct) => Save(null, request, ct);

    [HttpPut("{id:long}")]
    [RequirePermission("beat_edit")]
    public Task<IActionResult> Update(ulong id, [FromBody] BeatRequest request, CancellationToken ct) => Save(id, request, ct);

    [HttpPatch("{id:long}/status")]
    [RequirePermission("beat_edit")]
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
    [RequirePermission("beat_delete")]
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
