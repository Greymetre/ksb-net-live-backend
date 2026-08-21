using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations;

/// <summary>Invoice numbers are only unique inside one dealer's own series, which no
/// single column on new_invoices can express. The global unique index rejected a second
/// dealer reusing a number, so it becomes a plain lookup index and the rule moves into
/// NewInvoiceRepository.InvoiceNumberExistsAsync.</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260821070000_ScopeInvoiceNumberToDealer")]
public partial class ScopeInvoiceNumberToDealer : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_new_invoices_invoice_number", table: "new_invoices");
        migrationBuilder.CreateIndex(name: "IX_new_invoices_invoice_number", table: "new_invoices", column: "invoice_number");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_new_invoices_invoice_number", table: "new_invoices");
        migrationBuilder.CreateIndex(name: "IX_new_invoices_invoice_number", table: "new_invoices", column: "invoice_number", unique: true);
    }
}
