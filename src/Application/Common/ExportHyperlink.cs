using System.Text.Json;

namespace Application.Common;

public sealed record ExportHyperlink(string Text, string Url);

public static class ExportHyperlinkFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ExportHyperlink? Attachment(string? attachment, string baseUrl)
    {
        var path = FirstAttachmentPath(attachment);
        return path is null ? null : new ExportHyperlink("View", AbsoluteUrl(path, baseUrl));
    }

    private static string? FirstAttachmentPath(string? attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment)) return null;
        var text = attachment.Trim();
        if (!text.StartsWith("[", StringComparison.Ordinal)) return text;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(text, JsonOptions)?
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?
                .Trim();
        }
        catch
        {
            return text;
        }
    }

    private static string AbsoluteUrl(string path, string baseUrl)
    {
        var cleanBase = baseUrl.TrimEnd('/');
        var cleanPath = AttachmentPath(path, cleanBase);
        return $"{cleanBase}{cleanPath}";
    }

    private static string AttachmentPath(string path, string baseUrl)
    {
        var value = path.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            value = absolute.PathAndQuery;
        }

        // Migrated values can contain either the frontend or backend virtual-directory
        // prefix. Exported files are always served by the current backend application.
        var basePath = Uri.TryCreate(baseUrl, UriKind.Absolute, out var backend)
            ? backend.AbsolutePath.TrimEnd('/')
            : string.Empty;

        value = RemoveVirtualDirectory(value, "/FieldKonnect_API");
        value = RemoveVirtualDirectory(value, "/FieldKonnect");
        value = value.StartsWith('/') ? value : $"/{value}";

        if (!string.IsNullOrEmpty(basePath) && value.StartsWith($"{basePath}/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[basePath.Length..];
        }

        return value;
    }

    private static string RemoveVirtualDirectory(string path, string prefix)
    {
        if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return "/";
        return path.StartsWith($"{prefix}/", StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : path;
    }
}
