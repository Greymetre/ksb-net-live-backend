using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Makes the invoice listing's zone and branch filters usable.
    ///
    /// The assignment those filters read lives in customers.custom_fields as JSON, and
    /// was being found with ten leading-wildcard LIKEs per employee per row - 197 seconds
    /// of CPU for one zone on the live data, against a screen that gives up at twenty.
    /// These three persisted computed columns hold exactly what that predicate looked
    /// for, and are indexed. SQL Server recomputes them on every write, so they cannot
    /// drift from custom_fields and nothing in the application has to maintain them.
    ///
    /// DECIMAL(20, 0) because that is what executive_id and every other user column on
    /// this database is.
    ///
    /// Written by hand rather than scaffolded, like the others here: the scaffolder wants
    /// to create new_invoice_attachments, which every database already has.
    /// </summary>
    public partial class AddCustomerAssignmentColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('customers', 'assigned_employee_id') IS NULL
BEGIN
    ALTER TABLE customers ADD assigned_employee_id AS TRY_CAST(LEFT(
        JSON_VALUE(custom_fields, '$.employee_id'),
        CHARINDEX(',', JSON_VALUE(custom_fields, '$.employee_id') + ',') - 1) AS DECIMAL(20, 0)) PERSISTED;
END");
            migrationBuilder.Sql(@"
IF COL_LENGTH('customers', 'assigned_sales_executive_id') IS NULL
BEGIN
    ALTER TABLE customers ADD assigned_sales_executive_id AS TRY_CAST(LEFT(
        JSON_VALUE(custom_fields, '$.sales_executive_id'),
        CHARINDEX(',', JSON_VALUE(custom_fields, '$.sales_executive_id') + ',') - 1) AS DECIMAL(20, 0)) PERSISTED;
END");
            migrationBuilder.Sql(@"
IF COL_LENGTH('customers', 'assigned_fallback_employee_id') IS NULL
BEGIN
    ALTER TABLE customers ADD assigned_fallback_employee_id AS (CASE
        WHEN JSON_VALUE(custom_fields, '$.employee_id') IS NULL
         AND JSON_VALUE(custom_fields, '$.sales_executive_id') IS NULL
        THEN executive_id END) PERSISTED;
END");

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_customers_assigned_employee_id' AND object_id = OBJECT_ID('customers'))
    CREATE INDEX IX_customers_assigned_employee_id ON customers(assigned_employee_id);");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_customers_assigned_sales_executive_id' AND object_id = OBJECT_ID('customers'))
    CREATE INDEX IX_customers_assigned_sales_executive_id ON customers(assigned_sales_executive_id);");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_customers_assigned_fallback_employee_id' AND object_id = OBJECT_ID('customers'))
    CREATE INDEX IX_customers_assigned_fallback_employee_id ON customers(assigned_fallback_employee_id);");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_users_division_id' AND object_id = OBJECT_ID('users'))
    CREATE INDEX IX_users_division_id ON users(division_id);");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_users_primary_branch_id' AND object_id = OBJECT_ID('users'))
   AND COL_LENGTH('users', 'primary_branch_id') IS NOT NULL
    CREATE INDEX IX_users_primary_branch_id ON users(primary_branch_id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_customers_assigned_employee_id ON customers;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_customers_assigned_sales_executive_id ON customers;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_customers_assigned_fallback_employee_id ON customers;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_users_division_id ON users;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_users_primary_branch_id ON users;");
            migrationBuilder.Sql(@"
IF COL_LENGTH('customers', 'assigned_employee_id') IS NOT NULL ALTER TABLE customers DROP COLUMN assigned_employee_id;
IF COL_LENGTH('customers', 'assigned_sales_executive_id') IS NOT NULL ALTER TABLE customers DROP COLUMN assigned_sales_executive_id;
IF COL_LENGTH('customers', 'assigned_fallback_employee_id') IS NOT NULL ALTER TABLE customers DROP COLUMN assigned_fallback_employee_id;");
        }
    }
}
