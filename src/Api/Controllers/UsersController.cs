using Api.Filters;
using Application.DTOs.Users;
using Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Api.Controllers;

[ApiController]
[Route("api")]
public sealed class UsersController : ControllerBase
{
    private static readonly string[] AllowedPhotoExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private const long MaxPhotoBytes = 5 * 1024 * 1024;

    private readonly IUserService _userService;
    private readonly IWebHostEnvironment _environment;

    public UsersController(IUserService userService, IWebHostEnvironment environment)
    {
        _userService = userService;
        _environment = environment;
    }

    [Authorize]
    [RequirePermission("user.view")]
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? search,
        [FromQuery(Name = "user_type")] string? userType,
        [FromQuery] string? active,
        [FromQuery(Name = "division_id")] ulong? divisionId,
        [FromQuery(Name = "branch_id")] string? branchId,
        [FromQuery(Name = "department_id")] ulong? departmentId,
        CancellationToken cancellationToken)
    {
        var response = await _userService.GetUsersAsync(new UserListFiltersDto
        {
            ActorUserId = CurrentUserId(),
            Search = search,
            UserType = userType,
            Active = active,
            DivisionId = divisionId,
            BranchId = branchId,
            DepartmentId = departmentId
        }, cancellationToken);
        return Ok(response);
    }

    [Authorize]
    [RequirePermission("user.view")]
    [HttpGet("users/{id}")]
    public async Task<IActionResult> GetUser(ulong id, CancellationToken cancellationToken)
    {
        var response = await _userService.GetUserAsync(id, CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    /// <summary>Profile screen for whoever is signed in. Deliberately has no
    /// RequirePermission - a user always reads their own record, and most roles
    /// (dealers included) do not hold user_access.</summary>
    [Authorize]
    [HttpGet("profile/details")]
    public async Task<IActionResult> GetMyProfile(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { status = "error", message = "Unauthenticated." });
        }

        var response = await _userService.GetMyProfileAsync(userId.Value, cancellationToken);
        return Ok(response);
    }

    /// <summary>Profile picture for whoever is signed in. Like the profile read above
    /// it carries no RequirePermission - every role, dealers included, may replace
    /// their own photo, and nothing except that one column is written.</summary>
    [Authorize]
    [HttpPost("profile/photo")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UpdateMyPhoto([FromForm(Name = "profile_image")] IFormFile? profileImage, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { status = "error", message = "Unauthenticated." });
        }

        if (profileImage is null || profileImage.Length == 0)
        {
            return BadRequest(new { status = "error", message = "Please choose an image to upload." });
        }

        if (profileImage.Length > MaxPhotoBytes)
        {
            return BadRequest(new { status = "error", message = "Profile picture must be 5 MB or smaller." });
        }

        var extension = Path.GetExtension(profileImage.FileName).ToLowerInvariant();
        if (!AllowedPhotoExtensions.Contains(extension) || !profileImage.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { status = "error", message = "Only JPG, PNG or WEBP images are allowed." });
        }

        // Stored under wwwroot/uploads so the mounted media volume keeps the file
        // across redeploys, same as invoice attachments and product images.
        var root = Path.Combine(_environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"), "uploads", "profile-images");
        Directory.CreateDirectory(root);
        var fileName = $"{userId.Value}-{Guid.NewGuid():N}{extension}";
        await using (var stream = System.IO.File.Create(Path.Combine(root, fileName)))
        {
            await profileImage.CopyToAsync(stream, cancellationToken);
        }

        var response = await _userService.UpdateMyProfileImageAsync(userId.Value, $"/uploads/profile-images/{fileName}", cancellationToken);
        return Ok(response);
    }

    [Authorize]
    [HttpGet("users/options")]
    public async Task<IActionResult> GetUserOptions(CancellationToken cancellationToken)
    {
        var response = await _userService.GetUserOptionsAsync(CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    [Authorize]
    [RequirePermission("user.create")]
    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] UserRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _userService.CreateUserAsync(request, CurrentUserId(), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [Authorize]
    [RequirePermission("user.edit")]
    [HttpPut("users/{id}")]
    [HttpPatch("users/{id}")]
    public async Task<IActionResult> UpdateUser(ulong id, [FromBody] UserRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _userService.UpdateUserAsync(id, request, CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    [Authorize]
    [RequirePermission("user.active")]
    [HttpPatch("users/{id}/status")]
    public async Task<IActionResult> SetUserActive(ulong id, [FromBody] UserRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _userService.SetUserActiveAsync(id, request.Active, CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    [Authorize]
    [RequirePermission("user.delete")]
    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(ulong id, CancellationToken cancellationToken)
    {
        var response = await _userService.DeleteUserAsync(id, CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    // Laravel mobile-app compatibility route. The authenticated user may only
    // delete their own account through this endpoint.
    [Authorize]
    [HttpPost("delete-user")]
    public async Task<IActionResult> DeleteMobileUser([FromBody] DeleteMobileUserRequest request, CancellationToken cancellationToken)
    {
        var actorId = CurrentUserId();
        if (!actorId.HasValue)
            return Unauthorized(new { status = false, message = "Unauthenticated." });
        if (!request.UserId.HasValue)
            return UnprocessableEntity(new { status = false, message = "The user_id field is required." });
        if (request.UserId.Value != actorId.Value)
            return StatusCode(StatusCodes.Status403Forbidden, new { status = false, message = "You can only delete your own account." });

        await _userService.DeleteUserAsync(actorId.Value, actorId.Value, cancellationToken);
        return Ok(new { status = true, message = "User deleted successfully" });
    }

    [Authorize]
    [RequirePermission("user.export")]
    [HttpGet("users-download")]
    [HttpGet("users/export")]
    public async Task<IActionResult> ExportUsers([FromQuery(Name = "user_type")] string? userType, [FromQuery] string? active, [FromQuery(Name = "division_id")] ulong? divisionId, [FromQuery(Name = "branch_id")] string? branchId, [FromQuery(Name = "department_id")] ulong? departmentId, CancellationToken cancellationToken)
    {
        var file = await _userService.ExportUsersAsync(new UserExportFiltersDto
        {
            ActorUserId = CurrentUserId(),
            UserType = userType,
            Active = active,
            DivisionId = divisionId,
            BranchId = branchId,
            DepartmentId = departmentId
        }, cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [Authorize]
    [RequirePermission("user.template")]
    [HttpGet("users-template")]
    [HttpGet("users/template")]
    public async Task<IActionResult> UserTemplate(CancellationToken cancellationToken)
    {
        var file = await _userService.GetUserTemplateAsync(cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [Authorize]
    [RequirePermission("user.import")]
    [HttpPost("users-upload")]
    [HttpPost("users/upload")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadUsers(IFormFile import_file, CancellationToken cancellationToken)
    {
        await using var stream = import_file.OpenReadStream();
        var response = await _userService.UploadUsersAsync(stream, CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    private ulong? CurrentUserId()
    {
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return ulong.TryParse(subject, out var userId) ? userId : null;
    }
}

public sealed class DeleteMobileUserRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("user_id")]
    public ulong? UserId { get; set; }
}
