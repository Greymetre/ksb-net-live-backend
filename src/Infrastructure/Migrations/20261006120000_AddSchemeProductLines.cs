using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Infrastructure.Data;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// The lines a Product or Quantity scheme is written as.
    ///
    /// An Invoice scheme is read on the whole bill and keeps its slabs. The two new types
    /// are read on the goods, so a scheme names segments, families and products and says
    /// what they earn. One row holds one such line, with the ids comma separated the way
    /// a beat holds its cities: a line is written, imported and edited whole, and is never
    /// looked up by a single id.
    ///
    /// Written by hand rather than scaffolded, like the others here, and guarded so it can
    /// be run twice - the live server applies it as a release script, not as a migration.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261006120000_AddSchemeProductLines")]
    public partial class AddSchemeProductLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID('loyalty_scheme_products', 'U') IS NULL
BEGIN
    CREATE TABLE loyalty_scheme_products (
        -- DECIMAL(20, 0), not BIGINT: every id on this database is that, because the
        -- schema came from Laravel, and the reader hands a BIGINT back as Int64 where the
        -- model expects a decimal.
        id                DECIMAL(20, 0) IDENTITY(1,1) NOT NULL CONSTRAINT PK_loyalty_scheme_products PRIMARY KEY,
        loyalty_scheme_id DECIMAL(20, 0) NOT NULL,
        segment_ids       NVARCHAR(MAX) NOT NULL CONSTRAINT DF_lsp_segment_ids DEFAULT (N''),
        family_ids        NVARCHAR(MAX) NOT NULL CONSTRAINT DF_lsp_family_ids DEFAULT (N''),
        product_ids       NVARCHAR(MAX) NOT NULL CONSTRAINT DF_lsp_product_ids DEFAULT (N''),
        reward_value      DECIMAL(15, 2) NOT NULL CONSTRAINT DF_lsp_reward_value DEFAULT (0),
        reward_type       NVARCHAR(50) NULL,
        sort_order        INT NOT NULL CONSTRAINT DF_lsp_sort_order DEFAULT (0),
        created_at        DATETIME2 NULL,
        updated_at        DATETIME2 NULL,
        deleted_at        DATETIME2 NULL
    );
END");
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_loyalty_scheme_products_loyalty_scheme_id' AND object_id = OBJECT_ID('loyalty_scheme_products'))
    CREATE INDEX IX_loyalty_scheme_products_loyalty_scheme_id ON loyalty_scheme_products(loyalty_scheme_id);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS loyalty_scheme_products;");
        }
    }
}
