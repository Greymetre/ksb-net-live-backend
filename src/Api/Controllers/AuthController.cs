using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Application.DTOs.Auth;
using Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly Api.Services.UserPasswordResetService _passwordReset;

    public AuthController(IAuthService authService, Api.Services.UserPasswordResetService passwordReset)
    {
        _authService = authService;
        _passwordReset = passwordReset;
    }

    /// <summary>CRM "Forgot password": mails a 6-digit code. Answers the same way whether
    /// or not the email belongs to an account.</summary>
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await _passwordReset.RequestAsync(request.Email, cancellationToken);
        return Ok(new { status = "success", message = Api.Services.UserPasswordResetService.RequestAcceptedMessage });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await _passwordReset.ResetAsync(request.Email, request.Code, request.Password, request.PasswordConfirmation, cancellationToken);
        return Ok(new { status = "success", message = "Your password has been reset. You can now log in with the new password." });
    }

    public sealed class ForgotPasswordRequest
    {
        public string? Email { get; init; }
    }

    public sealed class ResetPasswordRequest
    {
        public string? Email { get; init; }
        public string? Code { get; init; }
        public string? Password { get; init; }
        public string? PasswordConfirmation { get; init; }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _authService.LoginAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status200OK, response);
    }

    [HttpPost("fieldkonnect/login")]
    [HttpPost("field-konnect/login")]
    [HttpPost("fieldKonnectLogin")]
    public async Task<IActionResult> FieldKonnectLogin([FromBody] LoginRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _authService.FieldKonnectLoginAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status200OK, response);
    }

    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] SignupRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _authService.SignupAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("customerSignup")]
    public async Task<IActionResult> CustomerSignup([FromBody] CustomerSignupRequestDto request, CancellationToken cancellationToken)
    {
        var response = await _authService.CustomerSignupAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [Authorize]
    [AcceptVerbs("GET", "POST")]
    [Route("getProfile")]
    [Route("fieldkonnect/profile")]
    [Route("field-konnect/profile")]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var provider = User.FindFirstValue("provider") ?? "users";
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0";
        if (provider != "users" || !ulong.TryParse(subject, out var userId))
        {
            return Unauthorized(new { status = "error", message = "Unauthenticated." });
        }

        var response = await _authService.GetUserProfileAsync(userId, cancellationToken);
        return Ok(response);
    }

    [Authorize]
    [AcceptVerbs("GET", "POST")]
    [Route("logout")]
    [Route("customer/logout")]
    [Route("fieldkonnect/logout")]
    [Route("field-konnect/logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var tokenId = User.FindFirstValue(JwtRegisteredClaimNames.Jti) ?? string.Empty;
        var provider = User.FindFirstValue("provider") ?? "users";
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0";
        var response = await _authService.LogoutAsync(tokenId, provider, ulong.Parse(subject), cancellationToken);
        return Ok(response);
    }
}
