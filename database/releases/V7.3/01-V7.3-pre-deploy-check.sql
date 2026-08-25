-- FieldKonnect V7.3 SQL Server pre-deployment check.
--
-- V7.3 rebuilds roles and permissions. The API itself performs the change on its first
-- start after deployment: it applies one migration (seven nullable columns on the
-- permissions table) and then syncs the permissions table to the catalog compiled into
-- the build - renaming the permissions that survive so every role keeps what it was
-- granted, granting the newly split actions to the roles that could already perform
-- them, and deleting the rows the CRM does not enforce.
--
-- This script changes nothing. Run it BEFORE deploying and keep the output: it is what
-- the after-deployment check is compared against.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 01-V7.3-pre-deploy-check.sql

SET NOCOUNT ON;

SELECT
    N'FieldKonnect V7.3 pre-deployment check. Take a full database backup before deploying.' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [checked_at_utc];

-- ---------------------------------------------------------------------------
-- 0. A snapshot of what every role can do right now, kept in the database so
--    03-V7.3-role-comparison.sql can prove afterwards that nothing was lost.
--    Running this script again simply retakes the snapshot.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('v73_role_permission_snapshot', 'U') IS NOT NULL
    DROP TABLE v73_role_permission_snapshot;

SELECT rp.role_id, p.name AS permission_name
INTO v73_role_permission_snapshot
FROM role_has_permissions rp
INNER JOIN permissions p ON p.id = rp.permission_id;

SELECT
    N'Snapshot taken (role, permission) rows' AS [check],
    COUNT(*) AS [row_count]
FROM v73_role_permission_snapshot;

-- ---------------------------------------------------------------------------
-- 1. What the permissions table holds today.
-- ---------------------------------------------------------------------------
SELECT
    N'Permissions before deployment' AS [check],
    COUNT(*) AS [row_count]
FROM permissions;

SELECT
    N'Role assignments before deployment' AS [check],
    COUNT(*) AS [row_count]
FROM role_has_permissions;

-- ---------------------------------------------------------------------------
-- 2. Every role and how much it carries. The permission counts drop after the
--    deployment because the legacy rows a role held gated nothing; what a role
--    can actually do does not change.
-- ---------------------------------------------------------------------------
SELECT
    r.id AS [role_id],
    r.name AS [role_name],
    r.guard_name AS [guard],
    COUNT(rp.permission_id) AS [permissions],
    (SELECT COUNT(*) FROM model_has_roles mr
      INNER JOIN users u ON u.id = mr.model_id AND u.deleted_at IS NULL
      WHERE mr.role_id = r.id) AS [users]
FROM roles r
LEFT JOIN role_has_permissions rp ON rp.role_id = r.id
GROUP BY r.id, r.name, r.guard_name
ORDER BY r.id;

-- ---------------------------------------------------------------------------
-- 3. Per-user permission grants. V7.3 drops this flow: permissions come from
--    roles only, and the API empties this table on every start. Anything counted
--    here is about to be removed, so check no one depends on it.
-- ---------------------------------------------------------------------------
SELECT
    N'Direct per-user permission grants (will be removed)' AS [check],
    COUNT(*) AS [row_count]
FROM model_has_permissions;

SELECT TOP (50)
    mp.model_id AS [user_id],
    u.name AS [user_name],
    p.name AS [permission]
FROM model_has_permissions mp
LEFT JOIN users u ON u.id = mp.model_id
LEFT JOIN permissions p ON p.id = mp.permission_id
ORDER BY mp.model_id;

-- ---------------------------------------------------------------------------
-- 4. Roles on a guard other than 'users'. They are all brought onto one guard,
--    which is what the permission check has always read.
-- ---------------------------------------------------------------------------
SELECT
    N'Roles not on the users guard (will be normalised)' AS [check],
    COUNT(*) AS [row_count]
FROM roles
WHERE guard_name <> 'users';
