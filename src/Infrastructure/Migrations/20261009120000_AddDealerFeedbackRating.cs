using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Adds the 1-5 star rating a dealer gives alongside the feedback. Guarded so it can be
    /// run twice - the live server applies it as a release script, not as a migration.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261009120000_AddDealerFeedbackRating")]
    public partial class AddDealerFeedbackRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('dealer_feedback', 'rating') IS NULL
    ALTER TABLE dealer_feedback ADD rating INT NOT NULL CONSTRAINT DF_dealer_feedback_rating DEFAULT (3);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('dealer_feedback', 'rating') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_dealer_feedback_rating')
        ALTER TABLE dealer_feedback DROP CONSTRAINT DF_dealer_feedback_rating;
    ALTER TABLE dealer_feedback DROP COLUMN rating;
END");
        }
    }
}
