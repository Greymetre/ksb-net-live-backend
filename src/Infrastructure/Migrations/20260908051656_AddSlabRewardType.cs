using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// One column: the reward type a slab pays under a mixed scheme.
    ///
    /// The scaffolded version also wanted to create new_invoice_attachments. That table
    /// is already on every database - it was added by a release script rather than a
    /// migration, so the model snapshot had never heard of it - and creating it again
    /// would fail the deployment. Only the column belongs here.
    /// </summary>
    public partial class AddSlabRewardType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_scheme_slabs', 'reward_type') IS NULL
BEGIN
    ALTER TABLE loyalty_scheme_slabs ADD reward_type NVARCHAR(50) NULL;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('loyalty_scheme_slabs', 'reward_type') IS NOT NULL
BEGIN
    ALTER TABLE loyalty_scheme_slabs DROP COLUMN reward_type;
END");
        }
    }
}
