using Domain.Constants;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Seeders;

public static class SuperAdminSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var email = Environment.GetEnvironmentVariable("SUPERADMIN_EMAIL")
                    ?? "gajendra@greymetre.io";
        var password = Environment.GetEnvironmentVariable("SUPERADMIN_PASSWORD")
                       ?? "Grey@2028@Field";

        if (password.Length < 10)
        {
            throw new InvalidOperationException("SUPERADMIN_PASSWORD must contain at least 10 characters.");
        }

        var name = Environment.GetEnvironmentVariable("SUPERADMIN_NAME") ?? "Gajendra";
        var mobile = Environment.GetEnvironmentVariable("SUPERADMIN_MOBILE") ?? "9713113280";
        var secondEmail = Environment.GetEnvironmentVariable("SECOND_SUPERADMIN_EMAIL")
                          ?? "swaraj.khalate@ksb.com";
        var secondPassword = Environment.GetEnvironmentVariable("SECOND_SUPERADMIN_PASSWORD")
                             ?? "Swaraj@5999@Fiedl";
        var secondName = Environment.GetEnvironmentVariable("SECOND_SUPERADMIN_NAME")
                         ?? "Swaraj Khalate";
        var secondMobile = Environment.GetEnvironmentVariable("SECOND_SUPERADMIN_MOBILE")
                           ?? "8793535999";

        if (secondPassword.Length < 10)
        {
            throw new InvalidOperationException(
                "SECOND_SUPERADMIN_PASSWORD must contain at least 10 characters.");
        }

        var now = DateTime.UtcNow;

        var role = await db.Roles.FirstOrDefaultAsync(
            x => x.Name == "superadmin" && x.GuardName == "users",
            cancellationToken);

        if (role is null)
        {
            role = new Role
            {
                Name = "superadmin",
                GuardName = "users",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync(cancellationToken);
        }

        var permissionIds = await db.Permissions
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var assignedPermissionIds = await db.RoleHasPermissions
            .Where(x => x.RoleId == role.Id)
            .Select(x => x.PermissionId)
            .ToListAsync(cancellationToken);

        var missingRolePermissions = permissionIds
            .Except(assignedPermissionIds)
            .Select(permissionId => new RoleHasPermission
            {
                RoleId = role.Id,
                PermissionId = permissionId
            })
            .ToArray();

        if (missingRolePermissions.Length > 0)
        {
            db.RoleHasPermissions.AddRange(missingRolePermissions);
            await db.SaveChangesAsync(cancellationToken);
        }

        var accounts = new[]
        {
            new SuperAdminAccount(email, password, name, mobile),
            new SuperAdminAccount(secondEmail, secondPassword, secondName, secondMobile)
        };

        foreach (var account in accounts)
        {
            var user = await db.Users
                .FirstOrDefaultAsync(x => x.Email == account.Email, cancellationToken);

            if (user is null)
            {
                user = new User
                {
                    Active = "Y",
                    Name = account.Name,
                    FirstName = account.Name,
                    LastName = string.Empty,
                    Mobile = account.Mobile,
                    Email = account.Email,
                    Password = BCrypt.Net.BCrypt.HashPassword(account.Password),
                    NotificationId = string.Empty,
                    DeviceType = string.Empty,
                    Gender = string.Empty,
                    ProfileImage = string.Empty,
                    Latitude = string.Empty,
                    Longitude = string.Empty,
                    UserCode = string.Empty,
                    Location = string.Empty,
                    SalesType = string.Empty,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.Users.Add(user);
            }
            else
            {
                user.Name = account.Name;
                user.FirstName = account.Name;
                user.Mobile = account.Mobile;
                user.Active = "Y";
                user.Password = BCrypt.Net.BCrypt.HashPassword(account.Password);
                user.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);

            var directPermissions = await db.ModelHasPermissions
                .Where(x => x.ModelId == user.Id && x.ModelType == LaravelModelTypes.User)
                .ToListAsync(cancellationToken);

            if (directPermissions.Count > 0)
            {
                db.ModelHasPermissions.RemoveRange(directPermissions);
                await db.SaveChangesAsync(cancellationToken);
            }

            var hasRole = await db.ModelHasRoles.AnyAsync(
                x => x.RoleId == role.Id &&
                     x.ModelId == user.Id &&
                     x.ModelType == LaravelModelTypes.User,
                cancellationToken);

            if (!hasRole)
            {
                db.ModelHasRoles.Add(new ModelHasRole
                {
                    RoleId = role.Id,
                    ModelId = user.Id,
                    ModelType = LaravelModelTypes.User
                });
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private sealed record SuperAdminAccount(
        string Email,
        string Password,
        string Name,
        string Mobile);
}
