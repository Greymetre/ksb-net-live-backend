using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Api.Extensions;
using Api.Filters;
using Api.Middleware;
using Api.Services;
using Application.Extensions;
using Infrastructure.Data;
using Infrastructure.Extensions;
using Infrastructure.Seeders;
using Infrastructure.Seeders.MasterData;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;

JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services
    .AddControllers(options => options.Filters.Add<ServerPaginationResultFilter>())
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddLaravelCompatibleSwagger();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ISmtpEmailSender, SmtpEmailSender>();

var jwt = builder.Configuration.GetSection("Jwt");
var jwtKey = jwt["Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured.");
var allowedCorsOrigins = GetCorsOrigins(builder.Configuration);

if (!builder.Environment.IsDevelopment() &&
    (jwtKey.StartsWith("SET_", StringComparison.OrdinalIgnoreCase) ||
     Encoding.UTF8.GetByteCount(jwtKey) < 32))
{
    throw new InvalidOperationException(
        "Jwt:Key must be a non-placeholder production secret of at least 32 bytes.");
}

if (!builder.Environment.IsDevelopment() && allowedCorsOrigins.Length == 0)
{
    throw new InvalidOperationException(
        "CORS_ALLOWED_ORIGINS must contain the deployed Angular application URL in production.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var tokenId = context.Principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
                if (string.IsNullOrWhiteSpace(tokenId))
                {
                    context.Fail("Token id missing.");
                    return;
                }

                var dbContext = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var storedToken = await dbContext.OAuthAccessTokens.FirstOrDefaultAsync(x => x.Id == tokenId, context.HttpContext.RequestAborted);
                if (storedToken is null || storedToken.Revoked)
                {
                    context.Fail("Token revoked.");
                    return;
                }

                // Mobile builds should send X-App-Version on API calls. This makes
                // the installed version refresh immediately after a Play Store update,
                // without requiring another login.
                var appVersion = context.HttpContext.Request.Headers["X-App-Version"].ToString().Trim();
                var provider = context.Principal?.FindFirst("provider")?.Value;
                if (provider == "users" && storedToken.Name?.Contains("mobile", StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(appVersion))
                {
                    var userIdValue = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    if (ulong.TryParse(userIdValue, out var userId))
                    {
                        var detail = await dbContext.MobileUserLoginDetails
                            .Where(x => x.UserId == userId && x.App == "2")
                            .OrderByDescending(x => x.Id)
                            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);
                        if (detail is not null && !string.Equals(detail.AppVersion, appVersion, StringComparison.Ordinal))
                        {
                            detail.AppVersion = appVersion;
                            detail.UpdatedAt = DateTime.UtcNow;
                            await dbContext.SaveChangesAsync(context.HttpContext.RequestAborted);
                        }
                    }
                }
            }
        };
    });

builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendCors", policy =>
    {
        if (allowedCorsOrigins.Length == 0)
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            policy.WithOrigins(allowedCorsOrigins);
        }

        policy
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddAuthorization();

var app = builder.Build();

if (HasFlag(args, "--migrate"))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
    var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync()).ToArray();
    Console.WriteLine(pendingMigrations.Length == 0
        ? "No pending database migrations."
        : $"Applying database migrations: {string.Join(", ", pendingMigrations)}");
    await dbContext.Database.MigrateAsync();
    Console.WriteLine("Database migration completed.");
    return;
}

if (HasFlag(args, "--seed-superadmin"))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await SuperAdminSeeder.SeedAsync(dbContext);
    Console.WriteLine("Seeder completed: configured superadmin user is ready.");
    return;
}

if (HasFlag(args, "--seed-master-data"))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await MasterDataSeeder.SeedAsync(dbContext);
    Console.WriteLine("Seeder completed: master data tables are ready.");
    return;
}

// Keep local, Docker and remote Microsoft SQL Server databases in sync.
if (!IsDisabled(Environment.GetEnvironmentVariable("SKIP_DB_BOOTSTRAP")))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

    Console.WriteLine("Checking database migrations...");
    var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync()).ToArray();
    Console.WriteLine(pendingMigrations.Length == 0
        ? "No pending database migrations."
        : $"Applying database migrations: {string.Join(", ", pendingMigrations)}");
    await dbContext.Database.MigrateAsync();

    Console.WriteLine("Checking master data and permissions...");
    await MasterDataSeeder.SeedAsync(dbContext);
    await SuperAdminSeeder.SeedAsync(dbContext);
    Console.WriteLine("Automatic database bootstrap completed.");
}

app.UseMiddleware<LaravelExceptionMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Keep migrated Laravel media outside the published application so an IIS or
// Docker redeploy cannot remove thousands of historical customer documents.
// The same physical files are exposed through both Laravel URL conventions.
var legacyFilesRoot = builder.Configuration["FileUploads:LegacyFilesRoot"];
if (!string.IsNullOrWhiteSpace(legacyFilesRoot))
{
    legacyFilesRoot = Path.GetFullPath(legacyFilesRoot);
    MapLegacyFiles(app, Path.Combine(legacyFilesRoot, "storage"), "/storage", "/public/storage");
    MapLegacyFiles(app, Path.Combine(legacyFilesRoot, "uploads"), "/uploads", "/public/uploads");

    // Spatie Media Library and a few Laravel modules stored objects on S3.
    // A downloaded S3 bucket can be placed here without changing DB values.
    MapLegacyFiles(app, Path.Combine(legacyFilesRoot, "s3"), "/storage", "/public/storage");
    MapLegacyFiles(app, Path.Combine(legacyFilesRoot, "s3", "uploads"), "/uploads", "/public/uploads");
}

app.UseStaticFiles();
app.UseRouting();
app.UseCors("FrontendCors");
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapControllers();

app.Run();

static bool HasFlag(string[] args, string flag)
{
    return args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
}

static bool IsDisabled(string? value) =>
    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
    || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
    || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

static string[] GetCorsOrigins(IConfiguration configuration)
{
    var configuredOrigins = configuration["CORS"]
        ?? configuration["Cors"]
        ?? configuration["Cors:AllowedOrigins"]
        ?? configuration["CORS_ALLOWED_ORIGINS"];

    return (configuredOrigins ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(origin => origin.TrimEnd('/'))
        .Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out _))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static void MapLegacyFiles(WebApplication app, string physicalPath, params string[] requestPaths)
{
    if (!Directory.Exists(physicalPath))
    {
        app.Logger.LogWarning("Legacy file directory does not exist and was skipped: {Path}", physicalPath);
        return;
    }

    var provider = new PhysicalFileProvider(physicalPath);
    foreach (var requestPath in requestPaths)
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            RequestPath = requestPath,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream"
        });
    }

    app.Logger.LogInformation("Serving legacy files from {Path}", physicalPath);
}
