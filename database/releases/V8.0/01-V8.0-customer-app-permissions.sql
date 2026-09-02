/* ============================================================================
   V8.0 - Customer App Details: three permissions

   The new screen under Customers Management mirrors User App Details, so it
   carries the same three permissions, named for customers:

       customer_app.view          open the listing
       customer_app.force_logout  sign a customer out of the app
       customer_app.reset_device  clear the device id so they can sign in elsewhere

   Whoever already holds the matching user_app.* permission inherits the customer
   one, on the run that creates it and only then - so a later run never re-grants
   something an admin has deliberately taken away. superadmin always gets them.

   Safe to run more than once.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.permissions', 'U') IS NULL
   OR OBJECT_ID('dbo.role_has_permissions', 'U') IS NULL
   OR OBJECT_ID('dbo.roles', 'U') IS NULL
BEGIN
    RAISERROR('STOPPED: permissions, role_has_permissions or roles table not found - wrong database. Nothing was changed.', 16, 1);
    RETURN;
END;

IF COL_LENGTH('dbo.permissions', 'module_key') IS NULL
   OR COL_LENGTH('dbo.permissions', 'group_key') IS NULL
   OR COL_LENGTH('dbo.permissions', 'action_key') IS NULL
BEGIN
    RAISERROR('STOPPED: the permissions table has no catalog columns, so this database is still pre-V7.3. Run the V7.3 permission rebuild first. Nothing was changed.', 16, 1);
    RETURN;
END;

IF NOT EXISTS (SELECT 1 FROM dbo.permissions WHERE name = N'user_app.view')
BEGIN
    RAISERROR('STOPPED: permission "user_app.view" was not found, so the User App Details permissions this release mirrors are missing. Nothing was changed.', 16, 1);
    RETURN;
END;

BEGIN TRANSACTION;

DECLARE @created INT = 0, @inherited INT = 0, @superadmin INT = 0;

DECLARE @wanted TABLE (
    name         NVARCHAR(255) NOT NULL,
    label        NVARCHAR(255) NOT NULL,
    action_key   NVARCHAR(100) NOT NULL,
    sort_order   INT           NOT NULL,
    mirrors      NVARCHAR(255) NOT NULL
);

INSERT INTO @wanted (name, label, action_key, sort_order, mirrors) VALUES
    (N'customer_app.view',         N'View Listing',       N'view',         1520, N'user_app.view'),
    (N'customer_app.force_logout', N'Force Logout',       N'force_logout', 1530, N'user_app.force_logout'),
    (N'customer_app.reset_device', N'Remove Device UUID', N'reset_device', 1540, N'user_app.reset_device');

/* Which of them this run introduces. Read before anything is written, because the
   inherit step below must happen once only. */
DECLARE @new TABLE (name NVARCHAR(255) NOT NULL);
INSERT INTO @new (name)
SELECT w.name FROM @wanted w
WHERE NOT EXISTS (SELECT 1 FROM dbo.permissions p WHERE p.name = w.name);

/* 1. Create the missing ones. */
INSERT INTO dbo.permissions
    (name, guard_name, label, group_key, group_label, module_key, module_label, action_key, sort_order, created_at, updated_at)
SELECT w.name, N'users', w.label, N'customers', N'Customers Management',
       N'customer_app', N'Customer App Details', w.action_key, w.sort_order,
       SYSUTCDATETIME(), SYSUTCDATETIME()
FROM @wanted w
WHERE w.name IN (SELECT name FROM @new);
SET @created = @@ROWCOUNT;

/* 2. Refresh the catalog metadata on the ones that already existed, so the role
      matrix groups them correctly however they were first created. */
UPDATE p
SET label        = w.label,
    guard_name   = N'users',
    group_key    = N'customers',
    group_label  = N'Customers Management',
    module_key   = N'customer_app',
    module_label = N'Customer App Details',
    action_key   = w.action_key,
    sort_order   = w.sort_order,
    updated_at   = SYSUTCDATETIME()
FROM dbo.permissions p
INNER JOIN @wanted w ON w.name = p.name
WHERE w.name NOT IN (SELECT name FROM @new);

IF EXISTS (SELECT 1 FROM @wanted w WHERE NOT EXISTS (SELECT 1 FROM dbo.permissions p WHERE p.name = w.name))
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR('STOPPED: the customer_app permissions could not be created. Nothing was changed.', 16, 1);
    RETURN;
END;

/* 3. Roles holding the matching user_app permission inherit the customer one -
      only on the run that creates it. */
INSERT INTO dbo.role_has_permissions (role_id, permission_id)
SELECT DISTINCT rp.role_id, np.id
FROM @wanted w
INNER JOIN @new n              ON n.name = w.name
INNER JOIN dbo.permissions np  ON np.name = w.name
INNER JOIN dbo.permissions mp  ON mp.name = w.mirrors
INNER JOIN dbo.role_has_permissions rp ON rp.permission_id = mp.id
WHERE NOT EXISTS (SELECT 1 FROM dbo.role_has_permissions existing
                  WHERE existing.role_id = rp.role_id AND existing.permission_id = np.id);
SET @inherited = @@ROWCOUNT;

/* 4. superadmin holds everything, which is what the CRM and the API assume. */
INSERT INTO dbo.role_has_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM dbo.roles r
CROSS JOIN dbo.permissions p
WHERE r.name = 'superadmin'
  AND p.name IN (SELECT name FROM @wanted)
  AND NOT EXISTS (SELECT 1 FROM dbo.role_has_permissions rp
                  WHERE rp.role_id = r.id AND rp.permission_id = p.id);
SET @superadmin = @@ROWCOUNT;

COMMIT TRANSACTION;

SELECT N'Permissions created by this run'        AS [step], CAST(@created    AS NVARCHAR(20)) AS [value]
UNION ALL SELECT N'Grants inherited from user_app.*',        CAST(@inherited  AS NVARCHAR(20))
UNION ALL SELECT N'superadmin grants added',                 CAST(@superadmin AS NVARCHAR(20));

/* What the role matrix will show for the new module. */
SELECT p.module_label AS [module], p.action_key AS [action], p.name AS [permission],
       (SELECT COUNT(*) FROM dbo.role_has_permissions rp WHERE rp.permission_id = p.id) AS [roles_holding_it]
FROM dbo.permissions p
WHERE p.module_key = N'customer_app'
ORDER BY p.sort_order;
