using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// The dealers picked on the scheme form, stored the way the area values already are:
    /// one JSON array on the scheme row. Ids rather than names, because a dealer can be
    /// renamed and the scheme has to keep meaning the same dealers.
    ///
    /// Written by hand rather than scaffolded, like the others here: the scaffolder wants to
    /// create new_invoice_attachments, which every database already has.
    /// </summary>
    public partial class AddSchemeExcludedDealerIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_schemes', 'excluded_dealer_ids') IS NULL
BEGIN
    ALTER TABLE loyalty_schemes ADD excluded_dealer_ids NVARCHAR(MAX) NULL;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_schemes', 'excluded_dealer_ids') IS NOT NULL
BEGIN
    ALTER TABLE loyalty_schemes DROP COLUMN excluded_dealer_ids;
END");
        }
    }
}
