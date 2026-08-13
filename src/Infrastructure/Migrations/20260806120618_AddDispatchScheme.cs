using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchScheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF COL_LENGTH('dbo.sales', 'loyalty_scheme_id') IS NULL
                    ALTER TABLE dbo.sales ADD loyalty_scheme_id decimal(20,0) NULL;

                IF NOT EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_sales_loyalty_scheme_id'
                      AND object_id = OBJECT_ID('dbo.sales')
                )
                    CREATE INDEX IX_sales_loyalty_scheme_id ON dbo.sales(loyalty_scheme_id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_sales_loyalty_scheme_id'
                      AND object_id = OBJECT_ID('dbo.sales')
                )
                    DROP INDEX IX_sales_loyalty_scheme_id ON dbo.sales;

                IF COL_LENGTH('dbo.sales', 'loyalty_scheme_id') IS NOT NULL
                    ALTER TABLE dbo.sales DROP COLUMN loyalty_scheme_id;");
        }
    }
}
