using System.Security.Claims;
using Api.Extensions;
using Application.DTOs.NewInvoices;
using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Responses;

namespace Api.Controllers;

/// <summary>Invoices for the field app.
///
/// The CRM's own invoice endpoints are gated by the invoice_transaction permissions, which
/// no field role holds - an ASR who is meant to raise invoices for their own retailers would
/// get a 403 from every one of them. These endpoints carry no permission of their own, the
/// way the dealer app's endpoints do not either, and lean on the data scope instead: the
/// repository is given the signed-in user and answers with the invoices of the retailers
/// assigned to them and to everyone reporting to them, however many levels down. Somebody
/// with nobody under them sees exactly their own retailers.</summary>
[ApiController]
[Authorize]
[Route("api/field")]
public sealed class FieldInvoiceController : ControllerBase
{
    private const int MaxPageSize = 50;
    private readonly INewInvoiceService _newInvoiceService;
    private readonly INewInvoiceRepository _newInvoiceRepository;
    private readonly IWebHostEnvironment _environment;
    private readonly Api.Services.InvoiceAttachmentStore _attachments;

    public FieldInvoiceController(
        INewInvoiceService newInvoiceService,
        INewInvoiceRepository newInvoiceRepository,
        IWebHostEnvironment environment,
        Api.Services.InvoiceAttachmentStore attachments)
    {
        _newInvoiceService = newInvoiceService;
        _newInvoiceRepository = newInvoiceRepository;
        _environment = environment;
        _attachments = attachments;
    }

    /// <summary>Whatever the caller sent, as one list. Old builds send "attachment",
    /// current ones send "attachments"; both are accepted together.</summary>
    private static List<IFormFile> IncomingFiles(FieldInvoiceForm form)
    {
        var files = new List<IFormFile>();
        if (form.Attachment is { Length: > 0 }) files.Add(form.Attachment);
        if (form.Attachments is not null) files.AddRange(form.Attachments.Where(x => x.Length > 0));
        return files;
    }

    [HttpGet("invoices")]
    public async Task<IActionResult> GetInvoices(
        [FromQuery] string? search,
        [FromQuery(Name = "approval_status")] int? approvalStatus,
        [FromQuery(Name = "from_date")] DateTime? fromDate,
        [FromQuery(Name = "to_date")] DateTime? toDate,
        [FromQuery] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var filter = new NewInvoiceFilterDto
        {
            RetailerSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            ApprovalStatus = approvalStatus,
            FromDate = fromDate,
            ToDate = toDate,
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, MaxPageSize)
        };

        var result = await _newInvoiceRepository.GetInvoicesAsync(filter, CurrentUserId(), cancellationToken);
        var summary = await _newInvoiceRepository.GetInvoiceSummaryAsync(filter, CurrentUserId(), cancellationToken);
        var canCreate = await _newInvoiceRepository.CanCreateFieldInvoiceAsync(CurrentUserId(), cancellationToken);

