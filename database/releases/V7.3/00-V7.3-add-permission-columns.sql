-- FieldKonnect V7.3 - add the permission catalog columns by hand.
--
-- WHAT THIS IS FOR
-- The API cannot read the permissions table until seven columns exist, so login fails
-- with "Invalid column name" while they are missing. Normally the API adds them itself
-- on its first start, but that only happens when SKIP_DB_BOOTSTRAP is not set to true.
-- This script does exactly the same thing the API would do, under your control.
--
-- WHAT IT TOUCHES
-- The permissions table only, and one row in __EFMigrationsHistory. It ADDS columns.
-- It does not update, delete or move a single row of business data. Customers, orders,
-- visits, expenses and invoices are not referenced anywhere in this script.
--
-- It is safe to run twice: every step checks first and skips work already done.
--
-- Take a database backup first, then run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 00-V7.3-add-permission-columns.sql

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

SELECT
    N'FieldKonnect V7.3 - adding permission catalog columns' AS [message],
    DB_NAME() AS [database_name],
    (SELECT COUNT(*) FROM permissions) AS [permissions_rows_before];
GO

BEGIN TRANSACTION;

IF COL_LENGTH('permissions', 'label') IS NULL
    ALTER TABLE permissions ADD [label] NVARCHAR(255) NULL;

IF COL_LENGTH('permissions', 'group_key') IS NULL
    ALTER TABLE permissions ADD [group_key] NVARCHAR(64) NULL;

IF COL_LENGTH('permissions', 'group_label') IS NULL
    ALTER TABLE permissions ADD [group_label] NVARCHAR(255) NULL;

IF COL_LENGTH('permissions', 'module_key') IS NULL
    ALTER TABLE permissions ADD [module_key] NVARCHAR(64) NULL;

IF COL_LENGTH('permissions', 'module_label') IS NULL
    ALTER TABLE permissions ADD [module_label] NVARCHAR(255) NULL;

IF COL_LENGTH('permissions', 'action_key') IS NULL
    ALTER TABLE permissions ADD [action_key] NVARCHAR(64) NULL;

-- The API declares sort_order as NOT NULL with a default of 0, so existing rows get 0.
IF COL_LENGTH('permissions', 'sort_order') IS NULL
    ALTER TABLE permissions ADD [sort_order] INT NOT NULL CONSTRAINT DF_permissions_sort_order DEFAULT 0;

COMMIT TRANSACTION;
GO

-- Record the migration so the API does not try to add these columns again and fail.
IF OBJECT_ID('__EFMigrationsHistory', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM __EFMigrationsHistory
                   WHERE MigrationId = N'20260825103753_AddPermissionCatalogMetadata')
BEGIN
    INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
    VALUES (N'20260825103753_AddPermissionCatalogMetadata', N'8.0.13');
END
GO

-- Confirm the result. Both checks must read OK before the API is restarted.
SELECT
    N'Catalog columns present' AS [check],
    CASE WHEN COUNT(*) = 7 THEN N'OK' ELSE N'FAILED - a column is still missing' END AS [result],
    COUNT(*) AS [columns_found]
FROM sys.columns
WHERE object_id = OBJECT_ID('permissions')
  AND name IN ('label', 'group_key', 'group_label', 'module_key', 'module_label', 'action_key', 'sort_order');
GO

SELECT
    N'Migration recorded' AS [check],
    CASE WHEN COUNT(*) = 1 THEN N'OK' ELSE N'FAILED - the API will retry the migration' END AS [result]
FROM __EFMigrationsHistory
WHERE MigrationId = N'20260825103753_AddPermissionCatalogMetadata';
GO

SELECT
    N'Permission rows (must match the count shown at the top)' AS [check],
    COUNT(*) AS [permissions_rows_after]
FROM permissions;
GO
