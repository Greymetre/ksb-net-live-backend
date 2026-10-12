using System.Globalization;
using System.Security.Claims;
using Api.Filters;
using ClosedXML.Excel;
using Application.DTOs.DealerFeedback;
using Application.Interfaces.Repositories;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api/dealer-feedback")]
public sealed class DealerFeedbackController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IDealerFeedbackRepository _feedback;

    public DealerFeedbackController(AppDbContext db, IDealerFeedbackRepository feedback)
    {
        _db = db;
        _feedback = feedback;
    }

    // ---- Dealer portal (the logged-in Distributor user) ----

    /// <summary>A dealer leaves a note. The customer is resolved from the logged-in user, so
    /// a non-dealer user (no linked customer) cannot post - no CRM permission is involved.</summary>
    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SubmitDealerFeedbackRequest request, CancellationToken cancellationToken)
    {
        var customerId = await CurrentDealerCustomerIdAsync(cancellationToken);
        if (customerId is null)
            return StatusCode(403, new { status = false, message = "Only a dealer account can submit feedback." });

        var category = request?.Category?.Trim() ?? string.Empty;
        var message = request?.Message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(category))
            return UnprocessableEntity(new { status = false, message = "Please choose a category." });
        if (string.IsNullOrWhiteSpace(message))
            return UnprocessableEntity(new { status = false, message = "Please write your feedback." });

        var rating = request?.Rating is >= 1 and <= 5 ? request!.Rating : 3;
        var row = await _feedback.AddAsync(customerId.Value, CurrentUserId(), category, message, rating, cancellationToken);
        return Ok(new { status = true, message = "Thank you - your feedback has been sent.", data = row });
    }

    /// <summary>The dealer's own notes.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken)
    {
        var customerId = await CurrentDealerCustomerIdAsync(cancellationToken);
        if (customerId is null)
            return StatusCode(403, new { status = false, message = "Only a dealer account can view this." });

        var rows = await _feedback.ListForDealerAsync(customerId.Value, cancellationToken);
        return Ok(new { data = rows });
    }

    // ---- Internal listing (reporting-wise, read only) ----

    [HttpGet]
    [RequirePermission("dealer_feedback.view")]
    public async Task<IActionResult> List(
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery(Name = "from_date")] string? fromDate,
        [FromQuery(Name = "to_date")] string? toDate,
        [FromQuery] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var filter = BuildFilter(category, search, fromDate, toDate, page, pageSize, all: false);
        var (rows, total) = await _feedback.ListAsync(filter, cancellationToken);
        var lastPage = Math.Max(1, (long)Math.Ceiling(total / (double)filter.PageSize));
        return Ok(new
        {
            data = rows,
            pagination = new { current_page = page, last_page = lastPage, page_size = filter.PageSize, total }
        });
    }

    [HttpGet("export")]
    [RequirePermission("dealer_feedback.export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery(Name = "from_date")] string? fromDate,
        [FromQuery(Name = "to_date")] string? toDate,
        CancellationToken cancellationToken = default)
    {
        var filter = BuildFilter(category, search, fromDate, toDate, 1, 10, all: true);
        var (rows, _) = await _feedback.ListAsync(filter, cancellationToken);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Dealer Feedback");
        var headers = new[] { "Dealer", "Mobile", "Category", "Rating", "Feedback", "Date" };
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var values = new object?[] { r.DealerName, r.DealerMobile, r.Category, r.Rating, r.Message, r.CreatedAt?.ToString("yyyy-MM-dd HH:mm") };
            for (var j = 0; j < values.Length; j++) sheet.Cell(i + 2, j + 1).Value = XLCellValue.FromObject(values[j]);
        }
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1f4e78");
        sheet.Row(1).Style.Font.FontColor = XLColor.White;
        if (rows.Count > 0) sheet.RangeUsed()!.SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(8, 60);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "dealer-feedback.xlsx");
    }

    // ---- helpers ----

    private DealerFeedbackListFilter BuildFilter(string? category, string? search, string? fromDate, string? toDate, int page, int pageSize, bool all)
    {
        return new DealerFeedbackListFilter
        {
            ActorUserId = CurrentUserId(),
            Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            FromDate = ParseDate(fromDate),
            ToDate = ParseEndOfDay(toDate),
            Page = page,
            PageSize = pageSize,
            All = all
        };
    }

    private async Task<ulong?> CurrentDealerCustomerIdAsync(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId is null) return null;
        var customerId = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.CustomerId)
            .FirstOrDefaultAsync(cancellationToken);
        return customerId is null or 0 ? null : customerId;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.Date
            : null;

    private static DateTime? ParseEndOfDay(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.Date.AddDays(1).AddTicks(-1)
            : null;

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }
}
