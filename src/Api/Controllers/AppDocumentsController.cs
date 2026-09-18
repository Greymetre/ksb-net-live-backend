using System.Security.Claims;
using Api.Extensions;
using Api.Filters;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>Setting Management > App Document Settings: named PDF documents. Each has a name
/// and exactly one PDF, both required on create and on edit, and two optional ticks for the
/// apps it is meant for - SFA and VRiDDHi. Delete is soft - the row keeps
/// its file and is only stamped deleted_at, so it drops out of every listing.</summary>
[ApiController]
[Authorize]
[Route("api/app-documents")]
public sealed class AppDocumentsController : ControllerBase
{
    private const int MaxNameLength = 200;
    private const long MaxFileBytes = 20 * 1024 * 1024;
    private const string StorageFolder = "app-documents";

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;

    public AppDocumentsController(AppDbContext db, IWebHostEnvironment environment)
    {
        _db = db;
        _environment = environment;
    }

    [HttpGet]
    [RequirePermission("app_document.view")]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.AppDocuments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.DocumentName.Contains(term) || (x.FileName != null && x.FileName.Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var documents = await ToResponsesAsync(rows, cancellationToken);
        return Ok(new { status = "success", app_documents = documents, total, page, page_size = pageSize });
    }

    [HttpGet("{id:long}")]
    [RequirePermission("app_document.detail", "app_document.edit")]
    public async Task<IActionResult> Show(long id, CancellationToken cancellationToken)
    {
        var document = await _db.AppDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (document is null) return NotFoundResult();
        return Ok(new { status = "success", app_document = (await ToResponsesAsync([document], cancellationToken))[0] });
    }

