using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
        builder.Property(x => x.GuardName).HasColumnName("guard_name").HasMaxLength(255);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => new { x.Name, x.GuardName }).IsUnique();
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
        builder.Property(x => x.GuardName).HasColumnName("guard_name").HasMaxLength(255);
        builder.Property(x => x.Label).HasColumnName("label").HasMaxLength(255);
        builder.Property(x => x.GroupKey).HasColumnName("group_key").HasMaxLength(64);
        builder.Property(x => x.GroupLabel).HasColumnName("group_label").HasMaxLength(255);
        builder.Property(x => x.ModuleKey).HasColumnName("module_key").HasMaxLength(64);
        builder.Property(x => x.ModuleLabel).HasColumnName("module_label").HasMaxLength(255);
        builder.Property(x => x.ActionKey).HasColumnName("action_key").HasMaxLength(64);
        builder.Property(x => x.SortOrder).HasColumnName("sort_order");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(x => new { x.Name, x.GuardName }).IsUnique();
    }
}

public sealed class ModelHasRoleConfiguration : IEntityTypeConfiguration<ModelHasRole>
{
    public void Configure(EntityTypeBuilder<ModelHasRole> builder)
    {
        builder.ToTable("model_has_roles");
        builder.HasKey(x => new { x.RoleId, x.ModelId, x.ModelType });
        builder.Property(x => x.RoleId).HasColumnName("role_id");
        builder.Property(x => x.ModelType).HasColumnName("model_type").HasMaxLength(255);
        builder.Property(x => x.ModelId).HasColumnName("model_id");
        builder.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
    }
}

/// <summary>The CRM grants permissions through roles only - there is no screen or API that
/// gives one user a permission of their own. The table is kept mapped so no migration drops
/// it and the foreign key to permissions stays valid, but nothing reads or writes it, and
/// the catalog seeder empties it on every start.</summary>
public sealed class ModelHasPermissionConfiguration : IEntityTypeConfiguration<ModelHasPermission>
{
    public void Configure(EntityTypeBuilder<ModelHasPermission> builder)
    {
        builder.ToTable("model_has_permissions");
        builder.HasKey(x => new { x.PermissionId, x.ModelId, x.ModelType });
        builder.Property(x => x.PermissionId).HasColumnName("permission_id");
        builder.Property(x => x.ModelType).HasColumnName("model_type").HasMaxLength(255);
        builder.Property(x => x.ModelId).HasColumnName("model_id");
        builder.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId);
    }
}

public sealed class RoleHasPermissionConfiguration : IEntityTypeConfiguration<RoleHasPermission>
{
    public void Configure(EntityTypeBuilder<RoleHasPermission> builder)
    {
        builder.ToTable("role_has_permissions");
        builder.HasKey(x => new { x.PermissionId, x.RoleId });
        builder.Property(x => x.PermissionId).HasColumnName("permission_id");
        builder.Property(x => x.RoleId).HasColumnName("role_id");
    }
}

public sealed class MobileUserLoginDetailConfiguration : IEntityTypeConfiguration<MobileUserLoginDetail>
{
    public void Configure(EntityTypeBuilder<MobileUserLoginDetail> builder)
    {
        builder.ToTable("mobile_user_login_details");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.CustomerId).HasColumnName("customer_id");
        builder.Property(x => x.AppVersion).HasColumnName("app_version").HasMaxLength(255);
        builder.Property(x => x.DeviceName).HasColumnName("device_name").HasMaxLength(255);
        builder.Property(x => x.DeviceType).HasColumnName("device_type").HasMaxLength(255);
        builder.Property(x => x.UniqueId).HasColumnName("unique_id").HasMaxLength(255);
        builder.Property(x => x.FirstLoginDate).HasColumnName("first_login_date");
        builder.Property(x => x.LastLoginDate).HasColumnName("last_login_date");
        builder.Property(x => x.LoginStatus).HasColumnName("login_status").HasMaxLength(10);
        builder.Property(x => x.MultiLogin).HasColumnName("multi_login").HasMaxLength(10);
        builder.Property(x => x.App).HasColumnName("app").HasMaxLength(10);
        builder.Property(x => x.LoginAt).HasColumnName("login_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class OAuthAccessTokenConfiguration : IEntityTypeConfiguration<OAuthAccessToken>
{
    public void Configure(EntityTypeBuilder<OAuthAccessToken> builder)
    {
        builder.ToTable("oauth_access_tokens");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasMaxLength(100);
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ClientId).HasColumnName("client_id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
        builder.Property(x => x.Scopes).HasColumnName("scopes").HasColumnType("text");
        builder.Property(x => x.Revoked).HasColumnName("revoked");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.HasIndex(x => x.UserId);
    }
}