        return Ok(new
        {
            status = "success",
            can_create = canCreate,
            summary = new
            {
                total = summary.TotalInvoices,
                pending = summary.Pending,
                hold = summary.Hold,
                approved = summary.ApprovedHo,
                in_process = summary.ApprovedSs + summary.ApprovedSales,
                rejected = summary.Rejected,
                total_amount = summary.TotalAmount
            },
            items = result.Items.Select(ToListItem),
            pagination = new { page = result.Page, page_size = result.PageSize, total = result.Total }
        });
    }

    [HttpGet("invoices/{id:long}")]
    public async Task<IActionResult> GetInvoice(ulong id, CancellationToken cancellationToken)
    {
        var invoice = await _newInvoiceRepository.GetInvoiceAsync(id, CurrentUserId(), cancellationToken);
        if (invoice is null) return NotFound(new { status = "error", message = "Invoice not found." });
        return Ok(new { status = "success", invoice = ToDetail(invoice) });
    }

    /// <summary>The retailers this user may raise an invoice for - the same set whose invoices
    /// they can see, so an invoice can never be raised into a list they cannot read.</summary>
    [HttpGet("invoice-retailers")]
    public async Task<IActionResult> GetRetailers(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // A page at a time, searched in SQL. Handing a phone every retailer the actor can
        // reach - fourteen thousand of them for a manager - is what made the picker hang.
        var result = await _newInvoiceRepository.GetRetailerOptionPageAsync(
            search, CurrentUserId(), Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize), cancellationToken);

        return Ok(new
        {
            status = "success",
            retailers = result.Items.Select(retailer => new
            {
                id = retailer.Id,
                owner_name = retailer.OwnerName,
                shop_name = retailer.ShopName,
                mobile = retailer.MobileNumber,
                city = retailer.CityName,
                address = retailer.Address
            }),
            pagination = new
            {
                page = result.Page,
                page_size = result.PageSize,
                total = result.Total,
                has_more = result.Page * result.PageSize < result.Total
            }
        });
    }

    [HttpGet("retailer-dealers")]
    public async Task<IActionResult> GetRetailerDealers([FromQuery(Name = "customer_id")] ulong customerId, CancellationToken cancellationToken)
    {
        var dealers = await _newInvoiceRepository.GetRetailerDealerOptionsAsync(customerId, cancellationToken);
        return Ok(new
        {
            status = "success",
            dealers = dealers.Select(dealer => new { id = dealer.Id, name = dealer.Name, firm_name = dealer.FirmName, code = dealer.Code })
        });
    }

    /// <summary>The scheme cards. A scheme is shown when any retailer this user can reach
    /// would qualify for it - which is how a scheme built for one branch, zone or state
    /// reaches exactly the people working that area.</summary>
    [HttpGet("schemes")]
    public async Task<IActionResult> GetSchemes(CancellationToken cancellationToken)
    {
        var schemes = await _newInvoiceRepository.GetFieldSchemesAsync(CurrentUserId(), Today(), cancellationToken);
        return Ok(new { status = "success", schemes = schemes.Select(scheme => ToSchemeCard(scheme, Request.PublicBaseUrl())) });
    }

    [HttpGet("schemes/{id:long}")]
    public async Task<IActionResult> GetScheme(ulong id, CancellationToken cancellationToken)
    {
        var detail = await _newInvoiceRepository.GetFieldSchemeAsync(id, CurrentUserId(), Today(), cancellationToken);
        if (detail is null) return NotFound(new { status = "error", message = "Scheme not found." });

        return Ok(new
        {
            status = "success",
            scheme = ToSchemeCard(detail.Scheme, Request.PublicBaseUrl()),
            slabs = detail.Slabs.Select(slab => new
            {
                from_amount = slab.FromAmount,
                to_amount = slab.ToAmount,
                value = slab.Value,
                value_type = slab.ValueType,
                reward_label = slab.RewardLabel
            }),
            performance = new
            {
                invoice_count = detail.InvoiceCount,
                retailer_count = detail.RetailerCount,
                approved_amount = detail.ApprovedAmount,
                pending_amount = detail.PendingAmount,
                points_earned = detail.PointsEarned,
                points_expected = detail.PointsExpected
            }
        });
    }

    private static object ToSchemeCard(FieldSchemeDto scheme, string baseUrl) => new
    {
        id = scheme.Id,
        name = scheme.Name,
        code = scheme.Code,
        scheme_note = scheme.SchemeNote,
        // Absolute, like the invoice attachment on this controller, so the app can
        // open it without knowing where the API lives.
        brochure_url = string.IsNullOrWhiteSpace(scheme.BrochurePath) ? null : $"{baseUrl}{scheme.BrochurePath}",
        tag = scheme.Tag,
        wallet_type = scheme.WalletType,
        based_on = scheme.BasedOn,
        start_date = scheme.StartDate.ToString("yyyy-MM-dd"),
        end_date = scheme.EndDate.ToString("yyyy-MM-dd"),
        status = scheme.Status,
        status_label = scheme.StatusLabel,
        is_live = scheme.IsLive,
        days_remaining = scheme.DaysRemaining,
        area_scope = scheme.AreaScope,
        area_values = scheme.AreaValues,
        customer_type = scheme.CustomerType,
        slab_count = scheme.SlabCount
    };

    /// <summary>Schemes run on Indian dates; reading "today" in UTC would end a scheme
    /// five and a half hours early.</summary>
    private static DateOnly Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, IndiaTimeZone).DateTime);

    private static readonly TimeZoneInfo IndiaTimeZone = ResolveIndiaTimeZone();

    private static TimeZoneInfo ResolveIndiaTimeZone()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
    }

    [HttpGet("invoice-schemes")]
    public async Task<IActionResult> GetSchemes(
        [FromQuery(Name = "customer_id")] ulong customerId,
        [FromQuery(Name = "invoice_date")] DateTime? invoiceDate,
        CancellationToken cancellationToken)
    {
        var response = await _newInvoiceService.GetSchemeOptionsAsync(customerId, invoiceDate, cancellationToken);
        var schemes = response.Extra.TryGetValue("schemes", out var value) ? value : null;
        return Ok(new { status = "success", schemes });
    }

    [HttpPost("invoices")]
    [RequestSizeLimit(15_000_000)]
    public async Task<IActionResult> CreateInvoice([FromForm] FieldInvoiceForm form, CancellationToken cancellationToken)
    {
        // The app hides the add button for anyone but an ASR; the same rule is applied here so
        // hiding it is not the only thing standing between another role and a new invoice.
        if (!await _newInvoiceRepository.CanCreateFieldInvoiceAsync(CurrentUserId(), cancellationToken))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "Only an ASR can add an invoice." });
        }

        var files = IncomingFiles(form);
        if (files.Count == 0)
        {
            return UnprocessableEntity(new { status = "error", message = "Invoice attachment is required." });
        }

        var saved = await _attachments.SaveAsync(files, cancellationToken);
        try
        {
            var response = await _newInvoiceService.CreateInvoiceAsync(new NewInvoiceRequestDto
            {
                SecondaryCustomerId = form.RetailerId,
                DealerCustomerId = form.DealerId,
                SchemeId = form.SchemeId,
                InvoiceNumber = form.InvoiceNumber,
                InvoiceDate = form.InvoiceDate,
                Amount = form.Amount,
                Points = 0,
                Attachment = saved[0].FilePath,
                Attachments = saved.Select(x => new InvoiceAttachmentInput
                {
                    FilePath = x.FilePath, FileName = x.FileName, MimeType = x.MimeType, FileSize = x.FileSize
                }).ToList()
            }, CurrentUserId(), cancellationToken);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch
        {
            // A rejected invoice - duplicate number, ineligible scheme - must not leave the
            // uploaded files behind on every retry.
            _attachments.DeleteAll(saved.Select(x => x.FilePath));
            throw;
        }
    }

    /// <summary>Correcting an invoice that has not been acted on yet. The service allows this
    /// only while the invoice is pending or on hold, and puts it back to pending afterwards -
    /// whoever reviews it next is looking at different figures. A new photo is optional; without
    /// one the invoice keeps the one it has.</summary>
    [HttpPost("invoices/{id:long}")]
    [HttpPut("invoices/{id:long}")]
    [RequestSizeLimit(15_000_000)]
    public async Task<IActionResult> UpdateInvoice(ulong id, [FromForm] FieldInvoiceForm form, CancellationToken cancellationToken)
    {
        var existing = await _newInvoiceRepository.GetInvoiceAsync(id, CurrentUserId(), cancellationToken);
        if (existing is null) return NotFound(new { status = "error", message = "Invoice not found." });

        var files = IncomingFiles(form);
        var saved = files.Count > 0 ? await _attachments.SaveAsync(files, cancellationToken) : [];

        try
        {
            var response = await _newInvoiceService.UpdateInvoiceAsync(id, new NewInvoiceRequestDto
            {
                SecondaryCustomerId = form.RetailerId,
                DealerCustomerId = form.DealerId,
                SchemeId = form.SchemeId,
                InvoiceNumber = form.InvoiceNumber,
                InvoiceDate = form.InvoiceDate,
                Amount = form.Amount,
                Points = 0,
                Attachment = saved.Count > 0 ? saved[0].FilePath : existing.Attachment,
                Attachments = saved.Select(x => new InvoiceAttachmentInput
                {
                    FilePath = x.FilePath, FileName = x.FileName, MimeType = x.MimeType, FileSize = x.FileSize
                }).ToList(),
                RemovedAttachmentIds = form.RemovedAttachmentIds ?? []
            }, CurrentUserId(), cancellationToken);

            // Files the edit took off the invoice go once the change has stuck.
            if (response.Extra.TryGetValue("removed_files", out var removed) && removed is IEnumerable<string> paths)
            {
                _attachments.DeleteAll(paths);
            }
            // Internal storage paths, not something the caller should see.
            response.Extra.Remove("removed_files");

            return Ok(response);
        }
        catch
        {
            // Only the files this request uploaded go; the ones already on the invoice stay.
            _attachments.DeleteAll(saved.Select(x => x.FilePath));
            throw;
        }
    }

    /// <summary>Removing an invoice that has not been approved. A held invoice can go too -
    /// a hold is a correction asked for, and the dealer may simply re-enter it.</summary>
    [HttpDelete("invoices/{id:long}")]
    public async Task<IActionResult> DeleteInvoice(ulong id, CancellationToken cancellationToken)
    {
        var response = await _newInvoiceService.DeleteInvoiceAsync(
            id, InvoiceDeletePolicy.PendingOrHold, CurrentUserId(), cancellationToken);

        if (response.Extra.TryGetValue("removed_files", out var removed) && removed is IEnumerable<string> files)
        {
            foreach (var file in files) DeleteAttachment(file);
            response.Extra.Remove("removed_files");
        }

        return Ok(response);
    }

    private object ToListItem(NewInvoiceDto invoice) => new
    {
        id = invoice.Id,
        invoice_number = invoice.InvoiceNumber,
        invoice_date = invoice.InvoiceDate,
        amount = invoice.Amount,
        points = invoice.SchemePoints,
        approval_status = invoice.ApprovalStatus,
        status_label = invoice.ApprovalStatusLabel,
        retailer_id = invoice.SecondaryCustomerId,
        retailer_name = invoice.CustomerName,
        shop_name = invoice.ShopName,
        mobile = invoice.MobileNumber,
        dealer_name = invoice.AssignedDistributorName,
        scheme_name = invoice.SchemeName,
        scheme_note = invoice.SchemeNote,
        created_by_name = invoice.CreatedByName,
        created_at = invoice.CreatedAt
    };

    private object ToDetail(NewInvoiceDto invoice) => new
    {
        id = invoice.Id,
        invoice_number = invoice.InvoiceNumber,
        invoice_date = invoice.InvoiceDate,
        amount = invoice.Amount,
        points = invoice.SchemePoints,
        approval_status = invoice.ApprovalStatus,
        status_label = invoice.ApprovalStatusLabel,
        approval_remark = invoice.ApprovalRemark,
        retailer_id = invoice.SecondaryCustomerId,
        retailer_name = invoice.CustomerName,
        shop_name = invoice.ShopName,
        mobile = invoice.MobileNumber,
        city = invoice.CityName,
        dealer_name = invoice.AssignedDistributorName,
        scheme_name = invoice.SchemeName,
        scheme_note = invoice.SchemeNote,
        attachment = string.IsNullOrWhiteSpace(invoice.Attachment) ? null : $"{Request.PublicBaseUrl()}{invoice.Attachment}",
        created_by_name = invoice.CreatedByName,
        created_at = invoice.CreatedAt,
        // Pending or on hold: nobody has acted on it, so it can still be corrected or removed.
        can_edit = invoice.ApprovalStatus is 0 or 5,
        can_delete = invoice.ApprovalStatus is 0 or 5,
        scheme_id = invoice.SchemeId,
        dealer_id = invoice.AssignedDistributorId,
        attachment_path = invoice.Attachment,
        // Every file on the invoice, absolute so the app can show it straight away.
        attachments = invoice.Attachments?.Select(file => new
        {
            id = file.Id,
            url = $"{Request.PublicBaseUrl()}{file.FilePath}",
            file_path = file.FilePath,
            file_name = file.FileName,
            mime_type = file.MimeType,
            file_size = file.FileSize
        }),
        approval_logs = invoice.ApprovalLogs?.Select(log => new
        {
            status_type = log.StatusType,
            remark = log.Remark,
            by = log.CreatedByName,
            at = log.CreatedAt
        })
    };

    private async Task<string> SaveAttachmentAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png" or ".webp")
            && !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new Shared.Exceptions.LaravelHttpException(422, "Only PDF and image invoice attachments are allowed.");
        }

        var root = Path.Combine(_environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"), "uploads", "new-invoices");
        Directory.CreateDirectory(root);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        await using var stream = System.IO.File.Create(Path.Combine(root, fileName));
        await file.CopyToAsync(stream, cancellationToken);
        return $"/uploads/new-invoices/{fileName}";
    }

    private void DeleteAttachment(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || !storedPath.StartsWith("/uploads/new-invoices/", StringComparison.Ordinal)) return;
        var root = _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
        var path = Path.Combine(root, storedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch (IOException)
        {
            // A file left behind is noise, not a failure - never mask the real error.
        }
    }

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }
}

public sealed class FieldInvoiceForm
{
    [FromForm(Name = "retailer_id")] public ulong RetailerId { get; set; }
    [FromForm(Name = "dealer_id")] public ulong? DealerId { get; set; }
    [FromForm(Name = "scheme_id")] public ulong? SchemeId { get; set; }
    [FromForm(Name = "invoice_number")] public string? InvoiceNumber { get; set; }
    [FromForm(Name = "invoice_date")] public DateTime? InvoiceDate { get; set; }
    [FromForm(Name = "amount")] public decimal? Amount { get; set; }
    [FromForm(Name = "attachment")] public IFormFile? Attachment { get; set; }
    /// <summary>Several files may be sent at once. The single "attachment" field is still
    /// read, so an app build from before this change keeps working.</summary>
    [FromForm(Name = "attachments")] public List<IFormFile>? Attachments { get; set; }
    [FromForm(Name = "removed_attachment_ids")] public List<long>? RemovedAttachmentIds { get; set; }
}
