-- FieldKonnect V6.7 - expense data reset.
--
-- The expense module goes live with the SFA app in V6.7, so the trial claims
-- entered while it was being built are cleared out and users start from an empty
-- register. Expense TYPES are master data and are deliberately kept - only the
-- claims, their audit log and their attachment records are removed.
--
-- DESTRUCTIVE. Take a database backup before running this on the live server.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 02-V6.7-reset-expenses.sql
--
-- Local docker:
--   docker exec -i ksb-cloud-local-sqlserver-1 /opt/mssql-tools18/bin/sqlcmd \
--     -S localhost -U sa -P 'KsbLocal_SQL2022!Pass' -C -d ksb_pr \
--     -i backend/database/releases/V6.7/02-V6.7-reset-expenses.sql

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @expenses INT = (SELECT COUNT(*) FROM expenses);
DECLARE @logs INT = (SELECT COUNT(*) FROM expense_logs);
DECLARE @media INT = (SELECT COUNT(*) FROM media WHERE model_type = 'App\Models\Expenses');

SELECT
    N'Before reset' AS [stage],
    @expenses AS [expenses],
    @logs AS [expense_logs],
    @media AS [attachment_rows];

BEGIN TRANSACTION;

    -- Attachment rows first: they point at expenses through the polymorphic
    -- model_type/model_id pair, so they would otherwise be orphaned.
    DELETE FROM media
    WHERE model_type = 'App\Models\Expenses';

    -- The audit trail belongs to the claims being removed.
    DELETE FROM expense_logs;

    DELETE FROM expenses;

COMMIT TRANSACTION;

-- A fresh register should start numbering from 1 again. IDENT_CURRENT stays put
-- after a DELETE, so the seed is reset explicitly. Only run for a truly empty
-- table, which is what the reset above guarantees.
IF NOT EXISTS (SELECT 1 FROM expenses)
BEGIN
    DBCC CHECKIDENT ('expenses', RESEED, 0) WITH NO_INFOMSGS;
END;

IF NOT EXISTS (SELECT 1 FROM expense_logs)
BEGIN
    DBCC CHECKIDENT ('expense_logs', RESEED, 0) WITH NO_INFOMSGS;
END;

SELECT
    N'After reset' AS [stage],
    (SELECT COUNT(*) FROM expenses) AS [expenses],
    (SELECT COUNT(*) FROM expense_logs) AS [expense_logs],
    (SELECT COUNT(*) FROM media WHERE model_type = 'App\Models\Expenses') AS [attachment_rows],
    (SELECT COUNT(*) FROM expenses_types) AS [expense_types_kept];

-- The uploaded files themselves live outside the database, under the API's
-- wwwroot/uploads/expenses folder. Nothing references them once the rows above
-- are gone; clear that folder on the server separately if the disk space matters.
