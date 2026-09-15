using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using Application.Interfaces.Services;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Exceptions;

namespace Api.Services;

/// <summary>
/// Password reset for CRM users, by a six-digit code sent to the user's email.
///
/// The pending reset is kept in users.remember_token, a column nothing else reads, so the
/// feature needs no schema change: "reset:{expires unix}:{failed attempts}:{bcrypt of code}".
/// The code itself is never stored and never returned by the API - not even when email is
/// switched off on a server, because a page that shows the code would let anyone reset
/// anyone's password.
/// </summary>
public sealed class UserPasswordResetService
{
    private const string Prefix = "reset";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    private const int MaxFailedAttempts = 5;
    private const int MinPasswordLength = 6;

    /// <summary>The same answer whether or not the address belongs to an account, so the
    /// form cannot be used to find out which emails have one.</summary>
    public const string RequestAcceptedMessage =
        "If an active account uses this email, a 6-digit code has been sent to it. The code expires in 15 minutes.";

    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISmtpEmailSender _emailSender;
    private readonly ILogger<UserPasswordResetService> _logger;

    public UserPasswordResetService(AppDbContext dbContext, IPasswordHasher passwordHasher, ISmtpEmailSender emailSender, ILogger<UserPasswordResetService> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task RequestAsync(string? email, CancellationToken cancellationToken)
    {
        var address = NormalizeEmail(email) ?? throw BadRequest("Enter a valid email address.");
        if (_emailSender.BypassEnabled)
        {
            throw new LaravelHttpException(StatusCodes.Status503ServiceUnavailable,
                "Password reset by email is not available on this server. Please contact your administrator.");
        }

        var user = await FindUserAsync(address, cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.Email)) return;

        var now = DateTimeOffset.UtcNow;
        var pending = ReadState(user.RememberToken);
        // A code sent a moment ago is still on its way; sending another would only make the
        // first one wrong by the time it arrives.
        if (pending is not null && pending.ExpiresAt - CodeLifetime > now - ResendCooldown) return;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        user.RememberToken = WriteState(new ResetState(now.Add(CodeLifetime), 0, _passwordHasher.Hash(code)));
        await _dbContext.SaveChangesAsync(cancellationToken);

        var name = string.IsNullOrWhiteSpace(user.Name) ? "there" : user.Name.Trim();
        var body =
            $"Hello {name},\n\n" +
            $"Use this code to reset your FieldKonnect password: {code}\n\n" +
            "The code expires in 15 minutes. If you did not ask to reset your password, you can ignore this email - your password has not changed.\n\n" +
            "KSB FieldKonnect";
        try
        {
            await _emailSender.SendAsync(user.Email, "Reset your FieldKonnect password", body, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Nobody can use a code that never arrived, so take it back and let them try again
            // straight away instead of waiting out the cooldown.
            user.RememberToken = null;
            await _dbContext.SaveChangesAsync(CancellationToken.None);
            _logger.LogError(exception, "Password reset email could not be sent. UserId={UserId}", user.Id);
            throw new LaravelHttpException(StatusCodes.Status503ServiceUnavailable,
                $"We could not send the email. {SmtpEmailSender.Describe(exception)}");
        }
    }

    public async Task ResetAsync(string? email, string? code, string? password, string? passwordConfirmation, CancellationToken cancellationToken)
    {
        var address = NormalizeEmail(email) ?? throw BadRequest("Enter a valid email address.");
        var trimmedCode = code?.Trim() ?? string.Empty;
        if (trimmedCode.Length != 6 || !trimmedCode.All(char.IsDigit)) throw BadRequest("Enter the 6-digit code from the email.");
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw BadRequest($"Password must be at least {MinPasswordLength} characters.");
        if (passwordConfirmation is not null && passwordConfirmation != password)
            throw BadRequest("The two passwords do not match.");

        var user = await FindUserAsync(address, cancellationToken);
        var state = user is null ? null : ReadState(user.RememberToken);
        if (user is null || state is null || state.ExpiresAt <= DateTimeOffset.UtcNow || state.FailedAttempts >= MaxFailedAttempts)
        {
            if (user is not null && state is not null)
            {
                user.RememberToken = null;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            throw BadRequest("The code is invalid or has expired. Request a new code.");
        }

        if (!_passwordHasher.Verify(trimmedCode, state.CodeHash))
        {
            var attempts = state.FailedAttempts + 1;
            user.RememberToken = attempts >= MaxFailedAttempts ? null : WriteState(state with { FailedAttempts = attempts });
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw BadRequest(attempts >= MaxFailedAttempts
                ? "Too many incorrect attempts. Request a new code."
                : "The code is incorrect.");
        }

        // Written the way the user screen writes a password, so the two stay in step.
        user.Password = _passwordHasher.Hash(password);
        user.PasswordString = password;
        user.RememberToken = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Anyone signed in with the old password is signed out. Only this user's CRM and
        // field app sessions - customer tokens share the table and can share an id.
        await _dbContext.OAuthAccessTokens
            .Where(x => x.UserId == user.Id && !x.Revoked && x.Name != null && x.Name.StartsWith("users-"))
            .ExecuteUpdateAsync(setter => setter.SetProperty(x => x.Revoked, true), cancellationToken);
    }

    private Task<Domain.Entities.User?> FindUserAsync(string email, CancellationToken cancellationToken) =>
        _dbContext.Users.FirstOrDefaultAsync(x => x.Active == "Y" && x.Email != null && x.Email.ToLower() == email, cancellationToken);

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToLowerInvariant();
        try
        {
            return new MailAddress(normalized).Address == normalized ? normalized : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static LaravelHttpException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);

    private sealed record ResetState(DateTimeOffset ExpiresAt, int FailedAttempts, string CodeHash);

    private static string WriteState(ResetState state) =>
        string.Join(':', Prefix, state.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            state.FailedAttempts.ToString(CultureInfo.InvariantCulture), state.CodeHash);

    private static ResetState? ReadState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // The bcrypt hash has ':' nowhere in it, but split only three times so it could.
        var parts = value.Split(':', 4);
        if (parts.Length != 4 || parts[0] != Prefix) return null;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires)) return null;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts)) return null;
        return new ResetState(DateTimeOffset.FromUnixTimeSeconds(expires), attempts, parts[3]);
    }
}
