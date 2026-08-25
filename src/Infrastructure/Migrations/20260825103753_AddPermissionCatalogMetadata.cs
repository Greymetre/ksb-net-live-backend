using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionCatalogMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "action_key",
                table: "permissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "group_key",
                table: "permissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "group_label",
                table: "permissions",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "permissions",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "module_key",
                table: "permissions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "module_label",
                table: "permissions",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sort_order",
                table: "permissions",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "action_key",
                table: "permissions");

            migrationBuilder.DropColumn(
                name: "group_key",
                table: "permissions");

            migrationBuilder.DropColumn(
                name: "group_label",
                table: "permissions");

            migrationBuilder.DropColumn(
                name: "label",
                table: "permissions");

            migrationBuilder.DropColumn(
                name: "module_key",
                table: "permissions");

            migrationBuilder.DropColumn(
                name: "module_label",
                table: "permissions");

            migrationBuilder.DropColumn(
                name: "sort_order",
                table: "permissions");
        }
    }
}
