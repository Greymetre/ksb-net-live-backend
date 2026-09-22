using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Push notifications to both apps through Firebase Cloud Messaging (HTTP v1). A phone's token
/// is kept in notification_id - on users for the field app (SFA), on customers for VRiDDHi -
/// one per account, from the phone that signed in last.
///
/// The Firebase service account (the JSON key from the Firebase console) is read from
/// FIREBASE_SERVICE_ACCOUNT_FILE (a path) or FIREBASE_SERVICE_ACCOUNT_JSON (the JSON itself).
/// Without it nothing is sent and nothing fails: every send reports "not configured".
/// Sending never throws - a notification that cannot go out must not break the action behind it.
///
/// Tapping a notification opens a screen; say which in <c>data</c> (all values are strings):
///   field app (SFA): "screen" = a route name (e.g. "RetailerKyc"), "params" = JSON of its
///     route params (e.g. {"retailerId":12,"retailerName":"Shree Traders"}).
///   VRiDDHi: "screen" = a route (e.g. "InvoiceDetail", "Invoices", "Kyc", "Scheme", "Documents"),
///     "id" = the record, where the screen needs one (InvoiceDetail: the invoice id).
/// No "screen" = the app just opens. Delivery works with the app open, in the background or closed.
/// </summary>
public sealed class PushNotificationService
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    // One access token for the whole process; Google's tokens live an hour.
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? _accessToken;
    private static DateTime _accessTokenExpiresAt;

    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PushNotificationService> _logger;

    public PushNotificationService(AppDbContext db, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<PushNotificationService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public sealed record PushResult(bool Sent, string Status);

    public bool IsConfigured => LoadServiceAccount() is not null;

    public string? ProjectId => LoadServiceAccount()?.ProjectId;

    /// <summary>To a field-app (CRM) user's phone.</summary>
    public async Task<PushResult> SendToUserAsync(ulong userId, string title, string body, IDictionary<string, string>? data = null, CancellationToken cancellationToken = default)
    {
        var token = await _db.Users.AsNoTracking().IgnoreQueryFilters().Where(x => x.Id == userId).Select(x => x.NotificationId).FirstOrDefaultAsync(cancellationToken);
        var result = await SendAsync(token, title, body, data, cancellationToken);
        if (result.Status == "invalid_token")
            await _db.Users.IgnoreQueryFilters().Where(x => x.Id == userId && x.NotificationId == token)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.NotificationId, string.Empty), cancellationToken);
        return result;
    }

    /// <summary>To a VRiDDHi retailer's or dealer's phone.</summary>
    public async Task<PushResult> SendToCustomerAsync(ulong customerId, string title, string body, IDictionary<string, string>? data = null, CancellationToken cancellationToken = default)
    {
        var token = await _db.Customers.AsNoTracking().IgnoreQueryFilters().Where(x => x.Id == customerId).Select(x => x.NotificationId).FirstOrDefaultAsync(cancellationToken);
        var result = await SendAsync(token, title, body, data, cancellationToken);
        if (result.Status == "invalid_token")
            await _db.Customers.IgnoreQueryFilters().Where(x => x.Id == customerId && x.NotificationId == token)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.NotificationId, string.Empty), cancellationToken);
        return result;
    }

    private async Task<PushResult> SendAsync(string? deviceToken, string title, string body, IDictionary<string, string>? data, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceToken)) return new(false, "no_device");
        var account = LoadServiceAccount();
        if (account is null) return new(false, "not_configured");

        try
        {
            var accessToken = await AccessTokenAsync(account, cancellationToken);
            var message = new Dictionary<string, object?>
            {
                ["token"] = deviceToken,
                ["notification"] = new { title, body },
                ["data"] = data ?? new Dictionary<string, string>(),
                ["android"] = new { priority = "high", notification = new { sound = "default" } },
                ["apns"] = new { payload = new { aps = new { sound = "default" } } }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{account.ProjectId}/messages:send")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return new(true, "sent");

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            // The phone uninstalled the app or the token was replaced: forget it.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound || error.Contains("UNREGISTERED", StringComparison.Ordinal)
                || (response.StatusCode == System.Net.HttpStatusCode.BadRequest && error.Contains("registration token", StringComparison.OrdinalIgnoreCase)))
                return new(false, "invalid_token");
            _logger.LogWarning("Push notification failed: {Status} {Error}", (int)response.StatusCode, error);
            return new(false, "failed");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Push notification could not be sent");
            return new(false, "failed");
        }
    }

    private sealed record ServiceAccount(string ProjectId, string ClientEmail, string PrivateKey, string TokenUri);

    private ServiceAccount? LoadServiceAccount()
    {
        try
        {
            var json = _configuration["FIREBASE_SERVICE_ACCOUNT_JSON"];
            var path = _configuration["FIREBASE_SERVICE_ACCOUNT_FILE"];
            if (string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(path) && File.Exists(path)) json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string Read(string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
            var account = new ServiceAccount(Read("project_id"), Read("client_email"), Read("private_key"), Read("token_uri"));
            return string.IsNullOrEmpty(account.ProjectId) || string.IsNullOrEmpty(account.ClientEmail) || string.IsNullOrEmpty(account.PrivateKey) ? null : account;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Firebase service account could not be read");
            return null;
        }
    }

    /// <summary>A signed JWT from the service account, exchanged at Google for an access token.</summary>
    private async Task<string> AccessTokenAsync(ServiceAccount account, CancellationToken cancellationToken)
    {
        if (_accessToken is not null && DateTime.UtcNow < _accessTokenExpiresAt) return _accessToken;
        await TokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _accessTokenExpiresAt) return _accessToken;
            var tokenUri = string.IsNullOrEmpty(account.TokenUri) ? "https://oauth2.googleapis.com/token" : account.TokenUri;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = account.ClientEmail, scope = Scope, aud = tokenUri, iat = now, exp = now + 3600 }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(account.PrivateKey);
            var signature = Base64Url(rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

            using var response = await _httpClientFactory.CreateClient().PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = $"{header}.{claims}.{signature}"
            }), cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            _accessToken = document.RootElement.GetProperty("access_token").GetString();
            _accessTokenExpiresAt = DateTime.UtcNow.AddSeconds(document.RootElement.GetProperty("expires_in").GetInt32() - 120);
            return _accessToken!;
        }
        finally
        {
            TokenLock.Release();
        }
    }
}
