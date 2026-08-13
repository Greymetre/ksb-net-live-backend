using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260810111500_AddLeaveApprovalAudit")]
public partial class AddLeaveApprovalAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(name: "approved_by", table: "leaves", type: "decimal(20,0)", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "approved_at", table: "leaves", type: "datetime2", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "approved_by", table: "leaves");
        migrationBuilder.DropColumn(name: "approved_at", table: "leaves");
    }
}
