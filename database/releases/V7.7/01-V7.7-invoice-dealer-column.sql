-- FieldKonnect V7.7 - the dealer an invoice belongs to.
--
-- WHAT THIS IS
-- One nullable column on new_invoices, plus its index. The field app asks who the
-- invoice is for when a retailer sits under more than one dealer - about two hundred
-- retailers carry both a domestic and an agri dealer - and that answer is stored here.
--
-- Nothing is back-filled. Every invoice raised before this release keeps a NULL, and
-- NULL still means "read the dealer from the retailer's mapping", which is exactly how
-- the CRM and the dealer app have always found it. So no existing invoice moves, and
-- no dealer loses sight of anything they can see today.
--
-- WHAT IT TOUCHES
-- new_invoices and __EFMigrationsHistory. No data is written to any row.
--
-- Adding a NULLable column with no default is a metadata-only change in SQL Server: it
-- does not rewrite the table and does not depend on how many invoices there are. The
-- index build is the only real work.
--
-- It checks the database first and stops without writing if it is not the one expected.
-- It is safe to run twice: the second run finds the column and the index in place.
--
-- TAKE A FULL DATABASE BACKUP FIRST, then run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 01-V7.7-invoice-dealer-column.sql
--
-- Restart the API (IIS app pool recycle) after this completes.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

PRINT '== FieldKonnect V7.7 - invoice dealer column ==';
GO

-- One batch on purpose: a failed check must stop the script, and RETURN only ends the
-- batch it sits in.
BEGIN
    IF OBJECT_ID('dbo.new_invoices', 'U') IS NULL
    BEGIN
        RAISERROR('STOPPED: table "new_invoices" was not found. Is this the FieldKonnect database?', 16, 1);
        RETURN;
    END;

    DECLARE @added BIT = 0, @indexed BIT = 0, @migration_recorded BIT = 0;

    IF COL_LENGTH('dbo.new_invoices', 'dealer_customer_id') IS NULL
    BEGIN
        ALTER TABLE dbo.new_invoices ADD dealer_customer_id DECIMAL(20, 0) NULL;
        SET @added = 1;
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_new_invoices_dealer_customer_id'
                     AND object_id = OBJECT_ID('dbo.new_invoices'))
    BEGIN
        CREATE INDEX IX_new_invoices_dealer_customer_id ON dbo.new_invoices (dealer_customer_id);
        SET @indexed = 1;
    END;

    -- The API applies migrations only where bootstrap is enabled; live runs with it off.
    -- Recording the row keeps the two in step, so a future bootstrap-enabled start does
    -- not try to add a column that is already there.
    IF OBJECT_ID('dbo.__EFMigrationsHistory', 'U') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'20260827094620_AddDealerToNewInvoices')
    BEGIN
        INSERT INTO dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
        VALUES (N'20260827094620_AddDealerToNewInvoices', N'8.0.13');
        SET @migration_recorded = 1;
    END;

    SELECT N'Column added by this run'    AS [step], CASE WHEN @added = 1 THEN N'yes' ELSE N'no - already present' END AS [value]
    UNION ALL SELECT N'Index created by this run',    CASE WHEN @indexed = 1 THEN N'yes' ELSE N'no - already present' END
    UNION ALL SELECT N'Migration row recorded',       CASE WHEN @migration_recorded = 1 THEN N'yes' ELSE N'no - already present' END
    UNION ALL SELECT N'Invoices carrying a dealer',   CAST((SELECT COUNT(*) FROM dbo.new_invoices WHERE dealer_customer_id IS NOT NULL) AS NVARCHAR(20))
    UNION ALL SELECT N'Invoices left on the mapping', CAST((SELECT COUNT(*) FROM dbo.new_invoices WHERE dealer_customer_id IS NULL) AS NVARCHAR(20));
END;
GO
