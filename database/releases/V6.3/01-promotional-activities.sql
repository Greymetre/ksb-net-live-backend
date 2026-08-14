-- V6.3 additive-only SQL Server script. Existing live data is not updated or deleted.
IF OBJECT_ID(N'promotional_activities', N'U') IS NULL BEGIN
CREATE TABLE promotional_activities (id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY, activity_type NVARCHAR(20) NOT NULL, activity_name NVARCHAR(150) NOT NULL, activity_date DATE NOT NULL, user_id BIGINT NOT NULL, created_by_id BIGINT NOT NULL DEFAULT 0, branch_id BIGINT NULL, zone NVARCHAR(100) NULL, reporting_manager_id BIGINT NULL, distributor_id BIGINT NULL, distributor_name NVARCHAR(255) NULL, dealer_name NVARCHAR(255) NULL, hotel_name NVARCHAR(255) NULL, location_lat DECIMAL(10,7) NULL, location_lng DECIMAL(10,7) NULL, location_text NVARCHAR(500) NULL, gift_count INT NOT NULL DEFAULT 0, total_expense DECIMAL(18,2) NOT NULL DEFAULT 0, dealer_share_amount DECIMAL(18,2) NOT NULL DEFAULT 0, feedback NVARCHAR(MAX) NULL, status NVARCHAR(20) NOT NULL DEFAULT 'draft', created_at DATETIME2 NULL, updated_at DATETIME2 NULL, deleted_at DATETIME2 NULL, CONSTRAINT CK_promotional_activities_type CHECK(activity_type IN ('nukkad','retailer','farmer','influencer')), CONSTRAINT CK_promotional_activities_status CHECK(status IN ('draft','submitted')));
CREATE INDEX IX_promotional_activities_user_date ON promotional_activities(user_id, activity_date DESC); END;
IF OBJECT_ID(N'promotional_activity_participants', N'U') IS NULL CREATE TABLE promotional_activity_participants (id BIGINT IDENTITY(1,1) PRIMARY KEY, activity_id BIGINT NOT NULL REFERENCES promotional_activities(id) ON DELETE CASCADE, name NVARCHAR(255), shop_name NVARCHAR(255), proprietor_name NVARCHAR(255), profession NVARCHAR(100), mobile NVARCHAR(20), gift_name NVARCHAR(255), remarks NVARCHAR(MAX), is_influencer BIT NOT NULL DEFAULT 0, social_type NVARCHAR(50), social_link NVARCHAR(500), created_at DATETIME2, updated_at DATETIME2, deleted_at DATETIME2);
IF OBJECT_ID(N'promotional_activity_expenses', N'U') IS NULL CREATE TABLE promotional_activity_expenses (id BIGINT IDENTITY(1,1) PRIMARY KEY, activity_id BIGINT NOT NULL REFERENCES promotional_activities(id) ON DELETE CASCADE, expense_type NVARCHAR(50) NOT NULL, total_amount DECIMAL(18,2) NOT NULL DEFAULT 0, dealer_share_amount DECIMAL(18,2) NOT NULL DEFAULT 0, dealer_share_pct DECIMAL(7,2) NOT NULL DEFAULT 0, remarks NVARCHAR(MAX), invoice_url NVARCHAR(500), created_at DATETIME2, updated_at DATETIME2, deleted_at DATETIME2);
IF OBJECT_ID(N'promotional_activity_photos', N'U') IS NULL CREATE TABLE promotional_activity_photos (id BIGINT IDENTITY(1,1) PRIMARY KEY, activity_id BIGINT NOT NULL REFERENCES promotional_activities(id) ON DELETE CASCADE, photo_url NVARCHAR(500) NOT NULL, latitude DECIMAL(10,7) NOT NULL, longitude DECIMAL(10,7) NOT NULL, taken_at DATETIME2, created_at DATETIME2, updated_at DATETIME2, deleted_at DATETIME2);
IF COL_LENGTH(N'promotional_activities', N'created_by_id') IS NULL ALTER TABLE promotional_activities ADD created_by_id BIGINT NOT NULL CONSTRAINT DF_promotional_activities_created_by_id DEFAULT 0;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_promotional_activities_created_by_date' AND object_id=OBJECT_ID(N'promotional_activities')) CREATE INDEX IX_promotional_activities_created_by_date ON promotional_activities(created_by_id, activity_date DESC);

-- Activity Reports permissions. Additive only; no existing role or live business data is changed.
DECLARE @ActivityReportPermissions TABLE ([name] NVARCHAR(255) NOT NULL PRIMARY KEY);
INSERT INTO @ActivityReportPermissions ([name]) VALUES
(N'activity_report_access'),
(N'activity_report_sales_engineer_download'),
(N'activity_report_distributor_download'),
(N'activity_report_gift_summary_download');

INSERT INTO permissions ([name], guard_name, created_at, updated_at)
SELECT p.[name], N'users', SYSDATETIME(), SYSDATETIME()
FROM @ActivityReportPermissions p
WHERE NOT EXISTS (SELECT 1 FROM permissions existing WHERE existing.[name] = p.[name] AND existing.guard_name = N'users');

INSERT INTO role_has_permissions (permission_id, role_id)
SELECT permission.id, role.id
FROM permissions permission CROSS JOIN roles role
WHERE permission.[name] IN (SELECT [name] FROM @ActivityReportPermissions)
  AND permission.guard_name = N'users'
  AND LOWER(REPLACE(REPLACE(role.[name], N' ', N''), N'_', N'')) = N'superadmin'
  AND NOT EXISTS (SELECT 1 FROM role_has_permissions assigned WHERE assigned.permission_id = permission.id AND assigned.role_id = role.id);
