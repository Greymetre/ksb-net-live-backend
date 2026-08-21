-- FieldKonnect V7.0 SQL Server release script.
--
-- No schema change. Invoice Hold is a new value (5) in the existing
-- new_invoices.approval_status column, so the only database work is the
-- permission that controls who may put an invoice on hold.
--
-- Idempotent: running it twice grants nothing twice.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 01-V7.0-update.sql
--
-- Local docker:
--   docker exec -i ksb-cloud-local-sqlserver-1 /opt/mssql-tools18/bin/sqlcmd \
--     -S localhost -U sa -P 'KsbLocal_SQL2022!Pass' -C -d ksb_pr \
--     -i backend/database/releases/V7.0/01-V7.0-update.sql

SET NOCOUNT ON;
SET XACT_ABORT ON;

SELECT
    N'FieldKonnect V7.0 database update starting.' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [started_at_utc];

BEGIN TRANSACTION;

    ------------------------------------------------------------------
    -- 1. The new_invoice_hold permission.
    --
    -- Hold has its own permission rather than riding on an approval one, so a
    -- reviewer can be allowed to park an invoice without being allowed to
    -- approve it.
    --
    -- The id is computed rather than hardcoded. The application seeder uses 727
    -- for a fresh database, but ids that came from the legacy migration differ
    -- per server and 727 is already taken on live. Nothing references this
    -- permission by id, only by name.
    --
    -- It is also written explicitly with IDENTITY_INSERT: the live column is an
    -- identity declared NOT FOR REPLICATION, which refuses an insert that omits
    -- the id. The IsIdentity guard keeps the same script working on a database
    -- where id is a plain column.
    ------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM permissions WHERE name = N'new_invoice_hold')
    BEGIN
        DECLARE @permissionId BIGINT = (SELECT ISNULL(MAX(id), 0) + 1 FROM permissions);
        DECLARE @isIdentity INT = COLUMNPROPERTY(OBJECT_ID(N'permissions'), N'id', 'IsIdentity');

        IF @isIdentity = 1 SET IDENTITY_INSERT permissions ON;

        INSERT INTO permissions (id, name, guard_name, created_at, updated_at)
        VALUES (@permissionId, N'new_invoice_hold', N'users', SYSUTCDATETIME(), SYSUTCDATETIME());

        IF @isIdentity = 1 SET IDENTITY_INSERT permissions OFF;

        PRINT CONCAT(N'Created permission new_invoice_hold with id ', @permissionId);
    END;

    ------------------------------------------------------------------
    -- 2. Grant it to superadmin and to whoever already approves at SS.
    --
    -- Those are the people who see a pending invoice first, so they are the
    -- ones who need to park it. Any other role is granted from the CRM role
    -- screen as usual.
    ------------------------------------------------------------------
    INSERT INTO role_has_permissions (permission_id, role_id)
    SELECT p.id, r.id
    FROM permissions p
    CROSS JOIN roles r
    WHERE p.name = N'new_invoice_hold'
      AND (r.name = N'superadmin'
           OR EXISTS (SELECT 1 FROM role_has_permissions rhp
                      JOIN permissions ss ON ss.id = rhp.permission_id
                      WHERE rhp.role_id = r.id AND ss.name = N'new_invoice_approve_ss'))
      AND NOT EXISTS (SELECT 1 FROM role_has_permissions existing
                      WHERE existing.permission_id = p.id AND existing.role_id = r.id);

COMMIT TRANSACTION;

SELECT
    N'FieldKonnect V7.0 database update completed successfully.' AS [message],
    DB_NAME() AS [database_name],
    CASE WHEN EXISTS (SELECT 1 FROM permissions WHERE name = N'new_invoice_hold')
         THEN N'OK' ELSE N'MISSING' END AS [new_invoice_hold_permission],
    SYSUTCDATETIME() AS [completed_at_utc];

-- ---------------------------------------------------------------------------
-- Post-deployment readiness reporting. Read-only.
-- ---------------------------------------------------------------------------

-- Roles that can now hold an invoice. Review this list and grant the permission
-- to any other reviewer role from the CRM role screen.
SELECT
    N'Roles holding new_invoice_hold' AS [check],
    r.name AS [role]
FROM role_has_permissions rhp
JOIN permissions p ON p.id = rhp.permission_id
JOIN roles r ON r.id = rhp.role_id
WHERE p.name = N'new_invoice_hold'
ORDER BY r.name;

-- Invoices already parked on hold. Zero on a first deployment.
SELECT
    N'Invoices currently on hold' AS [check],
    COUNT(*) AS [row_count]
FROM new_invoices
WHERE approval_status = 5;
