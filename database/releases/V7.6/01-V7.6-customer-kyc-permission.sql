-- FieldKonnect V7.6 - Customers Management becomes two menus: Master and KYC.
--
-- WHAT THIS IS
-- The permission side of the split, in pure SQL. No seeder runs on live, so this
-- script does what a bootstrap-enabled start would have done:
--   1. the existing customer.* permissions are relabelled as the "Master" module,
--      so the role matrix reads the way the menu now reads. Their names do not
--      change, so no role gains or loses anything from this step.
--   2. one new permission, customer_kyc.view, gates the new KYC menu.
--   3. it is granted to superadmin, and - only on the run that creates it - to
--      every role that already holds customer.kyc_review, so the people who
--      review KYC today can open the new screen without being re-granted.
--
-- WHAT IT TOUCHES
-- permissions and role_has_permissions only. No business data.
--
-- It runs in ONE transaction and is safe to run twice: the second run finds the
-- permission in place and grants nothing further, so a permission an admin has
-- since unticked stays unticked.
--
-- TAKE A FULL DATABASE BACKUP FIRST, then run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 01-V7.6-customer-kyc-permission.sql
--
-- Restart the API (IIS app pool recycle) after this completes.

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

PRINT '== FieldKonnect V7.6 - Customers Management: Master + KYC ==';
GO

BEGIN TRANSACTION;

-- Whether this run is the one that introduces the permission. Read before anything
-- is written, because the back-fill below must happen once and never again.
DECLARE @is_new BIT =
    CASE WHEN EXISTS (SELECT 1 FROM permissions WHERE name = N'customer_kyc.view') THEN 0 ELSE 1 END;

DECLARE @relabelled INT = 0, @granted INT = 0, @superadmin_granted INT = 0;

-- ---------------------------------------------------------------------------
-- STEP 1. The customer.* rows become the Master module.
-- ---------------------------------------------------------------------------
UPDATE permissions
SET module_label = N'Master',
    updated_at   = SYSUTCDATETIME()
WHERE module_key = N'customer'
  AND (module_label IS NULL OR module_label <> N'Master');
SET @relabelled = @@ROWCOUNT;

-- ---------------------------------------------------------------------------
-- STEP 2. The KYC menu's own permission.
-- ---------------------------------------------------------------------------
IF @is_new = 1
    INSERT INTO permissions
        (name, guard_name, label, group_key, group_label, module_key, module_label, action_key, sort_order, created_at, updated_at)
    VALUES
        (N'customer_kyc.view', N'users', N'View Listing', N'customers', N'Customers Management',
         N'customer_kyc', N'KYC', N'view', 175, SYSUTCDATETIME(), SYSUTCDATETIME());
ELSE
    -- Already there from an earlier run: only the catalog metadata is refreshed.
    UPDATE permissions
    SET label        = N'View Listing',
        group_key    = N'customers',
        group_label  = N'Customers Management',
        module_key   = N'customer_kyc',
        module_label = N'KYC',
        action_key   = N'view',
        sort_order   = 175,
        guard_name   = N'users',
        updated_at   = SYSUTCDATETIME()
    WHERE name = N'customer_kyc.view';

DECLARE @kyc_view_id BIGINT = (SELECT id FROM permissions WHERE name = N'customer_kyc.view');
DECLARE @kyc_review_id BIGINT = (SELECT id FROM permissions WHERE name = N'customer.kyc_review');

IF @kyc_view_id IS NULL
BEGIN
    ROLLBACK TRANSACTION;
    RAISERROR('customer_kyc.view could not be created - nothing was changed.', 16, 1);
    RETURN;
END;

-- ---------------------------------------------------------------------------
-- STEP 3. Who gets it. Only on the run that creates it.
-- ---------------------------------------------------------------------------
IF @is_new = 1 AND @kyc_review_id IS NOT NULL
BEGIN
    INSERT INTO role_has_permissions (role_id, permission_id)
    SELECT DISTINCT rp.role_id, @kyc_view_id
    FROM role_has_permissions rp
    WHERE rp.permission_id = @kyc_review_id
      AND NOT EXISTS (SELECT 1 FROM role_has_permissions existing
                      WHERE existing.role_id = rp.role_id AND existing.permission_id = @kyc_view_id);
    SET @granted = @@ROWCOUNT;
END;

-- superadmin holds everything, which is what the CRM and the API assume.
INSERT INTO role_has_permissions (role_id, permission_id)
SELECT r.id, @kyc_view_id
FROM roles r
WHERE r.name = 'superadmin'
  AND NOT EXISTS (SELECT 1 FROM role_has_permissions rp
                  WHERE rp.role_id = r.id AND rp.permission_id = @kyc_view_id);
SET @superadmin_granted = @@ROWCOUNT;

COMMIT TRANSACTION;

SELECT N'Was the permission created by this run' AS [step], CASE WHEN @is_new = 1 THEN N'yes' ELSE N'no - already present' END AS [value]
UNION ALL SELECT N'customer.* rows relabelled to Master',   CAST(@relabelled AS NVARCHAR(20))
UNION ALL SELECT N'Roles that inherited it from KYC review', CAST(@granted AS NVARCHAR(20))
UNION ALL SELECT N'superadmin grants added',                 CAST(@superadmin_granted AS NVARCHAR(20));
GO

-- ---------------------------------------------------------------------------
-- STEP 4. What the role matrix will show for Customers Management.
-- ---------------------------------------------------------------------------
SELECT p.module_label AS [module], p.action_key AS [action], p.name AS [permission],
       (SELECT COUNT(*) FROM role_has_permissions rp WHERE rp.permission_id = p.id) AS [roles_holding_it]
FROM permissions p
WHERE p.group_key = N'customers'
ORDER BY p.sort_order;
GO
