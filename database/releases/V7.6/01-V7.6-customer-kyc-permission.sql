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
-- permissions and role_has_permissions only. No business data: no customer,
-- order, visit, expense or invoice table is referenced anywhere below.
--
-- BEFORE IT CHANGES ANYTHING it checks that the database is the one this script
-- expects - the tables are there, the V7.3 catalog columns are there, and the
-- V7.3 permission names are in place. If any of that is missing it stops and
-- says so, having written nothing.
--
-- It runs in ONE transaction. Any error rolls the whole thing back, so the
-- database is either fully updated or exactly as it was. It is safe to run
-- twice: the second run finds the permission in place and grants nothing
-- further, so a permission an admin has since unticked stays unticked.
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

-- Everything below is one batch on purpose: a failed check must stop the whole
-- script, and a RETURN only ends the batch it sits in.
BEGIN
    -- ---------------------------------------------------------------------
    -- CHECKS. Nothing is written until all of these pass.
    -- ---------------------------------------------------------------------
    IF OBJECT_ID('dbo.permissions', 'U') IS NULL
    BEGIN
        RAISERROR('STOPPED: table "permissions" was not found. Is this the FieldKonnect database?', 16, 1);
        RETURN;
    END;

    IF OBJECT_ID('dbo.role_has_permissions', 'U') IS NULL
    BEGIN
        RAISERROR('STOPPED: table "role_has_permissions" was not found.', 16, 1);
        RETURN;
    END;

    IF OBJECT_ID('dbo.roles', 'U') IS NULL
    BEGIN
        RAISERROR('STOPPED: table "roles" was not found.', 16, 1);
        RETURN;
    END;

    IF COL_LENGTH('dbo.permissions', 'label')        IS NULL
       OR COL_LENGTH('dbo.permissions', 'group_key')    IS NULL
       OR COL_LENGTH('dbo.permissions', 'group_label')  IS NULL
       OR COL_LENGTH('dbo.permissions', 'module_key')   IS NULL
       OR COL_LENGTH('dbo.permissions', 'module_label') IS NULL
       OR COL_LENGTH('dbo.permissions', 'action_key')   IS NULL
       OR COL_LENGTH('dbo.permissions', 'sort_order')   IS NULL
    BEGIN
        RAISERROR('STOPPED: the permissions table is missing the catalog columns. Run 04-V7.3-permission-rebuild.sql from the V7.3 package first. Nothing was changed.', 16, 1);
        RETURN;
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.permissions WHERE name = N'customer.view')
    BEGIN
        RAISERROR('STOPPED: permission "customer.view" was not found, so this database still holds the pre-V7.3 permission names. Run 04-V7.3-permission-rebuild.sql from the V7.3 package first. Nothing was changed.', 16, 1);
        RETURN;
    END;

    -- ---------------------------------------------------------------------
    -- THE WORK.
    -- ---------------------------------------------------------------------
    BEGIN TRANSACTION;

    -- Whether this run is the one that introduces the permission. Read before
    -- anything is written, because the back-fill below must happen once only.
    DECLARE @is_new BIT =
        CASE WHEN EXISTS (SELECT 1 FROM dbo.permissions WHERE name = N'customer_kyc.view') THEN 0 ELSE 1 END;

    DECLARE @relabelled INT = 0, @granted INT = 0, @superadmin_granted INT = 0;

    -- STEP 1. The customer.* rows become the Master module.
    UPDATE dbo.permissions
    SET module_label = N'Master',
        updated_at   = SYSUTCDATETIME()
    WHERE module_key = N'customer'
      AND (module_label IS NULL OR module_label <> N'Master');
    SET @relabelled = @@ROWCOUNT;

    -- STEP 2. The KYC menu's own permission.
    IF @is_new = 1
        INSERT INTO dbo.permissions
            (name, guard_name, label, group_key, group_label, module_key, module_label, action_key, sort_order, created_at, updated_at)
        VALUES
            (N'customer_kyc.view', N'users', N'View Listing', N'customers', N'Customers Management',
             N'customer_kyc', N'KYC', N'view', 175, SYSUTCDATETIME(), SYSUTCDATETIME());
    ELSE
        -- Already there from an earlier run: only the catalog metadata is refreshed.
        UPDATE dbo.permissions
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

    -- The id column is decimal(20,0), so the variables match it rather than
    -- relying on a conversion.
    DECLARE @kyc_view_id   DECIMAL(20, 0) = (SELECT TOP (1) id FROM dbo.permissions WHERE name = N'customer_kyc.view' ORDER BY id);
    DECLARE @kyc_review_id DECIMAL(20, 0) = (SELECT TOP (1) id FROM dbo.permissions WHERE name = N'customer.kyc_review' ORDER BY id);

    IF @kyc_view_id IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        RAISERROR('STOPPED: customer_kyc.view could not be created. Nothing was changed.', 16, 1);
        RETURN;
    END;

    -- STEP 3. Who gets it. Only on the run that creates it.
    IF @is_new = 1 AND @kyc_review_id IS NOT NULL
    BEGIN
        INSERT INTO dbo.role_has_permissions (role_id, permission_id)
        SELECT DISTINCT rp.role_id, @kyc_view_id
        FROM dbo.role_has_permissions rp
        WHERE rp.permission_id = @kyc_review_id
          AND NOT EXISTS (SELECT 1 FROM dbo.role_has_permissions existing
                          WHERE existing.role_id = rp.role_id AND existing.permission_id = @kyc_view_id);
        SET @granted = @@ROWCOUNT;
    END;

    -- superadmin holds everything, which is what the CRM and the API assume.
    INSERT INTO dbo.role_has_permissions (role_id, permission_id)
    SELECT r.id, @kyc_view_id
    FROM dbo.roles r
    WHERE r.name = 'superadmin'
      AND NOT EXISTS (SELECT 1 FROM dbo.role_has_permissions rp
                      WHERE rp.role_id = r.id AND rp.permission_id = @kyc_view_id);
    SET @superadmin_granted = @@ROWCOUNT;

    COMMIT TRANSACTION;

    SELECT N'Was the permission created by this run'  AS [step], CASE WHEN @is_new = 1 THEN N'yes' ELSE N'no - already present' END AS [value]
    UNION ALL SELECT N'customer.* rows relabelled to Master',    CAST(@relabelled AS NVARCHAR(20))
    UNION ALL SELECT N'Roles that inherited it from KYC review',  CAST(@granted AS NVARCHAR(20))
    UNION ALL SELECT N'superadmin grants added',                  CAST(@superadmin_granted AS NVARCHAR(20));

    -- What the role matrix will show for Customers Management.
    SELECT p.module_label AS [module], p.action_key AS [action], p.name AS [permission],
           (SELECT COUNT(*) FROM dbo.role_has_permissions rp WHERE rp.permission_id = p.id) AS [roles_holding_it]
    FROM dbo.permissions p
    WHERE p.group_key = N'customers'
    ORDER BY p.sort_order;
END;
GO
