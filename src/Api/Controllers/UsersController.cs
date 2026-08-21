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
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    [Authorize]
    [RequirePermission("user_access")]
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
    [RequirePermission("user_access")]
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

    [Authorize]
    [HttpGet("users/options")]
    public async Task<IActionResult> GetUserOptions(CancellationToken cancellationToken)
    {
        var response = await _userService.GetUserOptionsAsync(CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    [Authorize]
    [RequirePermission("user_create")]
    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] UserRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _userService.CreateUserAsync(request, CurrentUserId(), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [Authorize]
    [RequirePermission("user_edit")]
    [HttpPut("users/{id}")]
    [HttpPatch("users/{id}")]
    public async Task<IActionResult> UpdateUser(ulong id, [FromBody] UserRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _userService.UpdateUserAsync(id, request, CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    [Authorize]
    [RequirePermission("user_active")]
    [HttpPatch("users/{id}/status")]
    public async Task<IActionResult> SetUserActive(ulong id, [FromBody] UserRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _userService.SetUserActiveAsync(id, request.Active, CurrentUserId(), cancellationToken);
        return Ok(response);
    }

    [Authorize]
    [RequirePermission("user_delete")]
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
    [RequirePermission("user_download")]
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
    [RequirePermission("user_template")]
    [HttpGet("users-template")]
    [HttpGet("users/template")]
    public async Task<IActionResult> UserTemplate(CancellationToken cancellationToken)
    {
        var file = await _userService.GetUserTemplateAsync(cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [Authorize]
    [RequirePermission("user_upload")]
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
