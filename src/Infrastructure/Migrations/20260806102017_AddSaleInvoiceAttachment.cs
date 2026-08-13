using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleInvoiceAttachment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF COL_LENGTH('sales', 'invoice_attachment') IS NULL
                ALTER TABLE [sales] ADD [invoice_attachment] nvarchar(500) NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF COL_LENGTH('sales', 'invoice_attachment') IS NOT NULL
                ALTER TABLE [sales] DROP COLUMN [invoice_attachment];");
        }
    }
}
