namespace Api.Extensions;

public static class HttpRequestExtensions
{
    /// <summary>
    /// The origin a client should use to reach this API, including the path the app
    /// is mounted on. Live runs under /FieldKonnect_API, so a URL built from scheme
    /// and host alone points outside the application and 404s - which is how stored
    /// files end up as broken links.
    /// </summary>
    public static string PublicBaseUrl(this HttpRequest request)
    {
        // A reverse proxy that strips the prefix announces it in this header; without
        // one, PathBase already carries the mount point.
        var forwardedPrefix = request.Headers["X-Forwarded-Prefix"].FirstOrDefault();
        var pathBase = string.IsNullOrWhiteSpace(forwardedPrefix)
            ? request.PathBase.Value
            : forwardedPrefix;

        return $"{request.Scheme}://{request.Host}{NormalizePathBase(pathBase)}";
    }

    private static string NormalizePathBase(string? pathBase)
    {
        if (string.IsNullOrWhiteSpace(pathBase) || pathBase == "/") return string.Empty;
        return $"/{pathBase.Trim('/')}";
    }
}
