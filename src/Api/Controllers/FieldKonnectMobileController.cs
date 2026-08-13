using System.Data;
using System.Text.RegularExpressions;
using System.Text.Json;
using Api.Filters;
using Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api")]
public sealed class FieldKonnectMobileController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public FieldKonnectMobileController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [AllowAnonymous]
    [HttpGet("get-field-connet-version")]
    [HttpGet("fieldkonnect/version")]
    [HttpGet("field-konnect/version")]
    public async Task<IActionResult> GetVersion(CancellationToken cancellationToken)
    {
        var setting = await ReadLatestSetting(cancellationToken);
        var media = setting is null
            ? Array.Empty<object>()
            : await ReadSettingMedia(setting.Id, cancellationToken);

        return Ok(new
        {
            status = "success",
            message = "Data retrieved successfully.",
            data = new
            {
                app_version = setting?.AppVersion ?? string.Empty,
                android_version = setting?.AppVersion ?? string.Empty,
                ios_version = setting?.AppIosVersion ?? string.Empty,
                media
            }
        });
    }

    [AllowAnonymous]
    [HttpGet("getAppVersion")]
    [HttpGet("fieldkonnect/app-version")]
    [HttpGet("field-konnect/app-version")]
    public async Task<IActionResult> GetAppVersion(CancellationToken cancellationToken)
    {
        var setting = await ReadLatestSetting(cancellationToken);
        if (setting is null)
        {
            return NotFound(new { status = "error", message = "Settings not found." });
        }

        return Ok(new
        {
            status = "success",
            data = new
            {
                android_version = setting.AppVersion ?? string.Empty,
                ios_version = setting.AppIosVersion ?? string.Empty
            }
        });
    }

    [AllowAnonymous]
    [HttpGet("getsettings")]
    [HttpGet("fieldkonnect/settings")]
    [HttpGet("field-konnect/settings")]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        var setting = await ReadLatestSetting(cancellationToken);
        return Ok(new
        {
            status = "success",
            data = new
            {
                app_version = setting?.AppVersion ?? string.Empty,
                android_version = setting?.AppVersion ?? string.Empty,
                ios_version = setting?.AppIosVersion ?? string.Empty,
                order_discount_limit = setting?.OrderDiscountLimit
            }
        });
    }

    [Authorize]
    [RequirePermission("loyalty_app_setting_access", "field_konnect_app_setting_access")]
    [HttpGet("field-konnect-app-setting")]
    public async Task<IActionResult> GetAdminSetting(CancellationToken cancellationToken)
    {
        var setting = await ReadLatestSetting(cancellationToken);
        if (setting is null)
        {
            return NotFound(new { status = "error", message = "FieldKonnect app settings not found." });
        }

        return Ok(new
        {
            status = "success",
            data = ToAdminResponse(setting)
        });
    }

    [Authorize]
    [RequirePermission("loyalty_app_setting_access", "field_konnect_app_setting_access")]
    [HttpPost("field-konnect-app-setting")]
    [HttpPut("field-konnect-app-setting")]
    public async Task<IActionResult> SaveAdminSetting([FromBody] SaveFieldKonnectSettingRequest request, CancellationToken cancellationToken)
    {
        var androidVersion = request.AndroidVersion?.Trim();
        var iosVersion = request.IosVersion?.Trim();
        if (!IsValidVersion(androidVersion))
        {
            return UnprocessableEntity(new { status = "error", message = "Android app version is required and must be numeric, for example 1.3 or 16.0." });
        }
        if (!string.IsNullOrWhiteSpace(iosVersion) && !IsValidVersion(iosVersion))
        {
            return UnprocessableEntity(new { status = "error", message = "iOS app version must be numeric, for example 1.1 or 2.3." });
        }
        if (request.OrderDiscountLimit is < 0 or > 100)
        {
            return UnprocessableEntity(new { status = "error", message = "Order discount limit must be between 0 and 100." });
        }

        var connection = _dbContext.Database.GetDbConnection();
        await EnsureOpen(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            ulong settingId;
            await using (var findCommand = connection.CreateCommand())
            {
                findCommand.Transaction = transaction;
                findCommand.CommandText = "SELECT TOP (1) id FROM field_konnect_app_settings ORDER BY id DESC";
                var value = await findCommand.ExecuteScalarAsync(cancellationToken);
                settingId = value is null or DBNull ? 0 : Convert.ToUInt64(value);
            }

            await using var saveCommand = connection.CreateCommand();
            saveCommand.Transaction = transaction;
            if (settingId == 0)
            {
                saveCommand.CommandText = @"
                    INSERT INTO field_konnect_app_settings (app_version, app_ios_version, order_discount_limit, created_at, updated_at)
                    OUTPUT INSERTED.id
                    VALUES (@androidVersion, @iosVersion, @discountLimit, SYSUTCDATETIME(), SYSUTCDATETIME())";
            }
            else
            {
                saveCommand.CommandText = @"
                    UPDATE field_konnect_app_settings
                    SET app_version = @androidVersion,
                        app_ios_version = @iosVersion,
                        order_discount_limit = @discountLimit,
                        updated_at = SYSUTCDATETIME()
                    WHERE id = @id;
                    SELECT @id;";
                AddParameter(saveCommand, "@id", settingId);
            }

            AddParameter(saveCommand, "@androidVersion", androidVersion!);
            AddParameter(saveCommand, "@iosVersion", string.IsNullOrWhiteSpace(iosVersion) ? DBNull.Value : iosVersion);
            AddParameter(saveCommand, "@discountLimit", request.OrderDiscountLimit is null ? DBNull.Value : request.OrderDiscountLimit.Value);
            settingId = Convert.ToUInt64(await saveCommand.ExecuteScalarAsync(cancellationToken));
            await transaction.CommitAsync(cancellationToken);

            var setting = await ReadLatestSetting(cancellationToken);
            return Ok(new
            {
                status = "success",
                message = "FieldKonnect app settings saved successfully.",
                data = setting is null ? null : ToAdminResponse(setting)
            });
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    [Authorize]
    [AcceptVerbs("GET", "POST")]
    [Route("getOrderDiscountLimit")]
    [Route("fieldkonnect/order-discount-limit")]
    [Route("field-konnect/order-discount-limit")]
    public async Task<IActionResult> GetOrderDiscountLimit(CancellationToken cancellationToken)
    {
        var setting = await ReadLatestSetting(cancellationToken);
        return Ok(new
        {
            status = "success",
            order_discount_limit = setting?.OrderDiscountLimit
        });
    }

    private async Task<FieldKonnectSetting?> ReadLatestSetting(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await EnsureOpen(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = SqlServerSql.Normalize("SELECT id, app_version, order_discount_limit, created_at, updated_at FROM field_konnect_app_settings ORDER BY id DESC LIMIT 1");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var setting = new FieldKonnectSetting
        {
            Id = ReadUInt64(reader, "id"),
            AppVersion = ReadString(reader, "app_version"),
            OrderDiscountLimit = ReadNullableInt32(reader, "order_discount_limit"),
            CreatedAt = ReadNullableDateTime(reader, "created_at"),
            UpdatedAt = ReadNullableDateTime(reader, "updated_at")
        };

        await reader.CloseAsync();
        setting.AppIosVersion = await ReadOptionalIosVersion(setting.Id, cancellationToken);
        return setting;
    }

    private async Task<string?> ReadOptionalIosVersion(ulong settingId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await EnsureOpen(connection, cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = SqlServerSql.Normalize("SELECT app_ios_version FROM field_konnect_app_settings WHERE id = @id LIMIT 1");
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = SqlServerSql.ParameterValue(settingId);
            command.Parameters.Add(parameter);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is DBNull or null ? null : Convert.ToString(value);
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<object>> ReadSettingMedia(ulong settingId, CancellationToken cancellationToken)
    {
        var modelTypes = new[]
        {
            "App\\Models\\FieldKonnectAppSetting",
            "App\\\\Models\\\\FieldKonnectAppSetting"
        };

        var mediaRows = await _dbContext.Media
            .Where(media => media.ModelId == settingId
                && modelTypes.Contains(media.ModelType)
                && media.CollectionName == "product_catalogue")
            .OrderBy(media => media.OrderColumn)
            .ThenBy(media => media.Id)
            .ToListAsync(cancellationToken);

        return mediaRows
            .Select(media => new
            {
                id = media.Id,
                model_type = media.ModelType,
                model_id = media.ModelId,
                uuid = media.Uuid,
                collection_name = media.CollectionName,
                name = media.Name,
                file_name = media.FileName,
                mime_type = media.MimeType,
                disk = media.Disk,
                size = media.Size,
                manipulations = ParseJson(media.Manipulations),
                custom_properties = ParseJson(media.CustomProperties),
                generated_conversions = ParseJson(media.GeneratedConversions),
                responsive_images = ParseJson(media.ResponsiveImages),
                order_column = media.OrderColumn,
                created_at = media.CreatedAt,
                updated_at = media.UpdatedAt,
                original_url = $"/storage/{settingId}/{media.FileName}"
            })
            .Cast<object>()
            .ToList();
    }

    private static async Task EnsureOpen(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
    }

    private static object ToAdminResponse(FieldKonnectSetting setting) => new
    {
        id = setting.Id,
        android_version = setting.AppVersion ?? string.Empty,
        ios_version = setting.AppIosVersion ?? string.Empty,
        order_discount_limit = setting.OrderDiscountLimit,
        created_at = setting.CreatedAt,
        updated_at = setting.UpdatedAt
    };

    private static bool IsValidVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 255
        && Regex.IsMatch(value, @"^\d+(\.\d+){0,3}$", RegexOptions.CultureInvariant);

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        // Microsoft.Data.SqlClient does not support unsigned CLR parameter
        // types (for example UInt64). Normalize legacy MySQL unsigned values
        // before binding them to SQL Server parameters.
        parameter.Value = SqlServerSql.ParameterValue(value);
        command.Parameters.Add(parameter);
    }

    private static ulong ReadUInt64(IDataRecord record, string name)
    {
        var ordinal = record.GetOrdinal(name);
        return record.IsDBNull(ordinal) ? 0 : Convert.ToUInt64(record.GetValue(ordinal));
    }

    private static int? ReadNullableInt32(IDataRecord record, string name)
    {
        var ordinal = record.GetOrdinal(name);
        return record.IsDBNull(ordinal) ? null : Convert.ToInt32(record.GetValue(ordinal));
    }

    private static string? ReadString(IDataRecord record, string name)
    {
        var ordinal = record.GetOrdinal(name);
        return record.IsDBNull(ordinal) ? null : Convert.ToString(record.GetValue(ordinal));
    }

    private static DateTime? ReadNullableDateTime(IDataRecord record, string name)
    {
        var ordinal = record.GetOrdinal(name);
        return record.IsDBNull(ordinal) ? null : Convert.ToDateTime(record.GetValue(ordinal));
    }

    private static object ParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<object>();

        try
        {
            return JsonSerializer.Deserialize<object>(value) ?? Array.Empty<object>();
        }
        catch
        {
            return value;
        }
    }

    private sealed class FieldKonnectSetting
    {
        public ulong Id { get; init; }
        public string? AppVersion { get; init; }
        public string? AppIosVersion { get; set; }
        public int? OrderDiscountLimit { get; init; }
        public DateTime? CreatedAt { get; init; }
        public DateTime? UpdatedAt { get; init; }
    }

    public sealed class SaveFieldKonnectSettingRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("android_version")]
        public string? AndroidVersion { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("ios_version")]
        public string? IosVersion { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("order_discount_limit")]
        public int? OrderDiscountLimit { get; init; }
    }
}
