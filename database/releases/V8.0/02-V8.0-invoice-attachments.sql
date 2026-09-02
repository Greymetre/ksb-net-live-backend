/* ============================================================================
   V8.0 - Invoice attachments: many per invoice, not one

   new_invoices.attachment holds a single path. Invoices are now raised with up
   to ten files - photographs and PDFs - so they move into a table of their own.

   The old column is left in place and still carries the first attachment, so any
   code that has not been updated keeps working and old invoices keep rendering.
   Everything already on the server is copied across, so nothing has to be
   re-uploaded.

   Safe to run more than once. Statements that touch the new table go through
   sp_executesql, because SQL Server compiles a batch as a whole and a table
   created in the same batch is not visible to a plain statement in it.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.new_invoices', 'U') IS NULL
BEGIN
    RAISERROR('STOPPED: new_invoices table not found - wrong database. Nothing was changed.', 16, 1);
    RETURN;
END;

IF COL_LENGTH('dbo.new_invoices', 'attachment') IS NULL
BEGIN
    RAISERROR('STOPPED: new_invoices has no attachment column, so this database is not the one this release expects. Nothing was changed.', 16, 1);
    RETURN;
END;

BEGIN TRANSACTION;

/* 1. The table. */
IF OBJECT_ID('dbo.new_invoice_attachments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.new_invoice_attachments
    (
        id          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_new_invoice_attachments PRIMARY KEY,
        invoice_id  DECIMAL(20, 0)  NOT NULL,
        file_path   NVARCHAR(1000)  NOT NULL,
        file_name   NVARCHAR(500)   NULL,
        mime_type   NVARCHAR(150)   NULL,
        file_size   BIGINT          NULL,
        sort_order  INT             NOT NULL CONSTRAINT DF_new_invoice_attachments_sort DEFAULT (0),
        created_at  DATETIME2       NULL,
        updated_at  DATETIME2       NULL
    );
    PRINT 'Created table new_invoice_attachments.';
END
ELSE
    PRINT 'Table new_invoice_attachments already present.';

/* 2. Reading an invoice's files must not scan the table. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_new_invoice_attachments_invoice_id'
                 AND object_id = OBJECT_ID('dbo.new_invoice_attachments'))
BEGIN
    EXEC sp_executesql N'CREATE INDEX IX_new_invoice_attachments_invoice_id
                         ON dbo.new_invoice_attachments (invoice_id, sort_order, id)';
    PRINT 'Created index IX_new_invoice_attachments_invoice_id.';
END
ELSE
    PRINT 'Index IX_new_invoice_attachments_invoice_id already present.';

/* 3. Copy across what is already on the server, once. An invoice that already has
      a row here is left alone, so a second run adds nothing. */
EXEC sp_executesql N'
    INSERT INTO dbo.new_invoice_attachments (invoice_id, file_path, file_name, sort_order, created_at, updated_at)
    SELECT i.id,
           i.attachment,
           RIGHT(i.attachment, CHARINDEX(''/'', REVERSE(i.attachment) + ''/'') - 1),
           0,
           SYSUTCDATETIME(),
           SYSUTCDATETIME()
    FROM dbo.new_invoices i
    WHERE i.attachment IS NOT NULL
      AND LTRIM(RTRIM(i.attachment)) <> ''''
      AND NOT EXISTS (SELECT 1 FROM dbo.new_invoice_attachments a WHERE a.invoice_id = i.id);';

DECLARE @copied INT;
EXEC sp_executesql N'SELECT @out = COUNT(*) FROM dbo.new_invoice_attachments;', N'@out INT OUTPUT', @out = @copied OUTPUT;

COMMIT TRANSACTION;

PRINT 'Existing invoice attachments carried across.';

EXEC sp_executesql N'
    SELECT N''Rows in new_invoice_attachments'' AS [step], CAST(COUNT(*) AS NVARCHAR(20)) AS [value] FROM dbo.new_invoice_attachments
    UNION ALL
    SELECT N''Invoices holding an attachment'', CAST(COUNT(*) AS NVARCHAR(20))
    FROM dbo.new_invoices WHERE attachment IS NOT NULL AND LTRIM(RTRIM(attachment)) <> '''';';
