using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// The notes a dealer leaves from the CRM dealer portal.
    ///
    /// One row holds one note - a category and a message - tied to the dealer's customer id
    /// so internal users read it reporting-wise. Internal users only view and export it.
    ///
    /// Written by hand and guarded so it can be run twice - the live server applies it as a
    /// release script, not as a migration.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261008120000_AddDealerFeedback")]
    public partial class AddDealerFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID('dealer_feedback', 'U') IS NULL
BEGIN
    CREATE TABLE dealer_feedback (
        -- DECIMAL(20, 0), not BIGINT: every id on this database is that, because the
        -- schema came from Laravel, and the reader hands a BIGINT back as Int64 where the
        -- model expects a decimal.
        id                  DECIMAL(20, 0) IDENTITY(1,1) NOT NULL CONSTRAINT PK_dealer_feedback PRIMARY KEY,
        customer_id         DECIMAL(20, 0) NOT NULL,
        created_by_user_id  DECIMAL(20, 0) NULL,
        category            NVARCHAR(100) NOT NULL CONSTRAINT DF_dealer_feedback_category DEFAULT (N''),
        message             NVARCHAR(MAX) NOT NULL CONSTRAINT DF_dealer_feedback_message DEFAULT (N''),
        created_at          DATETIME2 NULL,
        updated_at          DATETIME2 NULL,
        deleted_at          DATETIME2 NULL
    );
END");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_dealer_feedback_customer_id' AND object_id = OBJECT_ID('dealer_feedback'))
    CREATE INDEX IX_dealer_feedback_customer_id ON dealer_feedback(customer_id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS dealer_feedback;");
        }
    }
}
