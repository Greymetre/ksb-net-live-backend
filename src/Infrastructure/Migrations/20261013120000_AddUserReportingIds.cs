using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// A user may now report to more than one manager. reportingid stays as the primary
    /// (first) manager; reporting_ids holds the full comma-separated list, and existing rows
    /// are back-filled from reportingid so nothing changes until a user is given a second one.
    ///
    /// Written by hand and guarded so it can be run twice - the live server applies it as a
    /// release script, not as a migration.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261013120000_AddUserReportingIds")]
    public partial class AddUserReportingIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('users', 'reporting_ids') IS NULL
    ALTER TABLE users ADD reporting_ids NVARCHAR(MAX) NULL;");
            migrationBuilder.Sql(@"
UPDATE users SET reporting_ids = CONVERT(nvarchar(50), CONVERT(bigint, reportingid))
WHERE (reporting_ids IS NULL OR reporting_ids = '') AND reportingid IS NOT NULL AND reportingid > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF COL_LENGTH('users', 'reporting_ids') IS NOT NULL ALTER TABLE users DROP COLUMN reporting_ids;");
        }
    }
}
