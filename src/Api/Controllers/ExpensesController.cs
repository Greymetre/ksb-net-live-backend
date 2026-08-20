using Api.Extensions;
using Api.Filters;
using Application.DTOs.Expenses;
using Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api/expenses")]
public sealed class ExpensesController : ControllerBase
{
    private readonly IExpenseService _service;
    private readonly IWebHostEnvironment _environment;

    public ExpensesController(IExpenseService service, IWebHostEnvironment environment)
    {
        _service = service;
        _environment = environment;
    }

    [HttpGet]
    [RequirePermission("expense_access")]
    public async Task<IActionResult> GetExpenses([FromQuery(Name = "executive_id")] ulong? executiveId, [FromQuery(Name = "expenses_type")] ulong? expensesType, [FromQuery(Name = "branch_id")] ulong? branchId, [FromQuery(Name = "division_id")] ulong? divisionId, [FromQuery] string? payroll, [FromQuery] int? status, [FromQuery(Name = "start_date")] string? startDate, [FromQuery(Name = "end_date")] string? endDate, [FromQuery(Name = "expense_id")] ulong? expenseId, [FromQuery] string? search, CancellationToken cancellationToken)
    {
        var response = await _service.GetExpensesAsync(new ExpenseFilterDto { ExecutiveId = executiveId, ExpensesType = expensesType, BranchId = branchId, DivisionId = divisionId, Payroll = payroll, Status = status, StartDate = startDate, EndDate = endDate, ExpenseId = expenseId, Search = search, ActorUserId = CurrentUserId() }, cancellationToken);
        NormalizeAttachmentUrls(response);
        return Ok(response);
    }

    /// <summary>Status counts and amounts for the same scope and filters the list uses.</summary>
    [HttpGet("summary")]
    [RequirePermission("expense_access")]
    public async Task<IActionResult> GetSummary([FromQuery(Name = "executive_id")] ulong? executiveId, [FromQuery(Name = "expenses_type")] ulong? expensesType, [FromQuery(Name = "branch_id")] ulong? branchId, [FromQuery(Name = "division_id")] ulong? divisionId, [FromQuery] string? payroll, [FromQuery] int? status, [FromQuery(Name = "start_date")] string? startDate, [FromQuery(Name = "end_date")] string? endDate, CancellationToken cancellationToken) =>
        Ok(await _service.GetSummaryAsync(new ExpenseFilterDto { ExecutiveId = executiveId, ExpensesType = expensesType, BranchId = branchId, DivisionId = divisionId, Payroll = payroll, Status = status, StartDate = startDate, EndDate = endDate, ActorUserId = CurrentUserId() }, cancellationToken));

