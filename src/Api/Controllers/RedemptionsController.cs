using System.Security.Claims;
using Api.Filters;
using Application.DTOs.Redemptions;
using Application.Interfaces.Services;
using Domain.Constants;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Responses;

namespace Api.Controllers;

[ApiController]
[Authorize]
[Route("api/redemptions")]
public sealed class RedemptionsController : ControllerBase
{
    private readonly IRedemptionService _service;
    private readonly AppDbContext _db;

    public RedemptionsController(IRedemptionService service, AppDbContext db)
    {
        _service = service;
        _db = db;
    }

    [RequirePermission("redemption.view")]
    [HttpGet]
    public async Task<IActionResult> GetRedemptions(
        [FromQuery] RedemptionFilterDto filter,
        [FromQuery(Name = "redeem_mode")] string? redeemMode,
        CancellationToken cancellationToken)
    {
        filter.RedeemMode ??= redeemMode;
        filter.ActorUserId = CurrentUserId();
        return Ok(await _service.GetRedemptionsAsync(filter, cancellationToken));
    }

    [RequirePermission("redemption.export")]
    [HttpGet("export")]
    public async Task<IActionResult> ExportRedemptions(
        [FromQuery] RedemptionFilterDto filter,
        [FromQuery(Name = "redeem_mode")] string? redeemMode,
        CancellationToken cancellationToken)
    {
        filter.RedeemMode ??= redeemMode;
        filter.ActorUserId = CurrentUserId();
        var file = await _service.ExportRedemptionsAsync(filter, cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpGet("customers")]
    public async Task<IActionResult> GetCustomers([FromQuery] string? search, CancellationToken cancellationToken) =>
        Ok(await _service.GetCustomerOptionsAsync(search, CurrentUserId(), cancellationToken));

    [HttpPost]
    public async Task<IActionResult> CreateRedemption([FromBody] RedemptionCreateRequestDto request, CancellationToken cancellationToken)
    {
        var currentUserId = CurrentUserId();
        var isCustomerProvider = string.Equals(User.FindFirstValue("provider"), "customers", StringComparison.OrdinalIgnoreCase);
        if (isCustomerProvider && currentUserId.HasValue)
        {
            request.CustomerId = currentUserId.Value;
        }
        else if (!await ActorCanCreateRedemptionAsync(currentUserId, cancellationToken))
        {
            // A CRM user needs redemption.create. The customer app (a "customers" provider
            // redeeming its own points) is let through above, because [RequirePermission]
            // would 403 every non-"users" token and so cannot guard this shared endpoint.
            return StatusCode(StatusCodes.Status403Forbidden, LaravelApiResponse.MessageOnly("error", "403 Forbidden"));
        }

        var response = await _service.CreateRedemptionAsync(request, currentUserId, cancellationToken, scopeInvoicesToActor: !isCustomerProvider, isAppChannel: isCustomerProvider);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    /// <summary>True only for a "users"-provider actor that is superadmin or holds
    /// redemption.create. Mirrors RequirePermissionFilter, which cannot sit on this endpoint
    /// because it is shared with the customer app.</summary>
    private async Task<bool> ActorCanCreateRedemptionAsync(ulong? currentUserId, CancellationToken cancellationToken)
    {
        var provider = User.FindFirstValue("provider") ?? "users";
        if (provider != "users" || !currentUserId.HasValue) return false;
        if (User.Claims.Any(claim => claim.Type == ClaimTypes.Role && string.Equals(claim.Value, "superadmin", StringComparison.OrdinalIgnoreCase))) return true;
        return await _db.ModelHasRoles
            .Where(x => x.ModelId == currentUserId.Value && x.ModelType == LaravelModelTypes.User)
            .Join(_db.RoleHasPermissions, modelRole => modelRole.RoleId, rolePermission => rolePermission.RoleId, (_, rolePermission) => rolePermission)
            .Join(_db.Permissions, rolePermission => rolePermission.PermissionId, permission => permission.Id, (_, permission) => permission.Name)
            .AnyAsync(permission => permission == "redemption.create", cancellationToken);
    }

    [RequirePermission("redemption.import")]
    [HttpGet("import-template")]
    public IActionResult ImportTemplate()
    {
        var file = _service.GetImportTemplate();
        return File(file.Content, file.ContentType, file.FileName);
    }

    [RequirePermission("redemption.import")]
    [HttpPost("import")]
    public async Task<IActionResult> ImportRedemptions(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest(new { status = false, message = "Please choose a file to import." });

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;

        var isCustomerProvider = string.Equals(User.FindFirstValue("provider"), "customers", StringComparison.OrdinalIgnoreCase);
        var result = await _service.ImportRedemptionsAsync(stream, CurrentUserId(), scopeInvoicesToActor: !isCustomerProvider, cancellationToken);
        return Ok(new { status = "success", result });
    }

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }
}
