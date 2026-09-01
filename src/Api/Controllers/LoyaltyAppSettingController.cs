using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Api.Filters;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>The Loyalty app's own settings, kept apart from the FieldKonnect ones.
///
/// Only the two force-update versions are exposed for now. The row lives in
/// loyalty_app_settings, which already carried a numeric app_version and a
/// customer_types list from the Laravel days; neither is touched here. The
/// versions are read from and written to the NVARCHAR columns V7.9 adds,
/// because a float cannot hold "1.0.1".</summary>
[ApiController]
[Route("api")]
public sealed class LoyaltyAppSettingController : ControllerBase
{
    private static readonly Regex VersionPattern = new(@"^\d+(\.\d+){0,3}$", RegexOptions.Compiled);
    private readonly AppDbContext _dbContext;

    public LoyaltyAppSettingController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>What the Loyalty app asks for on launch. Anonymous on purpose: the check
    /// runs before the user is signed in, and an app too old to sign in still has to be
    /// told to update.</summary>
    [AllowAnonymous]
    [HttpGet("loyalty/app-version")]
    [HttpGet("loyalty-app-version")]
    public async Task<IActionResult> GetAppVersion(CancellationToken cancellationToken)
    {
        var setting = await ReadSettingAsync(cancellationToken);
        return Ok(new
        {
            status = "success",
            data = new
            {
                android_version = setting?.AndroidVersion ?? string.Empty,
                ios_version = setting?.IosVersion ?? string.Empty
            }
        });
    }

    [Authorize]
    [RequirePermission("app_setting.view")]
    [HttpGet("loyalty-app-setting")]
    public async Task<IActionResult> GetSetting(CancellationToken cancellationToken)
    {
        var setting = await ReadSettingAsync(cancellationToken);
        if (setting is null)
        {
            return NotFound(new { status = "error", message = "Loyalty app settings not found." });
        }

        return Ok(new { status = "success", data = ToResponse(setting) });
    }

    [Authorize]
    [RequirePermission("app_setting.edit")]
    [HttpPost("loyalty-app-setting")]
    [HttpPut("loyalty-app-setting")]
    public async Task<IActionResult> SaveSetting([FromBody] SaveLoyaltyAppSettingRequest request, CancellationToken cancellationToken)
    {
        var androidVersion = request.AndroidVersion?.Trim();
        var iosVersion = request.IosVersion?.Trim();

        if (string.IsNullOrWhiteSpace(androidVersion) || !VersionPattern.IsMatch(androidVersion))
        {
            return UnprocessableEntity(new { status = "error", message = "Android app version is required and must be numeric, for example 1.0 or 2.3.1." });
        }
        if (!string.IsNullOrWhiteSpace(iosVersion) && !VersionPattern.IsMatch(iosVersion))
        {
            return UnprocessableEntity(new { status = "error", message = "iOS app version must be numeric, for example 1.0 or 2.3.1." });
        }

        var connection = _dbContext.Database.GetDbConnection();
        await EnsureOpenAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            ulong settingId;
            await using (var findCommand = connection.CreateCommand())
            {
                findCommand.Transaction = transaction;
                findCommand.CommandText = "SELECT TOP (1) id FROM loyalty_app_settings ORDER BY id DESC";
                var value = await findCommand.ExecuteScalarAsync(cancellationToken);
                settingId = value is null or DBNull ? 0 : Convert.ToUInt64(value);
            }

            await using var saveCommand = connection.CreateCommand();
            saveCommand.Transaction = transaction;
            if (settingId == 0)
            {
                saveCommand.CommandText = @"
                    INSERT INTO loyalty_app_settings (app_android_version, app_ios_version, created_at, updated_at)
                    OUTPUT INSERTED.id
                    VALUES (@androidVersion, @iosVersion, SYSUTCDATETIME(), SYSUTCDATETIME())";
            }
            else
            {
                saveCommand.CommandText = @"
                    UPDATE loyalty_app_settings
                    SET app_android_version = @androidVersion,
                        app_ios_version = @iosVersion,
                        updated_at = SYSUTCDATETIME()
                    WHERE id = @id;
                    SELECT @id;";
                // The id column is decimal(13,0); SQL Server has no UInt64 parameter type.
                AddParameter(saveCommand, "@id", Convert.ToDecimal(settingId));
            }

            AddParameter(saveCommand, "@androidVersion", androidVersion);
            AddParameter(saveCommand, "@iosVersion", string.IsNullOrWhiteSpace(iosVersion) ? DBNull.Value : iosVersion);
            await saveCommand.ExecuteScalarAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        var saved = await ReadSettingAsync(cancellationToken);
        return Ok(new
        {
            status = "success",
            message = "Loyalty app settings saved successfully.",
            data = saved is null ? null : ToResponse(saved)
        });
    }

    private async Task<LoyaltyAppSetting?> ReadSettingAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await EnsureOpenAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT TOP (1) id, app_android_version, app_ios_version, created_at, updated_at
            FROM loyalty_app_settings
            ORDER BY id DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new LoyaltyAppSetting(
            Convert.ToUInt64(reader.GetValue(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4));
    }

    private static object ToResponse(LoyaltyAppSetting setting) => new
    {
        id = setting.Id,
        android_version = setting.AndroidVersion ?? string.Empty,
        ios_version = setting.IosVersion ?? string.Empty,
        created_at = setting.CreatedAt,
        updated_at = setting.UpdatedAt
    };

    private static async Task EnsureOpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record LoyaltyAppSetting(ulong Id, string? AndroidVersion, string? IosVersion, DateTime? CreatedAt, DateTime? UpdatedAt);
}

public sealed class SaveLoyaltyAppSettingRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("android_version")]
    public string? AndroidVersion { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("ios_version")]
    public string? IosVersion { get; init; }
}
