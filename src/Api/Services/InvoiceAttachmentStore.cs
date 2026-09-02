using Domain.Entities;
using Shared.Exceptions;

namespace Api.Services;

/// <summary>Saving, validating and removing the files on an invoice.
///
/// One place decides the rules, because invoices are raised from three different
/// screens - the CRM, the field app and the dealer app - and all three must agree
/// on what is allowed and on the wording when something is not.
///
/// Images are compressed on the device or in the browser before they are sent, so
/// anything arriving over the limit here has already had its chance. This is the
/// gate that cannot be bypassed by an old app build or a direct API call.</summary>
public sealed class InvoiceAttachmentStore
{
    public const int MaxAttachments = 10;
    public const long MaxImageBytes = 5L * 1024 * 1024;
    public const long MaxPdfBytes = 10L * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif"];
    private const string PdfExtension = ".pdf";

    private readonly IWebHostEnvironment _environment;

    public InvoiceAttachmentStore(IWebHostEnvironment environment) => _environment = environment;

    private static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.#");

    /// <summary>Checks the whole set before a single byte is written, so a rejected
    /// upload never leaves half its files behind.</summary>
    public void Validate(IReadOnlyList<IFormFile> files)
    {
        if (files.Count > MaxAttachments)
            throw new LaravelHttpException(422, $"An invoice can carry at most {MaxAttachments} attachments. You selected {files.Count}.");

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var isPdf = extension == PdfExtension || file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
            var isImage = ImageExtensions.Contains(extension) || file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

            if (!isPdf && !isImage)
                throw new LaravelHttpException(422, $"\"{file.FileName}\" is not allowed. Attach images or PDF files only.");

            var limit = isPdf ? MaxPdfBytes : MaxImageBytes;
            if (file.Length > limit)
            {
                // The apps and the CRM shrink an image before sending. Reaching this with an
                // image means shrinking it was not enough, and the message says so.
                throw new LaravelHttpException(422, isPdf
                    ? $"\"{file.FileName}\" is {Megabytes(file.Length)} MB. A PDF attachment must be {Megabytes(MaxPdfBytes)} MB or less."
                    : $"After compression the attachment is greater than {Megabytes(MaxImageBytes)} MB. \"{file.FileName}\" is {Megabytes(file.Length)} MB - please attach a smaller image.");
            }

            if (file.Length == 0)
                throw new LaravelHttpException(422, $"\"{file.FileName}\" is empty.");
        }
    }

    public async Task<List<NewInvoiceAttachment>> SaveAsync(IReadOnlyList<IFormFile> files, CancellationToken cancellationToken)
    {
        Validate(files);

        var root = Path.Combine(_environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"), "uploads", "new-invoices");
        Directory.CreateDirectory(root);

        var saved = new List<NewInvoiceAttachment>();
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                var storedName = $"{Guid.NewGuid():N}{extension}";
                await using (var stream = File.Create(Path.Combine(root, storedName)))
                {
                    await file.CopyToAsync(stream, cancellationToken);
                }

                saved.Add(new NewInvoiceAttachment
                {
                    FilePath = $"/uploads/new-invoices/{storedName}",
                    FileName = Path.GetFileName(file.FileName),
                    MimeType = file.ContentType,
                    FileSize = file.Length,
                    SortOrder = index,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }
        catch
        {
            // A failure part way through must not leave the earlier files on disk.
            foreach (var attachment in saved) Delete(attachment.FilePath);
            throw;
        }

        return saved;
    }

    public void Delete(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || !storedPath.StartsWith("/uploads/new-invoices/", StringComparison.Ordinal)) return;
        var root = _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
        var path = Path.Combine(root, storedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A file that will not delete must not fail the request that removed the invoice.
        }
    }

    public void DeleteAll(IEnumerable<string?> storedPaths)
    {
        foreach (var path in storedPaths) Delete(path);
    }
}
