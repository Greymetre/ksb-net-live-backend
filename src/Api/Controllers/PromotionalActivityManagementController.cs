using System.Security.Claims;
using Api.Filters;
using ClosedXML.Excel;
using Application.Interfaces.Repositories;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// User Management > Promotional Activities: the Retailer, Nukkad, Farmer and Influencer meets
/// recorded from the field app, drafts and submitted alike. Listing, view, full edit, soft
/// delete and export. Every action is limited to the rows the user may see - admin roles all,
/// a BM its branch, everyone else their reporting downline to the end level - matched on the
/// person who ran the activity or the person who recorded it, as the field app does.
/// </summary>
[ApiController]
[Authorize]
[Route("api/promotional-activities")]
public sealed class PromotionalActivityManagementController : ControllerBase
{
    private static readonly string[] Types = ["retailer", "nukkad", "farmer", "influencer"];

    private readonly AppDbContext _db;
    private readonly IHrRepository _hr;

    public PromotionalActivityManagementController(AppDbContext db, IHrRepository hr)
    {
        _db = db;
        _hr = hr;
    }

    // Dropdown values only - the rows themselves are permission gated below.
    [HttpGet("options")]
    public async Task<IActionResult> Options(CancellationToken ct)
    {
        var visible = await VisibleUserIds(ct);
        var visibleLong = visible.Select(x => (long)x).ToArray();
        var activityUsers = _db.PromotionalActivities.AsNoTracking()
            .Where(x => x.DeletedAt == null && (visibleLong.Contains(x.UserId) || visibleLong.Contains(x.CreatedById)));
        var userIds = await activityUsers.Select(x => x.UserId).Union(activityUsers.Select(x => x.CreatedById)).ToListAsync(ct);
        var ids = userIds.Select(x => (ulong)x).Where(visible.Contains).ToList();
        var users = await _db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Name)
            .Select(x => new { id = x.Id, name = x.Name, employee_code = x.EmployeeCodes }).ToListAsync(ct);
        var branchIds = await activityUsers.Where(x => x.BranchId != null).Select(x => x.BranchId!.Value).Distinct().ToListAsync(ct);
        var branches = await _db.Branches.AsNoTracking().Where(x => branchIds.Contains((long)x.Id)).OrderBy(x => x.BranchName)
            .Select(x => new { id = x.Id, name = x.BranchName }).ToListAsync(ct);
        var zones = (await _db.Divisions.AsNoTracking().Select(x => new { id = x.Id, name = x.DivisionName }).ToListAsync(ct))
            .OrderBy(x => Domain.Services.ZoneOrder.Rank(x.name)).ThenBy(x => x.name).ToList();
        return Ok(new
        {
            status = "success",
            data = new
            {
                users,
                branches,
                zones,
                types = Types.Select(x => new { id = x, name = TypeLabel(x) }),
                statuses = new[] { new { id = "submitted", name = "Submitted" }, new { id = "draft", name = "Draft" } }
            }
        });
    }

    [HttpGet]
    [RequirePermission("promotional_activity.view")]
    public async Task<IActionResult> List([FromQuery] ActivityListFilter filter, CancellationToken ct)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var query = await FilteredRows(filter, applyType: false, ct);

        // The type tiles stay put while a type is picked, so they count before the type filter.
        var typeCounts = await query.GroupBy(x => x.Type).Select(g => new { type = g.Key, count = g.Count() }).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(filter.ActivityType)) query = query.Where(x => x.Type == filter.ActivityType.Trim().ToLower());

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.ActivityDate).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new
        {
            status = "success",
            data = rows.Select(ToListRow),
            total,
            page,
            page_size = pageSize,
            counts = new
            {
                all = typeCounts.Sum(x => x.count),
                retailer = typeCounts.FirstOrDefault(x => x.type == "retailer")?.count ?? 0,
                nukkad = typeCounts.FirstOrDefault(x => x.type == "nukkad")?.count ?? 0,
                farmer = typeCounts.FirstOrDefault(x => x.type == "farmer")?.count ?? 0,
                influencer = typeCounts.FirstOrDefault(x => x.type == "influencer")?.count ?? 0
            }
        });
    }

    [HttpGet("export")]
    [RequirePermission("promotional_activity.export")]
    public async Task<IActionResult> Export([FromQuery] ActivityListFilter filter, CancellationToken ct)
    {
        var query = await FilteredRows(filter, applyType: true, ct);
        var rows = await query.OrderByDescending(x => x.ActivityDate).ThenByDescending(x => x.Id).ToListAsync(ct);
        var ids = rows.Select(x => x.Id).ToArray();
        var gifts = await _db.PromotionalActivityParticipants.AsNoTracking()
            .Where(x => ids.Contains(x.ActivityId) && x.GiftName != null && x.GiftName != "")
            .Select(x => new { x.ActivityId, x.GiftName }).ToListAsync(ct);
        var expenses = await _db.PromotionalActivityExpenses.AsNoTracking()
            .Where(x => ids.Contains(x.ActivityId))
            .Select(x => new { x.ActivityId, x.ExpenseType, x.TotalAmount }).ToListAsync(ct);
        decimal Expense(long id, string type) => expenses.Where(x => x.ActivityId == id && x.ExpenseType == type).Sum(x => x.TotalAmount);

        string[] headings =
        [
            "Sr. No", "Activity Id", "Activity Type", "Activity Name", "Activity Date", "Status", "Zone", "Branch",
            "ASR / DSR Name", "ASR / DSR Code", "Recorded By", "Reporting Manager", "Distributor", "Sub Dealer / Retailer",
            "Hotel Name", "Activity Location", "Participants", "Gift Count", "Gifts Given", "Food Expense", "Gift Expense",
            "Hotel Expense", "AV Expense", "Other Expense", "Total Expense", "Dealer Share", "Photos", "Feedback", "Created At"
        ];
        var serial = 0;
        var data = rows.Select(x => new object?[]
        {
            ++serial, x.ActivityCode, TypeLabel(x.Type), x.ActivityName, x.ActivityDate.ToString("dd-MM-yyyy"), StatusLabel(x.Status),
            x.ZoneName, x.BranchName, x.UserName, x.UserCode, x.CreatorName, x.ManagerName, x.DistributorName, x.DealerName,
            x.HotelName, x.LocationText, x.Participants, x.GiftCount,
            string.Join(", ", gifts.Where(g => g.ActivityId == x.Id).GroupBy(g => g.GiftName!.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => $"{g.Key} ({g.Count()})")),
            Expense(x.Id, "food"), Expense(x.Id, "gift"), Expense(x.Id, "hotel"), Expense(x.Id, "av"),
            Expense(x.Id, "other") + Expense(x.Id, "other1") + Expense(x.Id, "other2"),
            x.TotalExpense, x.DealerShareAmount, x.Photos, x.Feedback, x.CreatedAt?.ToString("dd-MM-yyyy HH:mm")
        });
        // Written here rather than through ExportWorkbook, which title-cases headings ("Asr / Dsr").
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Promotional Activities");
        sheet.Style.Font.FontName = "Calibri";
        sheet.Style.Font.FontSize = 9;
        for (var column = 0; column < headings.Length; column++)
        {
            sheet.Cell(1, column + 1).Value = headings[column];
            sheet.Cell(1, column + 1).Style.Font.Bold = true;
        }
        var rowNumber = 2;
        foreach (var row in data)
        {
            for (var column = 0; column < row.Length; column++) sheet.Cell(rowNumber, column + 1).Value = XLCellValue.FromObject(row[column]);
            rowNumber++;
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"promotional-activities-{DateTime.UtcNow:yyyyMMddHHmm}.xlsx");
    }

    [HttpGet("{id:long}")]
    [RequirePermission("promotional_activity.detail", "promotional_activity.edit")]
    public async Task<IActionResult> Show(long id, CancellationToken ct)
    {
        var row = await (await FilteredRows(new ActivityListFilter(), applyType: false, ct)).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return NotFoundResult();
        var entity = await _db.PromotionalActivities.AsNoTracking()
            .Include(x => x.Participants).Include(x => x.Expenses).Include(x => x.Photos)
            .FirstAsync(x => x.Id == id, ct);
        return Ok(new
        {
            status = "success",
            data = new
            {
                activity = ToListRow(row),
                user_id = entity.UserId,
                distributor_id = entity.DistributorId,
                expense_types = ExpenseTypes(entity.ActivityType),
                photo_limit = entity.ActivityType == "retailer" ? 10 : 5,
                participants = entity.Participants.OrderBy(x => x.Id).Select(x => new
                {
                    x.Id, x.Name, x.ShopName, x.ProprietorName, x.ParticipantType, x.Profession, x.Mobile,
                    x.GiftName, x.Remarks, x.IsInfluencer, x.SocialType, x.SocialLink
                }),
                expenses = entity.Expenses.OrderBy(x => Array.IndexOf(ExpenseTypes(entity.ActivityType), x.ExpenseType)).Select(x => new
                {
                    x.Id, x.ExpenseType, x.TotalAmount, x.DealerShareAmount, x.DealerSharePct, x.Remarks, x.InvoiceUrl
                }),
                photos = entity.Photos.OrderBy(x => x.Id).Select(x => new { x.Id, x.PhotoUrl, x.Latitude, x.Longitude, x.TakenAt })
            }
        });
    }

    /// <summary>
    /// Full edit: details, participants, expense lines, and which photos stay. The activity's
    /// type, code, status and who recorded it do not change. A submitted activity must still
    /// pass the field app's submit rules afterwards, so an edit cannot leave it incomplete.
    /// Replaced participants, expenses and removed photos are soft deleted.
    /// </summary>
    [HttpPut("{id:long}")]
    [RequirePermission("promotional_activity.edit")]
    public async Task<IActionResult> Update(long id, [FromBody] ActivityEditRequest request, CancellationToken ct)
    {
        if (!await (await FilteredRows(new ActivityListFilter(), applyType: false, ct)).AnyAsync(x => x.Id == id, ct)) return NotFoundResult();
        var entity = await _db.PromotionalActivities
            .Include(x => x.Participants).Include(x => x.Expenses).Include(x => x.Photos)
            .FirstAsync(x => x.Id == id, ct);

        if (request.ActivityDate == default) return Invalid("Activity Date is required.");
        if (request.ActivityDate.Date > DateTime.UtcNow.Date.AddDays(1)) return Invalid("Activity Date cannot be a future date.");
        if (string.IsNullOrWhiteSpace(request.ActivityName)) return Invalid("Activity Name is required.");
        if (request.UserId is { } userId)
        {
            var visible = await VisibleUserIds(ct);
            if (!visible.Contains(userId)) return Invalid("Select a valid ASR / DSR.");
        }
        var allowedTypes = ExpenseTypes(entity.ActivityType);
        if ((request.Expenses ?? []).Any(x => !allowedTypes.Contains(x.ExpenseType))) return Invalid("An expense type does not belong to this activity.");
        if ((request.Expenses ?? []).Any(x => x.TotalAmount < 0 || x.DealerShareAmount < 0)) return Invalid("Expense amounts cannot be negative.");
        if ((request.Expenses ?? []).Any(x => x.DealerShareAmount > x.TotalAmount)) return Invalid("Dealer share cannot be more than the expense amount.");

        var now = DateTime.UtcNow;
        entity.ActivityName = request.ActivityName.Trim();
        entity.ActivityDate = request.ActivityDate.Date;
        if (request.UserId is { } newUser) entity.UserId = checked((long)newUser);
        entity.DistributorId = request.DistributorId.HasValue ? checked((long)request.DistributorId.Value) : entity.DistributorId;
        entity.DistributorName = Clean(request.DistributorName) ?? entity.DistributorName;
        entity.DealerName = Clean(request.DealerName);
        entity.HotelName = Clean(request.HotelName);
        entity.LocationText = Clean(request.LocationText);
        entity.GiftCount = Math.Max(0, request.GiftCount);
        entity.Feedback = Clean(request.Feedback);

        foreach (var old in entity.Participants) old.DeletedAt = now;
        foreach (var old in entity.Expenses) old.DeletedAt = now;
        var keepPhotos = (request.KeepPhotoIds ?? []).ToHashSet();
        foreach (var photo in entity.Photos.Where(x => !keepPhotos.Contains(x.Id))) photo.DeletedAt = now;

        var participants = (request.Participants ?? []).Select(x => new PromotionalActivityParticipant
        {
            ActivityId = entity.Id, Name = Clean(x.Name), ShopName = Clean(x.ShopName), ProprietorName = Clean(x.ProprietorName),
            ParticipantType = Clean(x.ParticipantType), Profession = Clean(x.Profession), Mobile = Clean(x.Mobile),
            GiftName = Clean(x.GiftName), Remarks = Clean(x.Remarks), IsInfluencer = x.IsInfluencer,
            SocialType = x.IsInfluencer ? Clean(x.SocialType) : null, SocialLink = x.IsInfluencer ? Clean(x.SocialLink) : null, CreatedAt = now
        }).ToList();
        var expenses = (request.Expenses ?? []).Select(x => new PromotionalActivityExpense
        {
            ActivityId = entity.Id, ExpenseType = x.ExpenseType, TotalAmount = x.TotalAmount, DealerShareAmount = x.DealerShareAmount,
            DealerSharePct = x.TotalAmount <= 0 ? 0 : Math.Round(x.DealerShareAmount / x.TotalAmount * 100, 2),
            Remarks = Clean(x.Remarks), InvoiceUrl = Clean(x.InvoiceUrl), CreatedAt = now
        }).ToList();

        // The submit rules read the activity as it will be after this edit.
        var check = new PromotionalActivity
        {
            ActivityType = entity.ActivityType, ActivityName = entity.ActivityName, ActivityDate = entity.ActivityDate, UserId = entity.UserId,
            BranchId = entity.BranchId, Zone = entity.Zone, ReportingManagerId = entity.ReportingManagerId, DistributorId = entity.DistributorId,
            HotelName = entity.HotelName, LocationText = entity.LocationText, Participants = participants, Expenses = expenses,
            Photos = entity.Photos.Where(x => keepPhotos.Contains(x.Id)).ToList()
        };
        if (entity.Status == "submitted" && PromotionalActivitiesController.ValidateSubmit(check) is { } error) return Invalid(error);

        _db.PromotionalActivityParticipants.AddRange(participants);
        _db.PromotionalActivityExpenses.AddRange(expenses);
        entity.TotalExpense = expenses.Sum(x => x.TotalAmount);
        entity.DealerShareAmount = expenses.Sum(x => x.DealerShareAmount);
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return Ok(new { status = "success", message = "Promotional activity updated successfully." });
    }

    [HttpDelete("{id:long}")]
    [RequirePermission("promotional_activity.delete")]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        if (!await (await FilteredRows(new ActivityListFilter(), applyType: false, ct)).AnyAsync(x => x.Id == id, ct)) return NotFoundResult();
        var entity = await _db.PromotionalActivities.FirstAsync(x => x.Id == id, ct);
        entity.DeletedAt = DateTime.UtcNow;
        entity.UpdatedAt = entity.DeletedAt;
        await _db.SaveChangesAsync(ct);
        return Ok(new { status = "success", message = "Promotional activity deleted successfully." });
    }

    private async Task<IQueryable<ActivityRow>> FilteredRows(ActivityListFilter filter, bool applyType, CancellationToken ct)
    {
        var visible = (await VisibleUserIds(ct)).Select(x => (long)x).ToArray();
        var activities = _db.PromotionalActivities.AsNoTracking()
            .Where(x => x.DeletedAt == null && (visible.Contains(x.UserId) || visible.Contains(x.CreatedById)));

        var query =
            from a in activities
            join u in _db.Users.AsNoTracking().IgnoreQueryFilters() on (ulong)a.UserId equals u.Id into us
            from u in us.DefaultIfEmpty()
            join c in _db.Users.AsNoTracking().IgnoreQueryFilters() on (ulong)a.CreatedById equals c.Id into cs
            from c in cs.DefaultIfEmpty()
            join m in _db.Users.AsNoTracking().IgnoreQueryFilters() on (ulong?)a.ReportingManagerId equals (ulong?)m.Id into ms
            from m in ms.DefaultIfEmpty()
            join b in _db.Branches.AsNoTracking() on (ulong?)a.BranchId equals (ulong?)b.Id into bs
            from b in bs.DefaultIfEmpty()
            join d in _db.Divisions.AsNoTracking() on (u == null ? null : u.DivisionId) equals (ulong?)d.Id into ds
            from d in ds.DefaultIfEmpty()
            select new ActivityRow
            {
                Id = a.Id, ActivityCode = a.ActivityCode, Type = a.ActivityType, ActivityName = a.ActivityName, ActivityDate = a.ActivityDate,
                Status = a.Status, UserId = a.UserId, UserName = u == null ? null : u.Name, UserCode = u == null ? null : u.EmployeeCodes,
                CreatorName = c == null ? null : c.Name, ManagerName = m == null ? null : m.Name,
                BranchId = a.BranchId, BranchName = b == null ? null : b.BranchName,
                ZoneId = d == null ? null : (ulong?)d.Id, ZoneName = d == null ? a.Zone : d.DivisionName,
                DistributorName = a.DistributorName, DealerName = a.DealerName, HotelName = a.HotelName, LocationText = a.LocationText,
                Participants = a.Participants.Count(p => p.DeletedAt == null), Photos = a.Photos.Count(p => p.DeletedAt == null), GiftCount = a.GiftCount, TotalExpense = a.TotalExpense,
                DealerShareAmount = a.DealerShareAmount, Feedback = a.Feedback, CreatedAt = a.CreatedAt, UpdatedAt = a.UpdatedAt
            };

        if (applyType && !string.IsNullOrWhiteSpace(filter.ActivityType)) query = query.Where(x => x.Type == filter.ActivityType.Trim().ToLower());
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(x => x.Status == filter.Status.Trim().ToLower());
        if (filter.UserId is { } userId) { var id = checked((long)userId); query = query.Where(x => x.UserId == id); }
        if (filter.BranchId is { } branchId) { var id = checked((long)branchId); query = query.Where(x => x.BranchId == id); }
        if (filter.ZoneId is { } zoneId) query = query.Where(x => x.ZoneId == zoneId);
        if (filter.StartDate is { } start) query = query.Where(x => x.ActivityDate >= start.Date);
        if (filter.EndDate is { } end) query = query.Where(x => x.ActivityDate < end.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(x => (x.ActivityCode != null && x.ActivityCode.Contains(term)) || x.ActivityName.Contains(term)
                || (x.UserName != null && x.UserName.Contains(term)) || (x.DistributorName != null && x.DistributorName.Contains(term))
                || (x.LocationText != null && x.LocationText.Contains(term)) || (x.HotelName != null && x.HotelName.Contains(term)));
        }
        return query;
    }

    private static object ToListRow(ActivityRow x) => new
    {
        x.Id, x.ActivityCode, activity_type = x.Type, type_label = TypeLabel(x.Type), x.ActivityName, activity_date = x.ActivityDate.ToString("yyyy-MM-dd"),
        x.Status, status_label = StatusLabel(x.Status), x.UserName, x.UserCode, created_by_name = x.CreatorName, reporting_manager_name = x.ManagerName,
        branch_name = x.BranchName, zone_name = x.ZoneName, x.DistributorName, x.DealerName, x.HotelName, x.LocationText,
        participant_count = x.Participants, photo_count = x.Photos, x.GiftCount, x.TotalExpense, x.DealerShareAmount, x.Feedback, x.CreatedAt, x.UpdatedAt
    };

    private async Task<HashSet<ulong>> VisibleUserIds(CancellationToken ct)
    {
        var current = ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
        return (await _hr.GetVisibleUserIdsAsync(current, ct)).ToHashSet();
    }

    private static string[] ExpenseTypes(string type) => type switch
    {
        "nukkad" => ["food", "gift"],
        "farmer" => ["food", "gift", "other1", "other2"],
        _ => ["food", "gift", "hotel", "av", "other"]
    };

    private static string TypeLabel(string type) => type switch
    {
        "retailer" => "Retailer Meet",
        "nukkad" => "Nukkad Meet",
        "farmer" => "Farmer Meet / Field Demo",
        "influencer" => "Influencer Meet",
        _ => type
    };

    private static string StatusLabel(string status) => status == "submitted" ? "Submitted" : "Draft";
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private IActionResult Invalid(string message) => UnprocessableEntity(new { status = "error", message });
    private IActionResult NotFoundResult() => NotFound(new { status = "error", message = "Promotional activity not found." });

    private sealed class ActivityRow
    {
        public long Id { get; init; }
        public string? ActivityCode { get; init; }
        public string Type { get; init; } = "";
        public string ActivityName { get; init; } = "";
        public DateTime ActivityDate { get; init; }
        public string Status { get; init; } = "";
        public long UserId { get; init; }
        public string? UserName { get; init; }
        public string? UserCode { get; init; }
        public string? CreatorName { get; init; }
        public string? ManagerName { get; init; }
        public long? BranchId { get; init; }
        public string? BranchName { get; init; }
        public ulong? ZoneId { get; init; }
        public string? ZoneName { get; init; }
        public string? DistributorName { get; init; }
        public string? DealerName { get; init; }
        public string? HotelName { get; init; }
        public string? LocationText { get; init; }
        public int Participants { get; init; }
        public int Photos { get; init; }
        public int GiftCount { get; init; }
        public decimal TotalExpense { get; init; }
        public decimal DealerShareAmount { get; init; }
        public string? Feedback { get; init; }
        public DateTime? CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
    }
}

public sealed class ActivityListFilter
{
    [FromQuery(Name = "search")] public string? Search { get; set; }
    [FromQuery(Name = "activity_type")] public string? ActivityType { get; set; }
    [FromQuery(Name = "status")] public string? Status { get; set; }
    [FromQuery(Name = "user_id")] public ulong? UserId { get; set; }
    [FromQuery(Name = "branch_id")] public ulong? BranchId { get; set; }
    [FromQuery(Name = "zone_id")] public ulong? ZoneId { get; set; }
    [FromQuery(Name = "start_date")] public DateTime? StartDate { get; set; }
    [FromQuery(Name = "end_date")] public DateTime? EndDate { get; set; }
    [FromQuery(Name = "page")] public int Page { get; set; } = 1;
    [FromQuery(Name = "page_size")] public int PageSize { get; set; } = 10;
}

public sealed record ActivityEditRequest(
    string ActivityName, DateTime ActivityDate, ulong? UserId, ulong? DistributorId, string? DistributorName, string? DealerName,
    string? HotelName, string? LocationText, int GiftCount, string? Feedback,
    List<ActivityParticipantRequest>? Participants, List<ActivityExpenseRequest>? Expenses, List<long>? KeepPhotoIds);
