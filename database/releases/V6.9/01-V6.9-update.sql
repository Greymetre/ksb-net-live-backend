-- FieldKonnect V6.9 SQL Server release script.
--
-- One schema change: new_invoices.invoice_number stops being globally unique.
-- Invoice numbers belong to a dealer's own series, so two dealers both numbering
-- their bills 1, 2, 3 is normal and the old unique index rejected the second one
-- outright. From V6.9 the rule lives in the API - unique inside one dealer, with
-- rejected invoices ignored so a corrected invoice can reuse its number - and the
-- index stays only as a lookup index.
--
-- No row is inserted, updated or deleted. Idempotent: running it twice changes
-- nothing the second time.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 01-V6.9-update.sql
--
-- Local docker:
--   docker exec -i ksb-cloud-local-sqlserver-1 /opt/mssql-tools18/bin/sqlcmd \
--     -S localhost -U sa -P 'KsbLocal_SQL2022!Pass' -C -d ksb_pr \
--     -i backend/database/releases/V6.9/01-V6.9-update.sql

SET NOCOUNT ON;
SET XACT_ABORT ON;

SELECT
    N'FieldKonnect V6.9 database update starting.' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [started_at_utc];

BEGIN TRANSACTION;

    ------------------------------------------------------------------
    -- 1. Replace the unique invoice_number index with a plain one.
    --
    -- Dropped and recreated rather than altered, because the uniqueness is the
    -- property being removed. Nothing depends on the index name changing, so it
    -- keeps the same name the EF model produces.
    ------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_new_invoices_invoice_number'
                 AND object_id = OBJECT_ID(N'new_invoices')
                 AND is_unique = 1)
    BEGIN
        DROP INDEX IX_new_invoices_invoice_number ON new_invoices;
        PRINT N'Dropped unique index IX_new_invoices_invoice_number';
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'IX_new_invoices_invoice_number'
                     AND object_id = OBJECT_ID(N'new_invoices'))
    BEGIN
        CREATE INDEX IX_new_invoices_invoice_number ON new_invoices(invoice_number);
        PRINT N'Created lookup index IX_new_invoices_invoice_number';
    END;

    ------------------------------------------------------------------
    -- 2. Record the matching EF Core migration as applied.
    --
    -- Control-S runs with SKIP_DB_BOOTSTRAP=true, so EF Core never applies
    -- migrations there and this schema change arrives through this script.
    -- Stamping the history row keeps the two in agreement, so the migration is
    -- not attempted a second time if automatic bootstrap is ever switched on.
    ------------------------------------------------------------------
    IF OBJECT_ID(N'__EFMigrationsHistory', N'U') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM __EFMigrationsHistory
                       WHERE MigrationId = N'20260821070000_ScopeInvoiceNumberToDealer')
    BEGIN
        INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
        VALUES (N'20260821070000_ScopeInvoiceNumberToDealer', N'8.0.13');
        PRINT N'Recorded EF migration 20260821070000_ScopeInvoiceNumberToDealer';
    END;

COMMIT TRANSACTION;

SELECT
    N'FieldKonnect V6.9 database update completed successfully.' AS [message],
    DB_NAME() AS [database_name],
    CASE WHEN EXISTS (SELECT 1 FROM sys.indexes
                      WHERE name = N'IX_new_invoices_invoice_number'
                        AND object_id = OBJECT_ID(N'new_invoices')
                        AND is_unique = 0)
         THEN N'OK' ELSE N'MISSING' END AS [invoice_number_lookup_index],
    SYSUTCDATETIME() AS [completed_at_utc];

-- ---------------------------------------------------------------------------
-- Post-deployment readiness reporting. Read-only.
-- ---------------------------------------------------------------------------

-- Invoice numbers that would now clash under the new rule: the same number used
-- more than once inside one dealer, counting only invoices that are not rejected.
-- The unique index made this impossible before V6.9, so the expected answer is
-- zero. Anything reported here was filed while the index was already absent and
-- should be reviewed before the dealers start reusing numbers.
WITH dealer_invoices AS (
    SELECT
        i.id,
        i.invoice_number,
        COALESCE(
            TRY_CONVERT(BIGINT, CASE WHEN ISJSON(c.custom_fields) = 1
                                     THEN JSON_VALUE(c.custom_fields, '$.distributor_name') END),
            TRY_CONVERT(BIGINT, CASE WHEN ISJSON(c.custom_fields) = 1
                                     THEN JSON_VALUE(c.custom_fields, '$.agri_distributor') END),
            TRY_CONVERT(BIGINT, c.parent_id)
        ) AS dealer_id
    FROM new_invoices i
    JOIN customers c ON c.id = i.secondary_customer_id
    WHERE i.approval_status <> 4
)
SELECT
    N'Invoice numbers repeated inside one dealer (excluding rejected)' AS [check],
    COUNT(*) AS [row_count]
FROM (
    SELECT dealer_id, invoice_number
    FROM dealer_invoices
    WHERE dealer_id IS NOT NULL
    GROUP BY dealer_id, invoice_number
    HAVING COUNT(*) > 1
) AS clashes;

-- Retailers that carry no dealer at all. Their invoice numbers are checked
-- against that retailer alone, because there is no dealer to scope them to.
SELECT
    N'Retailers with invoices but no dealer mapped' AS [check],
    COUNT(DISTINCT c.id) AS [row_count]
FROM customers c
JOIN new_invoices i ON i.secondary_customer_id = c.id
WHERE c.deleted_at IS NULL
  AND COALESCE(
        TRY_CONVERT(BIGINT, CASE WHEN ISJSON(c.custom_fields) = 1
                                 THEN JSON_VALUE(c.custom_fields, '$.distributor_name') END),
        TRY_CONVERT(BIGINT, CASE WHEN ISJSON(c.custom_fields) = 1
                                 THEN JSON_VALUE(c.custom_fields, '$.agri_distributor') END),
        TRY_CONVERT(BIGINT, c.parent_id)
      ) IS NULL;
