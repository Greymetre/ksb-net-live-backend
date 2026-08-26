-- FieldKonnect V7.3 SQL Server post-deployment verification.
--
-- Run this AFTER the API has been deployed and started once. It changes nothing and
-- confirms the permission rebuild completed. Every check below should read "OK".
--
-- The API applies the migration (seven nullable columns on the permissions table) and
-- syncs the catalog on its first start. If check 0 reports the columns are missing, the
-- API has not started against this database yet - start it, then run this script again.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 02-V7.3-post-deploy-verify.sql

SET NOCOUNT ON;
GO

SELECT
    N'FieldKonnect V7.3 post-deployment verification' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [checked_at_utc];
GO

-- 0. Stop early with a readable message when the migration has not been applied yet,
--    instead of letting every later check fail on an unknown column name.
IF COL_LENGTH('permissions', 'module_key') IS NULL
BEGIN
    SELECT
        N'0. Migration applied' AS [check],
        N'FAILED - the catalog columns are missing. Deploy the API and start it once, then run this script again.' AS [result];
END
ELSE
BEGIN
    SELECT
        N'0. Migration applied' AS [check],
        N'OK' AS [result];
END
GO

-- 1. The catalog columns exist, which means the migration was applied.
SELECT
    N'1. Catalog columns on permissions table' AS [check],
    CASE WHEN COUNT(*) = 7 THEN N'OK' ELSE N'FAILED - the migration did not run' END AS [result],
    COUNT(*) AS [columns_found]
FROM sys.columns
WHERE object_id = OBJECT_ID('permissions')
  AND name IN ('label', 'group_key', 'group_label', 'module_key', 'module_label', 'action_key', 'sort_order');
GO

-- 2. Every permission carries its catalog metadata and its current name.
--    Run through sp_executesql so the batch still parses when the columns are absent.
IF COL_LENGTH('permissions', 'module_key') IS NULL
    SELECT
        N'2. Permissions carrying catalog metadata' AS [check],
        N'SKIPPED - the catalog columns are missing' AS [result];
ELSE
    EXEC sp_executesql N'
        SELECT
            N''2. Permissions carrying catalog metadata'' AS [check],
            CASE WHEN COUNT(*) = 0 THEN N''OK'' ELSE N''FAILED - the catalog sync did not complete'' END AS [result],
            COUNT(*) AS [rows_without_metadata]
        FROM permissions
        WHERE module_key IS NULL OR action_key IS NULL;';
GO

SELECT
    N'3. Legacy permission names left behind' AS [check],
    CASE WHEN COUNT(*) = 0 THEN N'OK' ELSE N'FAILED - old names are still present' END AS [result],
    COUNT(*) AS [legacy_rows]
FROM permissions
WHERE name NOT LIKE '%.%';
GO

SELECT
    N'4. Total permissions' AS [check],
    CASE WHEN COUNT(*) BETWEEN 200 AND 260 THEN N'OK' ELSE N'CHECK - unexpected count' END AS [result],
    COUNT(*) AS [row_count]
FROM permissions;
GO

-- 5. Permissions are granted through roles only.
SELECT
    N'5. Direct per-user permission grants' AS [check],
    CASE WHEN COUNT(*) = 0 THEN N'OK' ELSE N'FAILED - the API did not clear them' END AS [result],
    COUNT(*) AS [row_count]
FROM model_has_permissions;
GO

-- 6. One guard for every role.
SELECT
    N'6. Roles on the users guard' AS [check],
    CASE WHEN COUNT(*) = 0 THEN N'OK' ELSE N'FAILED - a role is on another guard' END AS [result],
    COUNT(*) AS [rows_on_other_guards]
FROM roles
WHERE guard_name <> 'users';
GO

-- 7. No assignment points at a permission that no longer exists.
SELECT
    N'7. Orphan role assignments' AS [check],
    CASE WHEN COUNT(*) = 0 THEN N'OK' ELSE N'FAILED - orphan rows found' END AS [result],
    COUNT(*) AS [row_count]
FROM role_has_permissions rp
WHERE NOT EXISTS (SELECT 1 FROM permissions p WHERE p.id = rp.permission_id);
GO

-- 8. superadmin holds everything, which is what the CRM and the API assume.
SELECT
    N'8. superadmin holds every permission' AS [check],
    CASE WHEN (SELECT COUNT(*) FROM role_has_permissions rp
               INNER JOIN roles r ON r.id = rp.role_id AND r.name = 'superadmin')
            = (SELECT COUNT(*) FROM permissions) THEN N'OK' ELSE N'CHECK - superadmin is short' END AS [result],
    (SELECT COUNT(*) FROM role_has_permissions rp
      INNER JOIN roles r ON r.id = rp.role_id AND r.name = 'superadmin') AS [superadmin_permissions];
GO

-- 9. Every role, for comparison against the pre-deployment output. No role should
--    have lost the ability to reach a screen it could reach before.
SELECT
    r.id AS [role_id],
    r.name AS [role_name],
    COUNT(rp.permission_id) AS [permissions],
    (SELECT COUNT(*) FROM model_has_roles mr
      INNER JOIN users u ON u.id = mr.model_id AND u.deleted_at IS NULL
      WHERE mr.role_id = r.id) AS [users]
FROM roles r
LEFT JOIN role_has_permissions rp ON rp.role_id = r.id
GROUP BY r.id, r.name
ORDER BY r.id;
GO

-- 10. What each role may now do, module by module. Use this to spot-check a role
--     against what its people actually need.
IF COL_LENGTH('permissions', 'module_label') IS NULL
    SELECT
        N'10. Actions allowed per role and module' AS [check],
        N'SKIPPED - the catalog columns are missing' AS [result];
ELSE
    EXEC sp_executesql N'
        SELECT
            r.name AS [role_name],
            p.module_label AS [module],
            COUNT(*) AS [actions_allowed]
        FROM role_has_permissions rp
        INNER JOIN roles r ON r.id = rp.role_id
        INNER JOIN permissions p ON p.id = rp.permission_id
        WHERE r.name <> ''superadmin''
        GROUP BY r.name, p.module_label
        ORDER BY r.name, p.module_label;';
GO
