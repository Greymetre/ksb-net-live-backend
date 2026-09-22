using System.Security.Claims;
using Api.Services;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// The phone's push token, for both apps: the field app signs in as a user, VRiDDHi as a
/// customer (the token's provider claim says which). The app calls register after sign-in and
/// whenever Firebase hands it a new token, and unregister on sign-out.
/// </summary>
[ApiController]
[Authorize]
[Route("api/push")]
public sealed class PushNotificationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PushNotificationService _push;

    public PushNotificationsController(AppDbContext db, PushNotificationService push)
    {
        _db = db;
        _push = push;
    }

    public sealed record RegisterRequest(string? Token, string? Platform);

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var token = request.Token?.Trim() ?? string.Empty;
        if (token.Length is 0 or > 450) return BadRequest(new { status = "error", message = "A valid push token is required." });
        var platform = string.Equals(request.Platform, "ios", StringComparison.OrdinalIgnoreCase) ? "ios" : "android";
        var (isCustomer, id) = Caller();
        if (id == 0) return Unauthorized(new { status = "error", message = "Unauthenticated." });

        // A phone belongs to whoever signed in on it last: drop the token from any other account,
        // or a shared phone would get the previous person's notifications too.
        await _db.Users.IgnoreQueryFilters().Where(x => x.NotificationId == token && (isCustomer || x.Id != id))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.NotificationId, string.Empty), ct);
        await _db.Customers.IgnoreQueryFilters().Where(x => x.NotificationId == token && (!isCustomer || x.Id != id))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.NotificationId, string.Empty), ct);

        var updated = isCustomer
            ? await _db.Customers.IgnoreQueryFilters().Where(x => x.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.NotificationId, token).SetProperty(x => x.DeviceType, platform), ct)
            : await _db.Users.IgnoreQueryFilters().Where(x => x.Id == id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.NotificationId, token).SetProperty(x => x.DeviceType, platform), ct);
        return updated == 0
            ? NotFound(new { status = "error", message = "Account not found." })
            : Ok(new { status = "success", message = "Push token saved." });
    }

    [HttpPost("unregister")]
    public async Task<IActionResult> Unregister([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var token = request.Token?.Trim() ?? string.Empty;
        var (isCustomer, id) = Caller();
        if (id == 0) return Unauthorized(new { status = "error", message = "Unauthenticated." });
        // Only the token this phone sent; a newer phone's token on the same account stays.
        if (isCustomer)
            await _db.Customers.IgnoreQueryFilters().Where(x => x.Id == id && (token == "" || x.NotificationId == token))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.NotificationId, string.Empty), ct);
        else
            await _db.Users.IgnoreQueryFilters().Where(x => x.Id == id && (token == "" || x.NotificationId == token))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.NotificationId, string.Empty), ct);
        return Ok(new { status = "success", message = "Push token removed." });
    }

    /// <summary>Whether this server can send, and whether this account has a phone registered.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var (isCustomer, id) = Caller();
        var token = isCustomer
            ? await _db.Customers.AsNoTracking().IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.NotificationId).FirstOrDefaultAsync(ct)
            : await _db.Users.AsNoTracking().IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.NotificationId).FirstOrDefaultAsync(ct);
        return Ok(new { status = "success", configured = _push.IsConfigured, project_id = _push.ProjectId, device_registered = !string.IsNullOrWhiteSpace(token) });
    }

    /// <summary>A test notification to the caller's own phone - for checking the setup.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        var (isCustomer, id) = Caller();
        if (id == 0) return Unauthorized(new { status = "error", message = "Unauthenticated." });
        var data = new Dictionary<string, string> { ["type"] = "test" };
        var result = isCustomer
            ? await _push.SendToCustomerAsync(id, "Test notification", "Push notifications are working on this phone.", data, ct)
            : await _push.SendToUserAsync(id, "Test notification", "Push notifications are working on this phone.", data, ct);
        var message = result.Status switch
        {
            "sent" => "Test notification sent.",
            "not_configured" => "Push notifications are not set up on this server yet.",
            "no_device" => "No phone is registered for this account. Sign in to the app on a phone first.",
            "invalid_token" => "The registered phone no longer accepts notifications. Sign in to the app again.",
            _ => "The notification could not be sent."
        };
        return Ok(new { status = result.Sent ? "success" : "error", result = result.Status, message });
    }

    private (bool IsCustomer, ulong Id) Caller()
    {
        var isCustomer = string.Equals(User.FindFirstValue("provider"), "customers", StringComparison.OrdinalIgnoreCase);
        return (isCustomer, ulong.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0);
    }
}
