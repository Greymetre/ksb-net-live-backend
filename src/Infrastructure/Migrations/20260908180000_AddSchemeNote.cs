using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// One column: the short note a scheme creator writes, shown under the scheme
    /// dates wherever the scheme appears.
    ///
    /// Written by hand rather than scaffolded. The scaffolder wants to create
    /// new_invoice_attachments, which is already on every database because it
    /// arrived through a release script rather than a migration, and creating it a
    /// second time would fail the deployment.
    /// </summary>
    public partial class AddSchemeNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_schemes', 'scheme_note') IS NULL
BEGIN
    ALTER TABLE loyalty_schemes ADD scheme_note NVARCHAR(500) NULL;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_schemes', 'scheme_note') IS NOT NULL
BEGIN
    ALTER TABLE loyalty_schemes DROP COLUMN scheme_note;
END");
        }
    }
}