    [HttpGet("options")]
    public async Task<IActionResult> GetOptions(CancellationToken cancellationToken)
    {
        var response = await _service.GetOptionsAsync(CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    [HttpGet("{id}")]
    [RequirePermission("expense_access")]
    public async Task<IActionResult> GetExpense(ulong id, CancellationToken cancellationToken)
    {
        var response = await _service.GetExpenseAsync(id, CurrentUserId(), cancellationToken);
        NormalizeAttachmentUrls(response);
        return Ok(response);
    }

    [HttpPost]
    [RequirePermission("expenses_create")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateExpense([FromForm] ExpenseFormRequest request, CancellationToken cancellationToken)
    {
        var response = await _service.CreateExpenseAsync(request.ToDto(), CurrentUserId(), cancellationToken);
        response = await AttachFilesAsync(response, request.ExpenseFile, cancellationToken);
        NormalizeAttachmentUrls(response);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPut("{id}")]
    [HttpPatch("{id}")]
    [RequirePermission("expenses_edit")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UpdateExpense(ulong id, [FromForm] ExpenseFormRequest request, CancellationToken cancellationToken)
    {
        var response = await _service.UpdateExpenseAsync(id, request.ToDto(), CurrentUserId(), cancellationToken);
        response = await AttachFilesAsync(response, request.ExpenseFile, cancellationToken);
        NormalizeAttachmentUrls(response);
        return Ok(response);
    }

    [HttpPatch("{id}/status")]
    [RequirePermission("expenses_authority")]
    public async Task<IActionResult> SetStatus(ulong id, [FromBody] ExpenseStatusRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _service.SetStatusAsync(id, request, CurrentUserId(), cancellationToken);
        NormalizeAttachmentUrls(response);
        return Ok(response);
    }

    /// <summary>
    /// The mobile app's single approval action. Available to an approval authority and
    /// to a reporting manager holding <c>expense_checked</c>; the final approve or
    /// reject stays on the CRM status endpoint above.
    /// </summary>
    [HttpPatch("{id}/checked-by-reporting")]
    [RequirePermission("expenses_authority", "expense_checked")]
    public async Task<IActionResult> CheckByReporting(ulong id, CancellationToken cancellationToken)
    {
        var response = await _service.CheckByReportingAsync(id, CurrentUserId(), cancellationToken);
        NormalizeAttachmentUrls(response);
        return Ok(response);
    }

    [HttpGet("{id}/logs")]
    [RequirePermission("expense_access")]
    public async Task<IActionResult> GetLogs(ulong id, CancellationToken cancellationToken) =>
        Ok(await _service.GetLogsAsync(id, CurrentUserId(), cancellationToken));

    /// <summary>Removes one attachment while the expense is still Pending.</summary>
    [HttpDelete("{id}/attachments/{attachmentId}")]
    [RequirePermission("expenses_edit")]
    public async Task<IActionResult> RemoveAttachment(ulong id, ulong attachmentId, CancellationToken cancellationToken)
    {
        var response = await _service.RemoveAttachmentAsync(id, attachmentId, CurrentUserId(), cancellationToken);

        if (response.Extra.TryGetValue("removed_file", out var removed) && removed is string fileName)
        {
            DeleteUploadedFile(fileName);
            response.Extra.Remove("removed_file");
        }

        NormalizeAttachmentUrls(response);
        return Ok(response);
    }

    [HttpDelete("{id}")]
    [RequirePermission("expenses_delete")]
    public async Task<IActionResult> DeleteExpense(ulong id, CancellationToken cancellationToken)
    {
        var response = await _service.DeleteExpenseAsync(id, CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    /// <summary>Clears the stored file once its row is gone, so uploads do not pile up.</summary>
    private void DeleteUploadedFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;

        // Guard against a stored name that tries to walk out of the upload folder.
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) return;

        var path = Path.Combine(UploadRoot(), safeName);
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch (IOException)
        {
            // The row is already gone; a locked file is not worth failing the request.
        }
    }

    private string UploadRoot() =>
        Path.Combine(_environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"), "uploads", "expenses");

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }

    private async Task<Shared.Responses.LaravelApiResponse> AttachFilesAsync(Shared.Responses.LaravelApiResponse response, IReadOnlyCollection<IFormFile>? files, CancellationToken cancellationToken)
    {
        if (files is null || files.Count == 0) return response;
        if (!response.Extra.TryGetValue("expense", out var value) || value is not ExpenseDto expense) return response;

        var uploadRoot = UploadRoot();
        Directory.CreateDirectory(uploadRoot);
        var uploads = new List<ExpenseUploadDto>();

        foreach (var file in files.Where(x => x.Length > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file.FileName);
            var safeFileName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}{extension}";
            var path = Path.Combine(uploadRoot, safeFileName);
            await using var stream = System.IO.File.Create(path);
            await file.CopyToAsync(stream, cancellationToken);
            uploads.Add(new ExpenseUploadDto
            {
                OriginalName = file.FileName,
                FileName = safeFileName,
                MimeType = file.ContentType,
                Size = (ulong)file.Length
            });
        }

        return uploads.Count == 0
            ? response
            : await _service.AddAttachmentsAsync(expense.Id, uploads, cancellationToken);
    }

    private void NormalizeAttachmentUrls(Shared.Responses.LaravelApiResponse response)
    {
        if (response.Extra.TryGetValue("expense", out var expense) && expense is ExpenseDto row)
        {
            NormalizeAttachmentUrls(row);
        }

        if (response.Extra.TryGetValue("expenses", out var expenses) && expenses is IEnumerable<ExpenseDto> rows)
        {
            foreach (var item in rows) NormalizeAttachmentUrls(item);
        }
    }

    private void NormalizeAttachmentUrls(ExpenseDto expense)
    {
        var baseUrl = Request.PublicBaseUrl();
        foreach (var attachment in expense.Attachments)
        {
            if (!string.IsNullOrWhiteSpace(attachment.Url) && !attachment.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                attachment.Url = $"{baseUrl}{(attachment.Url.StartsWith('/') ? string.Empty : "/")}{attachment.Url}";
            }
        }
    }
}

public sealed class ExpenseFormRequest
{
    [FromForm(Name = "expenses_type")] public ulong? ExpensesType { get; set; }
    [FromForm(Name = "user_id")] public ulong? UserId { get; set; }
    [FromForm(Name = "date")] public string? Date { get; set; }
    [FromForm(Name = "claim_amount")] public decimal? ClaimAmount { get; set; }
    [FromForm(Name = "approve_amount")] public decimal? ApproveAmount { get; set; }
    [FromForm(Name = "start_km")] public string? StartKm { get; set; }
    [FromForm(Name = "stop_km")] public string? StopKm { get; set; }
    [FromForm(Name = "total_km")] public string? TotalKm { get; set; }
    [FromForm(Name = "note")] public string? Note { get; set; }
    [FromForm(Name = "reason")] public string? Reason { get; set; }
    [FromForm(Name = "expense_file")] public List<IFormFile> ExpenseFile { get; set; } = [];

    public ExpenseRequestDto ToDto() => new()
    {
        ExpensesType = ExpensesType,
        UserId = UserId,
        Date = Date,
        ClaimAmount = ClaimAmount,
        ApproveAmount = ApproveAmount,
        StartKm = StartKm,
        StopKm = StopKm,
        TotalKm = TotalKm,
        Note = Note,
        Reason = Reason
    };
}
