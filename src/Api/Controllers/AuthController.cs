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
    private readonly Api.Services.TwoFactorAuthService _twoFactor;

    public AuthController(IAuthService authService, Api.Services.UserPasswordResetService passwordReset, Api.Services.TwoFactorAuthService twoFactor)
    {
        _authService = authService;
        _passwordReset = passwordReset;
        _twoFactor = twoFactor;
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

        // Web user with 2FA on: the password passed, now mail the code and let the client post it
        // back to verify-2fa with the code from their app. No token has been issued yet.
        if (response.Status == Application.Services.AuthService.TwoFactorRequiredStatus)
        {
            return Ok(new
            {
                status = "mfa_required",
                username = request.Username!.Trim(),
                message = "Enter the 6-digit code from your authenticator app."
            });
        }

        return StatusCode(StatusCodes.Status200OK, response);
    }

    /// <summary>Step two of a 2FA web sign-in: check the authenticator code, then issue the session.</summary>
    [HttpPost("verify-2fa")]
    public async Task<IActionResult> VerifyTwoFactor([FromBody] TwoFactorVerifyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(new { status = "error", message = "Sign in again." });
        }
        await _twoFactor.VerifyLoginAsync(request.Username, request.Code, cancellationToken);
        var response = await _authService.CompleteTwoFactorLoginAsync(request.Username!, cancellationToken);
        return StatusCode(StatusCodes.Status200OK, response);
    }

    /// <summary>Starts authenticator-app enrolment: returns the QR and secret to add to the app.</summary>
    [Authorize]
    [HttpPost("two-factor/setup")]
    public async Task<IActionResult> SetupTwoFactor(CancellationToken cancellationToken)
    {
        if (!TryCurrentUserId(out var userId))
        {
            return Unauthorized(new { status = "error", message = "Unauthenticated." });
        }
        var info = await _twoFactor.BeginSetupAsync(userId, cancellationToken);
        return Ok(new
        {
            status = "success",
            secret = info.Secret,
            otpauth_uri = info.OtpauthUri,
            qr_data_url = info.QrDataUrl,
            account = info.Account,
            message = "Scan the QR in your authenticator app, then enter the 6-digit code to finish."
        });
    }

    /// <summary>Finishes enrolment: the code proves the app is paired, then 2FA is switched on.</summary>
    [Authorize]
    [HttpPost("two-factor/confirm")]
    public async Task<IActionResult> ConfirmTwoFactor([FromBody] TwoFactorVerifyRequest request, CancellationToken cancellationToken)
    {
        if (!TryCurrentUserId(out var userId))
        {
            return Unauthorized(new { status = "error", message = "Unauthenticated." });
        }
        await _twoFactor.ConfirmSetupAsync(userId, request.Code, cancellationToken);
        return Ok(new
        {
            status = "success",
            two_factor_enabled = true,
            message = "Two-factor authentication is on. You'll enter a code from your app at your next sign-in."
        });
    }

    /// <summary>Turns the signed-in user's 2FA off and forgets the secret.</summary>
    [Authorize]
    [HttpPost("two-factor/disable")]
    public async Task<IActionResult> DisableTwoFactor(CancellationToken cancellationToken)
    {
        if (!TryCurrentUserId(out var userId))
        {
            return Unauthorized(new { status = "error", message = "Unauthenticated." });
        }
        await _twoFactor.DisableAsync(userId, cancellationToken);
        return Ok(new { status = "success", two_factor_enabled = false, message = "Two-factor authentication is off." });
    }

    private bool TryCurrentUserId(out ulong userId)
    {
        userId = 0;
        var provider = User.FindFirstValue("provider") ?? "users";
        var subject = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0";
        return provider == "users" && ulong.TryParse(subject, out userId);
    }

    public sealed class TwoFactorVerifyRequest
    {
        public string? Username { get; init; }
        public string? Code { get; init; }
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
