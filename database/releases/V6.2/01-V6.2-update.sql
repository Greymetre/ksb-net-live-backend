/* FieldKonnect V6.2 - cumulative, idempotent live database update. */
USE [ksb_pr];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF OBJECT_ID(N'dbo.permissions', N'U') IS NULL THROW 51000, 'Required dbo.permissions table was not found.', 1;
IF OBJECT_ID(N'dbo.roles', N'U') IS NULL THROW 51000, 'Required dbo.roles table was not found.', 1;
IF OBJECT_ID(N'dbo.role_has_permissions', N'U') IS NULL THROW 51000, 'Required dbo.role_has_permissions table was not found.', 1;
IF OBJECT_ID(N'dbo.users', N'U') IS NULL THROW 51000, 'Required dbo.users table was not found.', 1;
GO

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @RequiredPermissions TABLE ([name] nvarchar(255) NOT NULL PRIMARY KEY);
  INSERT INTO @RequiredPermissions ([name]) VALUES
    (N'asm_rating_report'), (N'order_dispatch'),
    (N'retailer_reject'), (N'retailer_pending'),
    (N'country_active'), (N'state_active'), (N'district_active'),
    (N'city_active'), (N'pincode_active'),
    (N'user_app_force_logout'), (N'user_app_uuid_reset'),
    (N'attendance_delete'), (N'new_invoice_export'),
    (N'scheme_draft'), (N'scheme_submit'), (N'scheme_approve'),
    (N'scheme_reject'), (N'scheme_publish');

  INSERT INTO dbo.permissions ([name], guard_name, created_at, updated_at)
  SELECT required.[name], N'users', SYSDATETIME(), SYSDATETIME()
  FROM @RequiredPermissions required
  WHERE NOT EXISTS (
    SELECT 1 FROM dbo.permissions existing
    WHERE existing.[name] = required.[name]
      AND existing.guard_name = N'users'
  );

  INSERT INTO dbo.role_has_permissions (permission_id, role_id)
  SELECT permission.id, role.id
  FROM dbo.permissions permission
  CROSS JOIN dbo.roles role
  WHERE permission.[name] IN (SELECT [name] FROM @RequiredPermissions)
    AND permission.guard_name = N'users'
    AND LOWER(REPLACE(REPLACE(role.[name], N' ', N''), N'_', N'')) = N'superadmin'
    AND NOT EXISTS (
      SELECT 1 FROM dbo.role_has_permissions assigned
      WHERE assigned.permission_id = permission.id
        AND assigned.role_id = role.id
    );

  /* Explicit V6.2 one-user restoration. No other user field is changed. */
  UPDATE dbo.users
  SET deleted_at = NULL,
      updated_at = SYSDATETIME()
  WHERE REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(mobile)), N' ', N''), N'-', N''), N'+91', N'') = N'9920037907'
    AND deleted_at IS NOT NULL;

  COMMIT TRANSACTION;
END TRY
BEGIN CATCH
  IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
GO

SELECT required.[name] AS required_permission,
       CASE WHEN permission.id IS NULL THEN N'MISSING' ELSE N'OK' END AS verification_status
FROM (VALUES
  (N'asm_rating_report'), (N'order_dispatch'),
  (N'retailer_reject'), (N'retailer_pending'),
  (N'country_active'), (N'state_active'), (N'district_active'),
  (N'city_active'), (N'pincode_active'),
  (N'user_app_force_logout'), (N'user_app_uuid_reset'),
  (N'attendance_delete'), (N'new_invoice_export'),
  (N'scheme_draft'), (N'scheme_submit'), (N'scheme_approve'),
  (N'scheme_reject'), (N'scheme_publish')
) required([name])
LEFT JOIN dbo.permissions permission
  ON permission.[name] = required.[name]
 AND permission.guard_name = N'users'
ORDER BY required.[name];
GO

SELECT id, name, mobile, active, deleted_at
FROM dbo.users
WHERE REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(mobile)), N' ', N''), N'-', N''), N'+91', N'') = N'9920037907';
GO

PRINT 'FieldKonnect V6.2 database update completed successfully.';
GO
