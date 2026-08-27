using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDealerToNewInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "dealer_customer_id",
                table: "new_invoices",
                type: "decimal(20,0)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_new_invoices_dealer_customer_id",
                table: "new_invoices",
                column: "dealer_customer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_new_invoices_dealer_customer_id",
                table: "new_invoices");

            migrationBuilder.DropColumn(
                name: "dealer_customer_id",
                table: "new_invoices");
        }
    }
}
