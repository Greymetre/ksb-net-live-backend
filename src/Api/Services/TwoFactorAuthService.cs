using System.Security.Cryptography;
using System.Text;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Shared.Exceptions;

namespace Api.Services;

/// <summary>
/// CRM two-factor sign-in with a TOTP authenticator app (Google Authenticator, Authy, ...).
///
/// First time: the user opens setup, scans a QR (an otpauth:// URI) into their app, and types a
/// code back to prove the app is paired - only then is 2FA turned on and the shared secret kept.
/// After that, a web sign-in that passes the password asks for the current 6-digit code from the
/// app. The field apps (mobile) never go through this.
///
///   users.two_factor_enabled : the on/off switch (set only once a setup code is confirmed).
///   users.two_factor_secret  : the confirmed Base32 TOTP secret the app and the server share.
///   users.two_factor_state   : the PENDING secret during setup, "setup:{base32}", until confirmed.
///
/// The secret is generated on the server and shown once (as the QR and as text); it is never sent
/// to any outside service - the QR image is drawn here, locally.
/// </summary>
public sealed class TwoFactorAuthService
{
    private const string Issuer = "KSB FieldKonnect";
    private const string PendingPrefix = "setup:";
    private const int SecretBytes = 20;   // 160-bit, the authenticator-app norm
    private const int Digits = 6;
    private const int PeriodSeconds = 30;
    private const int WindowSteps = 1;    // accept the previous/next step for clock skew

    private readonly AppDbContext _dbContext;

    public TwoFactorAuthService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public sealed record SetupInfo(string Secret, string OtpauthUri, string QrDataUrl, string Account);

    /// <summary>Starts (or restarts) enrolment: makes a fresh secret, keeps it pending, and returns
    /// the QR + secret for the user to add to their authenticator app.</summary>
    public async Task<SetupInfo> BeginSetupAsync(ulong userId, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new LaravelHttpException(StatusCodes.Status404NotFound, "User not found.");

        var secret = Base32Encode(RandomNumberGenerator.GetBytes(SecretBytes));
        user.TwoFactorState = PendingPrefix + secret;   // not yet enabled - confirm first
        await _dbContext.SaveChangesAsync(cancellationToken);

        var account = string.IsNullOrWhiteSpace(user.Email)
            ? (string.IsNullOrWhiteSpace(user.Mobile) ? user.Name : user.Mobile!)
            : user.Email!;
        var uri = BuildOtpauthUri(secret, account);
        return new SetupInfo(secret, uri, BuildQrDataUrl(uri), account);
    }

    /// <summary>Finishes enrolment: the code must match the pending secret, which then becomes the
    /// live secret and 2FA is switched on.</summary>
    public async Task ConfirmSetupAsync(ulong userId, string? code, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new LaravelHttpException(StatusCodes.Status404NotFound, "User not found.");

        var pending = ReadPendingSecret(user.TwoFactorState)
            ?? throw new LaravelHttpException(StatusCodes.Status400BadRequest, "Start the setup again, then scan the QR.");

        if (!VerifyCode(pending, code))
        {
            throw new LaravelHttpException(StatusCodes.Status400BadRequest, "That code is incorrect. Check the time on your phone and try the current code.");
        }

        user.TwoFactorSecret = pending;
        user.TwoFactorEnabled = true;
        user.TwoFactorState = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Turns 2FA off and forgets the secret.</summary>
    public async Task DisableAsync(ulong userId, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new LaravelHttpException(StatusCodes.Status404NotFound, "User not found.");

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.TwoFactorState = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Checks the authenticator code at sign-in against the user's live secret.</summary>
    public async Task VerifyLoginAsync(string username, string? code, CancellationToken cancellationToken)
    {
        var user = await FindByUsernameAsync(username, cancellationToken);
        if (user is null || !user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TwoFactorSecret))
        {
            throw new LaravelHttpException(StatusCodes.Status400BadRequest, "Two-factor is not set up for this account. Sign in again.");
        }
        if (!VerifyCode(user.TwoFactorSecret!, code))
        {
            throw new LaravelHttpException(StatusCodes.Status400BadRequest, "The code is incorrect. Enter the current code from your authenticator app.");
        }
    }

    // ------------------------------------------------------------------ TOTP (RFC 6238) ---

    private static bool VerifyCode(string base32Secret, string? code)
    {
        var entered = (code ?? string.Empty).Trim();
        if (entered.Length != Digits || !entered.All(char.IsDigit)) return false;

        var key = Base32Decode(base32Secret);
        if (key.Length == 0) return false;

        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PeriodSeconds;
        for (var offset = -WindowSteps; offset <= WindowSteps; offset++)
        {
            if (FixedEquals(entered, GenerateCode(key, step + offset))) return true;
        }
        return false;
    }

    private static string GenerateCode(byte[] key, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counter);
        var o = hash[^1] & 0x0f;
        var binary = ((hash[o] & 0x7f) << 24) | ((hash[o + 1] & 0xff) << 16) | ((hash[o + 2] & 0xff) << 8) | (hash[o + 3] & 0xff);
        return (binary % (int)Math.Pow(10, Digits)).ToString(new string('0', Digits));
    }

    // Length-constant compare so a wrong code cannot be timed out digit by digit.
    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static string BuildOtpauthUri(string secret, string account)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{account}");
        var issuer = Uri.EscapeDataString(Issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    private static string BuildQrDataUrl(string otpauthUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(otpauthUri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static string? ReadPendingSecret(string? state) =>
        !string.IsNullOrWhiteSpace(state) && state.StartsWith(PendingPrefix, StringComparison.Ordinal)
            ? state[PendingPrefix.Length..]
            : null;

    private Task<Domain.Entities.User?> FindByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        var value = username.Trim();
        return _dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.DeletedAt == null && (x.Mobile == value || x.Email == value), cancellationToken);
    }

    // ------------------------------------------------------------------- Base32 (RFC 4648) ---

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Base32Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        var cleaned = value.Trim().TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(cleaned.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in cleaned)
        {
            var index = Base32Alphabet.IndexOf(c);
            if (index < 0) return [];
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xff));
            }
        }
        return [.. bytes];
    }
}
