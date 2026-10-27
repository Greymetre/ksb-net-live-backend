using System.Security.Claims;
using Api.Filters;
using Application.DTOs.Redemptions;
using Application.Interfaces.Services;
using Domain.Constants;
using Domain.Entities;
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

    [RequirePermission("redemption.update")]
    [HttpPost("update-import")]
    public async Task<IActionResult> UpdateRedemptionsImport(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest(new { status = false, message = "Please choose a file to import." });

        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;

        var result = await _service.UpdateRedemptionsImportAsync(stream, file.FileName, CurrentUserId(), cancellationToken);
        return Ok(new { status = "success", result });
    }

    [RequirePermission("redemption.edit")]
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateRedemption(ulong id, [FromBody] RedemptionCreateRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _service.UpdateRedemptionAsync(id, request, CurrentUserId(), IsSuperAdmin(), cancellationToken);
        return Ok(response);
    }

    [RequirePermission("redemption.approve")]
    [HttpPost("{id}/approve")]
    public async Task<IActionResult> ApproveRedemption(ulong id, CancellationToken cancellationToken)
    {
        var redemption = await LoadInScopeAsync(id, cancellationToken);
        if (redemption is null) return NotFound(LaravelApiResponse.MessageOnly("error", "Redemption not found."));
        if (redemption.Status == LoyaltyRedemption.StatusApproved)
            return BadRequest(LaravelApiResponse.MessageOnly("error", "This redemption is already approved."));

        redemption.Status = LoyaltyRedemption.StatusApproved;
        redemption.ApprovedBy = CurrentUserId();
        redemption.ApprovedAt = DateTime.UtcNow;
        redemption.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(LaravelApiResponse.MessageOnly("success", "Redemption approved."));
    }

    [RequirePermission("redemption.reject")]
    [HttpPost("{id}/reject")]
    public async Task<IActionResult> RejectRedemption(ulong id, [FromBody] RedemptionRejectRequest? request, CancellationToken cancellationToken)
    {
        var redemption = await LoadInScopeAsync(id, cancellationToken);
        if (redemption is null) return NotFound(LaravelApiResponse.MessageOnly("error", "Redemption not found."));
        if (redemption.Status == LoyaltyRedemption.StatusRejected)
            return BadRequest(LaravelApiResponse.MessageOnly("error", "This redemption is already rejected."));

        redemption.Status = LoyaltyRedemption.StatusRejected;
        redemption.RejectedBy = CurrentUserId();
        redemption.RejectedAt = DateTime.UtcNow;
        redemption.Remark = string.IsNullOrWhiteSpace(request?.Remark) ? redemption.Remark : request!.Remark!.Trim();
        redemption.UpdatedAt = DateTime.UtcNow;
        // Rejecting releases the held points automatically - the wallet/point summary excludes
        // every rejected (and deleted) redemption.
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(LaravelApiResponse.MessageOnly("success", "Redemption rejected."));
    }

    // No single [RequirePermission] here - each payout state is its own gated action
    // (redemption.hold / .success / .failed / .reopen), checked inline below.
    [HttpPost("{id}/payout")]
    public async Task<IActionResult> SetPayoutState(ulong id, [FromBody] RedemptionPayoutRequest? request, CancellationToken cancellationToken)
    {
        var redemption = await LoadInScopeAsync(id, cancellationToken);
        if (redemption is null) return NotFound(LaravelApiResponse.MessageOnly("error", "Redemption not found."));
        // Payout states only apply after approval; a pending/rejected row must be approved first.
        if (redemption.Status is LoyaltyRedemption.StatusPending or LoyaltyRedemption.StatusRejected)
            return BadRequest(LaravelApiResponse.MessageOnly("error", "Approve the redemption before setting a payout status."));

        var state = (request?.State ?? string.Empty).Trim().ToLowerInvariant();
        var permission = state switch
        {
            "hold" => "redemption.hold",
            "failed" => "redemption.failed",
            "success" => "redemption.success",
            "pending" => "redemption.reopen",
            _ => null
        };
        if (permission is null) return BadRequest(LaravelApiResponse.MessageOnly("error", "Unknown payout status."));
        if (!await ActorHasPermissionAsync(permission, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, LaravelApiResponse.MessageOnly("error", "You do not have permission for this action."));

        switch (state)
        {
            case "hold":
                if (string.IsNullOrWhiteSpace(request?.Remark)) return BadRequest(LaravelApiResponse.MessageOnly("error", "A note is required to put a redemption on hold."));
                redemption.Status = LoyaltyRedemption.StatusHold;
                redemption.Remark = request!.Remark!.Trim();
                break;
            case "failed":
                if (string.IsNullOrWhiteSpace(request?.Remark)) return BadRequest(LaravelApiResponse.MessageOnly("error", "A note is required to mark a redemption failed."));
                redemption.Status = LoyaltyRedemption.StatusFailed;
                redemption.Remark = request!.Remark!.Trim();
                break;
            case "success":
                if (string.IsNullOrWhiteSpace(request?.TransactionNo)) return BadRequest(LaravelApiResponse.MessageOnly("error", "Transaction number is required."));
                var tds = request!.TdsPercent ?? 0m;
                if (tds is < 0m or > 100m) return BadRequest(LaravelApiResponse.MessageOnly("error", "TDS % must be between 0 and 100."));
                // Final payable = redeem points minus the TDS deduction; that is what is actually paid.
                var deduction = Math.Round(redemption.Points * tds / 100m, 2, MidpointRounding.AwayFromZero);
                redemption.Status = LoyaltyRedemption.StatusSuccess;
                redemption.PayoutTransactionNo = request.TransactionNo!.Trim();
                redemption.TdsPercent = tds;
                redemption.PayoutAmount = redemption.Points - deduction;
                break;
            case "pending":
                // Re-open to pending - clear the approval/payout trail so it starts fresh.
                redemption.Status = LoyaltyRedemption.StatusPending;
                redemption.ApprovedBy = null;
                redemption.ApprovedAt = null;
                redemption.PayoutTransactionNo = null;
                redemption.PayoutAmount = null;
                break;
        }

        redemption.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(LaravelApiResponse.MessageOnly("success", "Redemption updated."));
    }

    [RequirePermission("redemption.delete")]
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRedemption(ulong id, CancellationToken cancellationToken)
    {
        var redemption = await LoadInScopeAsync(id, cancellationToken);
        if (redemption is null) return NotFound(LaravelApiResponse.MessageOnly("error", "Redemption not found."));
        // Everyone except superadmin may only delete a still-pending redemption.
        if (!IsSuperAdmin() && redemption.Status != LoyaltyRedemption.StatusPending)
            return BadRequest(LaravelApiResponse.MessageOnly("error", "Only pending redemptions can be deleted."));

        // Soft delete - the listing and the point summary both skip rows with deleted_at set,
        // so the customer's points are released.
        redemption.DeletedAt = DateTime.UtcNow;
        redemption.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(LaravelApiResponse.MessageOnly("success", "Redemption deleted."));
    }

    /// <summary>Loads a live (not-deleted) redemption. These endpoints are CRM-only - the
    /// [RequirePermission] filter 403s every non-"users" provider (a dealer's customer-app
    /// login), which is exactly the audience the listing shows every redemption to, so no extra
    /// customer-visibility filter is applied here (it would reject rows the user can see).</summary>
    private async Task<LoyaltyRedemption?> LoadInScopeAsync(ulong id, CancellationToken cancellationToken) =>
        await _db.LoyaltyRedemptions.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, cancellationToken);

    private bool IsSuperAdmin() =>
        User.Claims.Any(claim => claim.Type == ClaimTypes.Role && string.Equals(claim.Value, "superadmin", StringComparison.OrdinalIgnoreCase));

    /// <summary>Mirrors RequirePermissionFilter for an inline check: superadmin always passes,
    /// otherwise the actor's roles must grant the permission.</summary>
    private async Task<bool> ActorHasPermissionAsync(string permission, CancellationToken cancellationToken)
    {
        if (IsSuperAdmin()) return true;
        var userId = CurrentUserId();
        if (!userId.HasValue) return false;
        return await _db.ModelHasRoles
            .Where(x => x.ModelId == userId.Value && x.ModelType == LaravelModelTypes.User)
            .Join(_db.RoleHasPermissions, modelRole => modelRole.RoleId, rolePermission => rolePermission.RoleId, (_, rolePermission) => rolePermission)
            .Join(_db.Permissions, rolePermission => rolePermission.PermissionId, p => p.Id, (_, p) => p.Name)
            .AnyAsync(name => name == permission, cancellationToken);
    }

    public sealed class RedemptionRejectRequest
    {
        public string? Remark { get; set; }
    }

    public sealed class RedemptionPayoutRequest
    {
        /// <summary>One of: hold, success, failed, pending.</summary>
        public string? State { get; set; }
        public string? Remark { get; set; }
        public string? TransactionNo { get; set; }
        public decimal? TdsPercent { get; set; }
    }

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }
}
