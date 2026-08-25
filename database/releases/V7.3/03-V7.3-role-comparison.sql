-- FieldKonnect V7.3 SQL Server role comparison.
--
-- Answers one question: did any role lose something in the permission rebuild?
--
-- It reads the snapshot 01-V7.3-pre-deploy-check.sql stored before the deployment,
-- maps every permission a role held onto the permission that replaced it, and compares
-- that with what the role holds now. It changes nothing.
--
-- Run AFTER deploying and starting the API once:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 03-V7.3-role-comparison.sql
--
-- Read the result this way:
--   MISSING - the role should hold this permission and does not. Nothing should appear
--             here; if something does, grant it on the Roles page and tell the developer.
--   EXTRA   - the role holds a permission the mapping did not predict. Expected for
--             superadmin, which always holds everything, and for anything granted by
--             hand after the deployment.

SET NOCOUNT ON;

IF OBJECT_ID('v73_role_permission_snapshot', 'U') IS NULL
BEGIN
    SELECT N'No snapshot found. 01-V7.3-pre-deploy-check.sql was not run before the deployment, so this comparison cannot be made.' AS [message];
    RETURN;
END;

-- Old permission name -> the permission that now covers it. Generated from the
-- catalog compiled into the V7.3 build.
DECLARE @map TABLE (source_name NVARCHAR(255), target_name NVARCHAR(255));
INSERT INTO @map (source_name, target_name) VALUES
    (N'ASR_report_Download', N'asr_performance_report.export'),
    (N'activity_report_access', N'activity_report.view'),
    (N'activity_report_distributor_download', N'activity_report.export_distributor'),
    (N'activity_report_gift_summary_download', N'activity_report.export_gift_summary'),
    (N'activity_report_sales_engineer_download', N'activity_report.export_sales_engineer'),
    (N'asm_rating_detailed_download', N'rating_report.export'),
    (N'asm_rating_download', N'rating_report.export'),
    (N'asm_rating_report', N'rating_report.view'),
    (N'attendance_delete', N'attendance.delete'),
    (N'attendance_report', N'attendance.approve'),
    (N'attendance_report', N'attendance.export'),
    (N'attendance_report', N'attendance.punch_in'),
    (N'attendance_report', N'attendance.punch_out'),
    (N'attendance_report', N'attendance.reject'),
    (N'attendance_report', N'attendance.view'),
    (N'attendance_summary_report', N'attendance_summary.export'),
    (N'attendance_summary_report', N'attendance_summary.view'),
    (N'beat_access', N'beat.view'),
    (N'beat_create', N'beat.create'),
    (N'beat_delete', N'beat.delete'),
    (N'beat_edit', N'beat.edit'),
    (N'beat_show', N'beat.detail'),
    (N'beatdetail_access', N'beat_detail.view'),
    (N'branch', N'branch.active'),
    (N'branch', N'branch.create'),
    (N'branch', N'branch.delete'),
    (N'branch', N'branch.edit'),
    (N'branch', N'branch.view'),
    (N'branch_report_download', N'branch.export'),
    (N'category_access', N'segment.view'),
    (N'category_active', N'segment.active'),
    (N'category_create', N'segment.create'),
    (N'category_delete', N'segment.delete'),
    (N'category_download', N'segment.export'),
    (N'category_edit', N'segment.edit'),
    (N'category_template', N'segment.template'),
    (N'category_upload', N'segment.import'),
    (N'checkin_access', N'checkin.view'),
    (N'checkin_download', N'checkin.export'),
    (N'city_access', N'city.view'),
    (N'city_active', N'city.active'),
    (N'city_assigned', N'city_assignment.create'),
    (N'city_assigned', N'city_assignment.delete'),
    (N'city_assigned', N'city_assignment.export'),
    (N'city_assigned', N'city_assignment.import'),
    (N'city_assigned', N'city_assignment.template'),
    (N'city_assigned', N'city_assignment.view'),
    (N'city_create', N'city.create'),
    (N'city_delete', N'city.delete'),
    (N'city_download', N'city.export'),
    (N'city_edit', N'city.edit'),
    (N'city_template', N'city.template'),
    (N'city_upload', N'city.import'),
    (N'country_access', N'country.view'),
    (N'country_active', N'country.active'),
    (N'country_create', N'country.create'),
    (N'country_delete', N'country.delete'),
    (N'country_download', N'country.export'),
    (N'country_edit', N'country.edit'),
    (N'country_template', N'country.template'),
    (N'country_upload', N'country.import'),
    (N'customer_access', N'customer.view'),
    (N'customer_active', N'customer.active'),
    (N'customer_create', N'customer.create'),
    (N'customer_delete', N'customer.delete'),
    (N'customer_download', N'customer.export'),
    (N'customer_edit', N'customer.edit'),
    (N'customer_kyc_access', N'customer.kyc_review'),
    (N'customer_show', N'customer.detail'),
    (N'customer_template', N'customer.template'),
    (N'customer_upload', N'customer.import'),
    (N'customers_report', N'customer.export'),
    (N'dashboard_access', N'dashboard.view'),
    (N'dashboard_activity', N'dashboard.activity'),
    (N'dashboard_loyalty', N'dashboard.loyalty'),
    (N'dashboard_secondary', N'dashboard.secondary_sales'),
    (N'dealer_portal_setting_access', N'dealer_portal_setting.view'),
    (N'department_report_download', N'department.export'),
    (N'departments', N'department.active'),
    (N'departments', N'department.create'),
    (N'departments', N'department.delete'),
    (N'departments', N'department.edit'),
    (N'departments', N'department.view'),
    (N'designation', N'designation.active'),
    (N'designation', N'designation.create'),
    (N'designation', N'designation.delete'),
    (N'designation', N'designation.edit'),
    (N'designation', N'designation.export'),
    (N'designation', N'designation.view'),
    (N'district_access', N'district.view'),
    (N'district_active', N'district.active'),
    (N'district_create', N'district.create'),
    (N'district_delete', N'district.delete'),
    (N'district_download', N'district.export'),
    (N'district_edit', N'district.edit'),
    (N'district_template', N'district.template'),
    (N'district_upload', N'district.import'),
    (N'division', N'zone.active'),
    (N'division', N'zone.create'),
    (N'division', N'zone.delete'),
    (N'division', N'zone.edit'),
    (N'division', N'zone.view'),
    (N'division_report_download', N'zone.export'),
    (N'expense_access', N'expense.view'),
    (N'expense_checked', N'expense.check'),
    (N'expenses_authority', N'expense.approve'),
    (N'expenses_create', N'expense.create'),
    (N'expenses_delete', N'expense.delete'),
    (N'expenses_edit', N'expense.edit'),
    (N'expenses_type', N'expense_type.view'),
    (N'expenses_type_create', N'expense_type.create'),
    (N'expenses_type_update', N'expense_type.active'),
    (N'expenses_type_update', N'expense_type.delete'),
    (N'expenses_type_update', N'expense_type.edit'),
    (N'field_konnect_app_setting_access', N'app_setting.edit'),
    (N'field_konnect_app_setting_access', N'app_setting.view'),
    (N'holiday_access', N'holiday.create'),
    (N'holiday_access', N'holiday.delete'),
    (N'holiday_access', N'holiday.edit'),
    (N'holiday_access', N'holiday.export'),
    (N'holiday_access', N'holiday.view'),
    (N'invoice_transaction.view', N'invoice_transaction.detail'),
    (N'leave_access', N'leave.approve'),
    (N'leave_access', N'leave.comp_off'),
    (N'leave_access', N'leave.create'),
    (N'leave_access', N'leave.delete'),
    (N'leave_access', N'leave.export'),
    (N'leave_access', N'leave.reject'),
    (N'leave_access', N'leave.view'),
    (N'loyalty_app_setting_access', N'app_setting.edit'),
    (N'loyalty_app_setting_access', N'app_setting.view'),
    (N'market_intelligence_access', N'market_intelligence_report.view'),
    (N'market_intelligence_report_download', N'market_intelligence_report.export'),
    (N'new_invoice_access', N'invoice_transaction.detail'),
    (N'new_invoice_access', N'invoice_transaction.view'),
    (N'new_invoice_approve_ho', N'invoice_transaction.approve_ho'),
    (N'new_invoice_approve_sales', N'invoice_transaction.approve_sales'),
    (N'new_invoice_approve_ss', N'invoice_transaction.approve_ss'),
    (N'new_invoice_create', N'invoice_transaction.create'),
    (N'new_invoice_delete', N'invoice_transaction.delete'),
    (N'new_invoice_edit', N'invoice_transaction.edit'),
    (N'new_invoice_export', N'invoice_transaction.export'),
    (N'new_invoice_hold', N'invoice_transaction.hold'),
    (N'new_invoice_reject', N'invoice_transaction.reject'),
    (N'order_access', N'order.view'),
    (N'order_active', N'order.active'),
    (N'order_create', N'order.create'),
    (N'order_delete', N'order.delete'),
    (N'order_dispatch', N'order.dispatch'),
    (N'order_download', N'order.export'),
    (N'order_edit', N'order.edit'),
    (N'order_show', N'order.detail'),
    (N'pincode_access', N'pincode.view'),
    (N'pincode_active', N'pincode.active'),
    (N'pincode_create', N'pincode.create'),
    (N'pincode_delete', N'pincode.delete'),
    (N'pincode_download', N'pincode.export'),
    (N'pincode_edit', N'pincode.edit'),
    (N'pincode_template', N'pincode.template'),
    (N'pincode_upload', N'pincode.import'),
    (N'product_access', N'product.view'),
    (N'product_active', N'product.active'),
    (N'product_create', N'product.create'),
    (N'product_delete', N'product.delete'),
    (N'product_download', N'product.export'),
    (N'product_edit', N'product.edit'),
    (N'product_template', N'product.template'),
    (N'product_upload', N'product.import'),
    (N'redemption_access', N'redemption.view'),
    (N'redemption_download', N'redemption.export'),
    (N'retailer_approve', N'customer.approve'),
    (N'retailer_pending', N'customer.pending'),
    (N'retailer_productivity_report', N'dealer_performance_report.export'),
    (N'retailer_productivity_report', N'retailer_performance_report.export'),
    (N'retailer_reject', N'customer.reject'),
    (N'role_access', N'role.view'),
    (N'role_create', N'role.create'),
    (N'role_delete', N'role.delete'),
    (N'role_edit', N'role.edit'),
    (N'sale_access', N'order_dispatch.view'),
    (N'sale_show', N'order_dispatch.detail'),
    (N'sales_target_users_download', N'user_target.export'),
    (N'sales_target_users_template', N'user_target.template'),
    (N'sales_target_users_upload', N'user_target.import'),
    (N'scheme_access', N'scheme.view'),
    (N'scheme_access_list', N'scheme.view'),
    (N'scheme_approve', N'scheme.approve'),
    (N'scheme_create', N'scheme.create'),
    (N'scheme_delete', N'scheme.delete'),
    (N'scheme_draft', N'scheme.draft'),
    (N'scheme_edit', N'scheme.edit'),
    (N'scheme_publish', N'scheme.publish'),
    (N'scheme_reject', N'scheme.reject'),
    (N'scheme_show', N'scheme.detail'),
    (N'scheme_submit', N'scheme.submit'),
    (N'state_access', N'state.view'),
    (N'state_active', N'state.active'),
    (N'state_create', N'state.create'),
    (N'state_delete', N'state.delete'),
    (N'state_download', N'state.export'),
    (N'state_edit', N'state.edit'),
    (N'state_template', N'state.template'),
    (N'state_upload', N'state.import'),
    (N'subcategory_access', N'family.view'),
    (N'subcategory_active', N'family.active'),
    (N'subcategory_create', N'family.create'),
    (N'subcategory_delete', N'family.delete'),
    (N'subcategory_download', N'family.export'),
    (N'subcategory_edit', N'family.edit'),
    (N'subcategory_template', N'family.template'),
    (N'subcategory_upload', N'family.import'),
    (N'target_access', N'user_target.view'),
    (N'target_users_access', N'user_target.view'),
    (N'target_users_access_create', N'user_target.create'),
    (N'target_users_access_delete', N'user_target.delete'),
    (N'target_users_access_edit', N'user_target.edit'),
    (N'tours', N'tour.create'),
    (N'tours', N'tour.delete'),
    (N'tours', N'tour.edit'),
    (N'tours', N'tour.export'),
    (N'tours', N'tour.import'),
    (N'tours', N'tour.status'),
    (N'tours', N'tour.template'),
    (N'tours', N'tour.view'),
    (N'user_access', N'user.view'),
    (N'user_active', N'user.active'),
    (N'user_app_details_access', N'user_app.view'),
    (N'user_app_force_logout', N'user_app.force_logout'),
    (N'user_app_uuid_reset', N'user_app.reset_device'),
    (N'user_create', N'user.create'),
    (N'user_delete', N'user.delete'),
    (N'user_download', N'user.export'),
    (N'user_edit', N'user.edit'),
    (N'user_location', N'user_activity.view'),
    (N'user_template', N'user.template'),
    (N'user_upload', N'user.import'),
    (N'visit_report', N'visit_report.view');

