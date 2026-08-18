using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityCodeAndParticipantType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The promotional activity tables were created by the release SQL scripts and
            // were never part of an EF migration, so the scaffolded CreateTable calls were
            // replaced by the two columns this migration actually introduces. Both are
            // guarded, which keeps re-runs and partially patched databases safe.
            migrationBuilder.Sql(@"IF COL_LENGTH('promotional_activities', 'activity_code') IS NULL
    ALTER TABLE promotional_activities ADD activity_code nvarchar(40) NULL;");

            migrationBuilder.Sql(@"IF COL_LENGTH('promotional_activity_participants', 'participant_type') IS NULL
    ALTER TABLE promotional_activity_participants ADD participant_type nvarchar(100) NULL;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"IF COL_LENGTH('promotional_activities', 'activity_code') IS NOT NULL
    ALTER TABLE promotional_activities DROP COLUMN activity_code;");

            migrationBuilder.Sql(@"IF COL_LENGTH('promotional_activity_participants', 'participant_type') IS NOT NULL
    ALTER TABLE promotional_activity_participants DROP COLUMN participant_type;");
        }
    }
}