    [HttpPost]
    [RequirePermission("app_document.create")]
    [RequestSizeLimit(MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Create([FromForm] AppDocumentForm form, CancellationToken cancellationToken)
    {
        var name = form.DocumentName?.Trim() ?? string.Empty;
        var nameError = ValidateName(name);
        if (nameError is not null) return Invalid(nameError);
        if (form.Attachment is null || form.Attachment.Length == 0) return Invalid("Attachment is required.");
        var fileError = await ValidatePdfAsync(form.Attachment, cancellationToken);
        if (fileError is not null) return Invalid(fileError);

        var now = DateTime.UtcNow;
        var document = new AppDocument
        {
            DocumentName = name,
            FilePath = await SaveFileAsync(form.Attachment, cancellationToken),
            FileName = CleanFileName(form.Attachment.FileName),
            FileSize = form.Attachment.Length,
            ShowInSfa = form.ShowInSfa,
            ShowInVriddhi = form.ShowInVriddhi,
            CreatedBy = CurrentUserId(),
            UpdatedBy = CurrentUserId(),
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.AppDocuments.Add(document);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            status = "success",
            message = "Document added successfully.",
            app_document = (await ToResponsesAsync([document], cancellationToken))[0]
        });
    }

    /// <summary>The name is always sent. The file is sent only when it was replaced; without
    /// one the document keeps the PDF it has, so it can never be left without an attachment.</summary>
    [HttpPost("{id:long}")]
    [HttpPut("{id:long}")]
    [RequirePermission("app_document.edit")]
    [RequestSizeLimit(MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Update(long id, [FromForm] AppDocumentForm form, CancellationToken cancellationToken)
    {
        var document = await _db.AppDocuments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (document is null) return NotFoundResult();

        var name = form.DocumentName?.Trim() ?? string.Empty;
        var nameError = ValidateName(name);
        if (nameError is not null) return Invalid(nameError);

        if (form.Attachment is { Length: > 0 })
        {
            var fileError = await ValidatePdfAsync(form.Attachment, cancellationToken);
            if (fileError is not null) return Invalid(fileError);
            document.FilePath = await SaveFileAsync(form.Attachment, cancellationToken);
            document.FileName = CleanFileName(form.Attachment.FileName);
            document.FileSize = form.Attachment.Length;
        }
        else if (string.IsNullOrWhiteSpace(document.FilePath))
        {
            return Invalid("Attachment is required.");
        }

        document.DocumentName = name;
        document.ShowInSfa = form.ShowInSfa;
        document.ShowInVriddhi = form.ShowInVriddhi;
        document.UpdatedBy = CurrentUserId();
        document.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            status = "success",
            message = "Document updated successfully.",
            app_document = (await ToResponsesAsync([document], cancellationToken))[0]
        });
    }

    [HttpDelete("{id:long}")]
    [RequirePermission("app_document.delete")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var document = await _db.AppDocuments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (document is null) return NotFoundResult();

        document.DeletedAt = DateTime.UtcNow;
        document.DeletedBy = CurrentUserId();
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(new { status = "success", message = "Document deleted successfully." });
    }

    /// <summary>The SFA (FieldKonnect) app's Documents menu: every document ticked for SFA.
    /// Any signed-in staff user may read it - there is no CRM permission on the app side.</summary>
    [HttpGet("~/api/fieldkonnect/app-documents")]
    public Task<IActionResult> ForSfa(CancellationToken cancellationToken) =>
        IsProvider("customers")
            ? Task.FromResult<IActionResult>(StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "This list is for the SFA app." }))
            : AppListAsync(x => x.ShowInSfa, cancellationToken);

    /// <summary>The VRiDDHi (Loyalty) app's Documents menu, for dealers and retailers alike:
    /// every document ticked for VRiDDHi.</summary>
    [HttpGet("~/api/retailer/app-documents")]
    public async Task<IActionResult> ForVriddhi(CancellationToken cancellationToken)
    {
        if (!IsProvider("customers"))
            return StatusCode(StatusCodes.Status403Forbidden, new { status = "error", message = "This list is for the VRiDDHi app." });
        return await AppListAsync(x => x.ShowInVriddhi, cancellationToken);
    }

    private async Task<IActionResult> AppListAsync(System.Linq.Expressions.Expression<Func<AppDocument, bool>> forApp, CancellationToken cancellationToken)
    {
        var rows = await _db.AppDocuments.AsNoTracking()
            .Where(forApp)
            .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        var documents = rows.Select(x => new
        {
            id = x.Id,
            document_name = x.DocumentName,
            file_name = x.FileName,
            file_size = x.FileSize,
            file_path = x.FilePath,
            file_url = $"{Request.PublicBaseUrl()}/{x.FilePath.TrimStart('/')}",
            updated_at = x.UpdatedAt ?? x.CreatedAt
        });
        return Ok(new { status = "success", data = documents });
    }

    private bool IsProvider(string provider) =>
        string.Equals(User.FindFirstValue("provider"), provider, StringComparison.OrdinalIgnoreCase);

    private async Task<List<AppDocumentResponse>> ToResponsesAsync(IReadOnlyCollection<AppDocument> rows, CancellationToken cancellationToken)
    {
        var userIds = rows.SelectMany(x => new[] { x.CreatedBy, x.UpdatedBy })
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        // A user deleted since still gets their name against what they uploaded.
        var names = userIds.Length == 0
            ? new Dictionary<ulong, string>()
            : await _db.Users.AsNoTracking().IgnoreQueryFilters()
                .Where(x => userIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name ?? string.Empty, cancellationToken);

        return rows.Select(x => new AppDocumentResponse(
            x.Id,
            x.DocumentName,
            x.FilePath,
            x.FileName,
            x.FileSize,
            x.ShowInSfa,
            x.ShowInVriddhi,
            x.CreatedBy.HasValue ? names.GetValueOrDefault(x.CreatedBy.Value) : null,
            x.UpdatedBy.HasValue ? names.GetValueOrDefault(x.UpdatedBy.Value) : null,
            x.CreatedAt,
            x.UpdatedAt)).ToList();
    }

    private static string? ValidateName(string name)
    {
        if (name.Length == 0) return "Document name is required.";
        if (name.Length > MaxNameLength) return $"Document name must be {MaxNameLength} characters or fewer.";
        return null;
    }

    /// <summary>PDF only, checked by extension and by the file's own first bytes, so a renamed
    /// image or Word file is refused too.</summary>
    private static async Task<string?> ValidatePdfAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            return "Only PDF files are allowed.";
        if (file.Length > MaxFileBytes) return "The PDF must be 20 MB or smaller.";

        var header = new byte[5];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAsync(header, cancellationToken);
        return read == 5 && header[0] == '%' && header[1] == 'P' && header[2] == 'D' && header[3] == 'F' && header[4] == '-'
            ? null
            : "Only PDF files are allowed.";
    }

    /// <summary>Stored the way customer documents are: under public/storage, with a copy under
    /// storage, and the path kept relative so the screen resolves it against the API origin.</summary>
    private async Task<string> SaveFileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var fileName = $"{Guid.NewGuid():N}.pdf";
        var webRoot = _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
        var uploadRoot = Path.Combine(webRoot, "public", "storage", StorageFolder);
        Directory.CreateDirectory(uploadRoot);
        var fullPath = Path.Combine(uploadRoot, fileName);
        await using (var stream = System.IO.File.Create(fullPath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }
        var storageRoot = Path.Combine(webRoot, "storage", StorageFolder);
        Directory.CreateDirectory(storageRoot);
        System.IO.File.Copy(fullPath, Path.Combine(storageRoot, fileName), true);
        return $"/public/storage/{StorageFolder}/{fileName}";
    }

    private static string CleanFileName(string fileName)
    {
        var name = Path.GetFileName(fileName).Trim();
        return name.Length > 500 ? name[..500] : name;
    }

    private IActionResult Invalid(string message) =>
        UnprocessableEntity(new { status = "error", message });

    private IActionResult NotFoundResult() =>
        NotFound(new { status = "error", message = "Document not found." });

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }
}

public sealed class AppDocumentForm
{
    [FromForm(Name = "document_name")]
    public string? DocumentName { get; set; }

    [FromForm(Name = "attachment")]
    public IFormFile? Attachment { get; set; }

    /// <summary>Unticked when left out, so a form that omits a box clears it.</summary>
    [FromForm(Name = "show_in_sfa")]
    public bool ShowInSfa { get; set; }

    [FromForm(Name = "show_in_vriddhi")]
    public bool ShowInVriddhi { get; set; }
}

public sealed record AppDocumentResponse(
    long Id,
    string DocumentName,
    string FilePath,
    string? FileName,
    long? FileSize,
    bool ShowInSfa,
    bool ShowInVriddhi,
    string? CreatedByName,
    string? UpdatedByName,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);