-- What each role should hold now, worked out from the snapshot.
WITH expected AS (
    SELECT DISTINCT s.role_id, m.target_name AS permission_name
    FROM v73_role_permission_snapshot s
    INNER JOIN @map m ON m.source_name = s.permission_name
),
actual AS (
    SELECT rp.role_id, p.name AS permission_name
    FROM role_has_permissions rp
    INNER JOIN permissions p ON p.id = rp.permission_id
),
diff AS (
    SELECT e.role_id, e.permission_name, N'MISSING' AS [status]
    FROM expected e
    WHERE NOT EXISTS (SELECT 1 FROM actual a WHERE a.role_id = e.role_id AND a.permission_name = e.permission_name)
    UNION ALL
    SELECT a.role_id, a.permission_name, N'EXTRA'
    FROM actual a
    WHERE NOT EXISTS (SELECT 1 FROM expected e WHERE e.role_id = a.role_id AND e.permission_name = a.permission_name)
)
SELECT
    N'Roles that lost a permission' AS [check],
    CASE WHEN COUNT(*) = 0 THEN N'OK - no role lost anything' ELSE N'CHECK - see the rows below' END AS [result],
    COUNT(*) AS [missing_rows]
FROM diff
WHERE [status] = N'MISSING';

-- The detail, role by role.
WITH expected AS (
    SELECT DISTINCT s.role_id, m.target_name AS permission_name
    FROM v73_role_permission_snapshot s
    INNER JOIN @map m ON m.source_name = s.permission_name
),
actual AS (
    SELECT rp.role_id, p.name AS permission_name
    FROM role_has_permissions rp
    INNER JOIN permissions p ON p.id = rp.permission_id
),
diff AS (
    SELECT e.role_id, e.permission_name, N'MISSING' AS [status]
    FROM expected e
    WHERE NOT EXISTS (SELECT 1 FROM actual a WHERE a.role_id = e.role_id AND a.permission_name = e.permission_name)
    UNION ALL
    SELECT a.role_id, a.permission_name, N'EXTRA'
    FROM actual a
    WHERE NOT EXISTS (SELECT 1 FROM expected e WHERE e.role_id = a.role_id AND e.permission_name = a.permission_name)
)
SELECT
    r.name AS [role_name],
    d.[status],
    d.permission_name AS [permission]
FROM diff d
INNER JOIN roles r ON r.id = d.role_id
WHERE d.[status] = N'MISSING' OR r.name <> 'superadmin'
ORDER BY CASE WHEN d.[status] = N'MISSING' THEN 0 ELSE 1 END, r.name, d.permission_name;

-- Side by side counts. The new number is smaller because the legacy rows a role
-- held gated nothing; what its people can reach on screen is unchanged.
SELECT
    r.name AS [role_name],
    (SELECT COUNT(*) FROM v73_role_permission_snapshot s WHERE s.role_id = r.id) AS [before_deployment],
    (SELECT COUNT(*) FROM role_has_permissions rp WHERE rp.role_id = r.id) AS [after_deployment]
FROM roles r
ORDER BY r.id;

-- The snapshot table may be dropped once the comparison reads OK:
--   DROP TABLE v73_role_permission_snapshot;
