using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Who published a scheme, and when.
    ///
    /// Submission, approval and rejection were each stamped; publishing was not, and
    /// UpdatedBy is no substitute because the next edit overwrites it. Both columns stay
    /// NULL on schemes published before this - that moment was never recorded, and
    /// inventing one would be worse than leaving it blank.
    ///
    /// Written by hand rather than scaffolded, for the same reason as the others: the
    /// scaffolder wants to create new_invoice_attachments, which every database already
    /// has from a release script.
    ///
    /// published_by is DECIMAL(20, 0) rather than BIGINT because that is what every other
    /// user column on this table is - the type the original MySQL bigint unsigned became.
    /// </summary>
    public partial class AddSchemePublishedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_schemes', 'published_at') IS NULL
BEGIN
    ALTER TABLE loyalty_schemes ADD published_at DATETIME2 NULL;
END");
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_schemes', 'published_by') IS NULL
BEGIN
    ALTER TABLE loyalty_schemes ADD published_by DECIMAL(20, 0) NULL;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_schemes', 'published_by') IS NOT NULL
BEGIN
    ALTER TABLE loyalty_schemes DROP COLUMN published_by;
END");
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_schemes', 'published_at') IS NOT NULL
BEGIN
    ALTER TABLE loyalty_schemes DROP COLUMN published_at;
END");
        }
    }
}
