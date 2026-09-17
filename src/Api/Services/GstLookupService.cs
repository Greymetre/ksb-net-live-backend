using System.Net;
using System.Text.Json;

namespace Api.Services;

/// <summary>What the GST register says about a GSTIN. Legal name is the registered
/// business name - for a proprietorship that is the owner's own name; trade name is the
/// name the shop trades under.</summary>
public sealed record GstLookupResult(
    bool Ok,
    string? Gstin,
    string? LegalName,
    string? TradeName,
    string? Status,
    string? Constitution,
    string? TaxpayerType,
    string? RegistrationDate,
    string? Address,
    string? Error);

public interface IGstLookupService
{
    /// <summary>False until an API key is set; the KYC screens then say so instead of failing.</summary>
    bool Configured { get; }

    Task<GstLookupResult> LookupAsync(string gstin, CancellationToken cancellationToken);
}

/// <summary>Looks a GSTIN up with gstinapi.in (GET {url}{gstin}, key in the x-api-key header).
/// The key and, if ever needed, another provider's URL come from GST_LOOKUP_API_KEY /
/// GST_LOOKUP_URL, falling back to GstLookup:ApiKey / GstLookup:Url. Every lookup spends a
/// credit, so callers keep the answer and ask again only when told to.</summary>
public sealed class GstLookupService : IGstLookupService
{
    private const string DefaultUrl = "https://gstinapi.in/v1/gstin/";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GstLookupService> _logger;

    public GstLookupService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<GstLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string? ApiKey =>
        NonEmpty(Environment.GetEnvironmentVariable("GST_LOOKUP_API_KEY")) ?? NonEmpty(_configuration["GstLookup:ApiKey"]);

    private string BaseUrl =>
        (NonEmpty(Environment.GetEnvironmentVariable("GST_LOOKUP_URL")) ?? NonEmpty(_configuration["GstLookup:Url"]) ?? DefaultUrl)
        .TrimEnd('/') + "/";

    public bool Configured => ApiKey is not null;

    public async Task<GstLookupResult> LookupAsync(string gstin, CancellationToken cancellationToken)
    {
        var apiKey = ApiKey;
        if (apiKey is null) return Fail("GST lookup is not set up on this server.");

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + Uri.EscapeDataString(gstin));
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GST lookup for {Gstin} answered {Status}: {Body}", gstin, (int)response.StatusCode, Truncate(body));
                return Fail(ErrorFor(response.StatusCode));
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            // Some providers wrap the record in "data"; read either shape.
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;

            return new GstLookupResult(
                true,
                Read(root, "gstin") ?? gstin,
                Read(root, "legal_name", "lgnm"),
                Read(root, "trade_name", "tradeNam"),
                Read(root, "status", "sts"),
                Read(root, "business_constitution", "ctb"),
                Read(root, "taxpayer_type", "dty"),
                Read(root, "registration_date", "rgdt"),
                Read(root, "address"),
                null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("The GST lookup service did not respond in time. Please try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            _logger.LogWarning(exception, "GST lookup for {Gstin} failed", gstin);
            return Fail("The GST lookup service could not be reached. Please try again.");
        }
    }

    private static string ErrorFor(HttpStatusCode status) => (int)status switch
    {
        400 or 404 or 422 => "This GSTIN was not found on the GST register.",
        401 or 403 => "The GST lookup key was refused. Check GST_LOOKUP_API_KEY.",
        402 or 429 => "GST lookup credits are used up or the request limit was reached.",
        _ => $"The GST lookup service returned an error (HTTP {(int)status})."
    };

    private static string? Read(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        return null;
    }

    private static GstLookupResult Fail(string error) => new(false, null, null, null, null, null, null, null, null, error);
    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Truncate(string value) => value.Length > 300 ? value[..300] : value;
}
