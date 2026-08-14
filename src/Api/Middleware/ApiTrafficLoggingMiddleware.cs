using System.Text;
using System.Text.RegularExpressions;

namespace Api.Middleware;

public sealed partial class ApiTrafficLoggingMiddleware
{
    private const int MaxLoggedBodyLength = 16_000;
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiTrafficLoggingMiddleware> _logger;

    public ApiTrafficLoggingMiddleware(RequestDelegate next, ILogger<ApiTrafficLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var requestBody = await ReadRequestBody(context);
        _logger.LogInformation("API REQUEST {Method} {Path}{Query} body={Body}",
            context.Request.Method, context.Request.Path, context.Request.QueryString,
            Redact(requestBody));

        var originalBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;
        try
        {
            await _next(context);
            responseBuffer.Position = 0;
            var responseBody = await new StreamReader(responseBuffer, Encoding.UTF8, leaveOpen: true).ReadToEndAsync();
            _logger.LogInformation("API RESPONSE {Method} {Path} status={StatusCode} body={Body}",
                context.Request.Method, context.Request.Path, context.Response.StatusCode,
                Redact(responseBody));
            responseBuffer.Position = 0;
            await responseBuffer.CopyToAsync(originalBody, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static async Task<string> ReadRequestBody(HttpContext context)
    {
        if (context.Request.ContentLength is null or 0 ||
            context.Request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
            return context.Request.ContentLength > 0 ? $"<multipart {context.Request.ContentLength} bytes>" : "<empty>";

        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, 4096, true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
        return Limit(body);
    }

    private static string Redact(string value) => SensitiveJsonValue().Replace(Limit(value), "$1<redacted>$3");
    private static string Limit(string value) => value.Length <= MaxLoggedBodyLength ? value : value[..MaxLoggedBodyLength] + "<truncated>";

    [GeneratedRegex("(\"(?:password|password_confirmation|token|access_token|refresh_token|authorization)\"\\s*:\\s*\")(.*?)(\")", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveJsonValue();
}
