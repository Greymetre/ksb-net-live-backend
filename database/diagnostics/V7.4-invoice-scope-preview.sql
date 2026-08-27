-- FieldKonnect V7.4 - preview what the invoice scope will do on THIS database.
--
-- READ-ONLY. It writes nothing, creates nothing and locks nothing. Run it on live
-- BEFORE deploying V7.4 to see exactly which invoices each user will be left with.
--
-- It reproduces the same three rules the backend applies:
--   * a role whose name contains "admin", or a privileged reporting role -> everything
--   * role id 54 (BM) -> the users sharing a branch
--   * anyone else -> themselves plus their reporting descendants, to the end of the chain
-- and then the same assignment match the invoice screen uses.
--
-- Set @user_name below to the person you want to check.
--
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i V7.4-invoice-scope-preview.sql

SET NOCOUNT ON;

DECLARE @user_name NVARCHAR(255) = N'Deepak Jha';   -- <<< change this

DECLARE @user_id DECIMAL(20,0) = (SELECT TOP (1) id FROM users WHERE name = @user_name AND deleted_at IS NULL ORDER BY id);

IF @user_id IS NULL
BEGIN
    SELECT N'User not found - check the spelling' AS [result], @user_name AS [looked_for];
    RETURN;
END;

-- 1. Who they are, and which tier they land in.
SELECT
    u.id AS [user_id], u.name, u.reportingid AS [reports_to],
    (SELECT name FROM users m WHERE m.id = u.reportingid) AS [manager],
    u.branch_id, u.active, u.customer_id,
    STUFF((SELECT ', ' + r.name FROM model_has_roles mr
           INNER JOIN roles r ON r.id = mr.role_id
           WHERE mr.model_id = u.id AND mr.model_type = 'App\Models\User'
           FOR XML PATH('')), 1, 2, '') AS [roles]
FROM users u WHERE u.id = @user_id;

SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM model_has_roles mr INNER JOIN roles r ON r.id = mr.role_id
                     WHERE mr.model_id = @user_id AND mr.model_type = 'App\Models\User' AND r.name = 'Distributor')
             THEN N'DISTRIBUTOR - pinned to its own dealer''s retailers'
        WHEN EXISTS (SELECT 1 FROM model_has_roles mr INNER JOIN roles r ON r.id = mr.role_id
                     WHERE mr.model_id = @user_id AND mr.model_type = 'App\Models\User'
                       AND (LOWER(r.name) LIKE '%admin%' OR r.name IN
                            ('superadmin','Admin','ZONAL','subAdmin','GM.','CRM','HR_Admin','HO_Account',
                             'Sub_Support','Accounts Order','Service Admin','All Customers','Sub billing',
                             'Sales Admin','Marketing_Admin','MIS_ADMIN','Data_Crm')))
             THEN N'UNRESTRICTED - sees every invoice (no change for this user)'
        WHEN EXISTS (SELECT 1 FROM model_has_roles mr WHERE mr.model_id = @user_id
                       AND mr.model_type = 'App\Models\User' AND mr.role_id = 54)
             THEN N'BRANCH MANAGER - sees the users sharing its branch'
        ELSE N'REPORTING SCOPE - sees itself plus its descendants'
    END AS [tier_this_user_lands_in];

-- 2. The downline, to the end of the chain. This is the set the scope is built from.
WITH downline AS (
    SELECT id, name, reportingid, 0 AS level FROM users WHERE id = @user_id
    UNION ALL
    SELECT u.id, u.name, u.reportingid, d.level + 1
    FROM users u INNER JOIN downline d ON u.reportingid = d.id
    WHERE u.deleted_at IS NULL AND u.active = 'Y' AND u.isDeleted = 0 AND u.customer_id IS NULL
)
SELECT level, id AS [user_id], name AS [visible_user]
FROM downline ORDER BY level, name
OPTION (MAXRECURSION 20);

-- 3. The invoices that survive the scope, and the ones that drop away.
WITH downline AS (
    SELECT id FROM users WHERE id = @user_id
    UNION ALL
    SELECT u.id FROM users u INNER JOIN downline d ON u.reportingid = d.id
    WHERE u.deleted_at IS NULL AND u.active = 'Y' AND u.isDeleted = 0 AND u.customer_id IS NULL
),
matched AS (
    SELECT i.id AS invoice_id, i.invoice_number, c.id AS customer_id, c.name AS customer_name,
           c.executive_id,
           CASE WHEN EXISTS (
                   SELECT 1 FROM downline d
                   WHERE c.custom_fields IS NOT NULL
                     AND (c.custom_fields LIKE '%"employee_id":"' + CAST(d.id AS VARCHAR(20)) + '"%'
                       OR c.custom_fields LIKE '%"employee_id": "' + CAST(d.id AS VARCHAR(20)) + '"%'
                       OR c.custom_fields LIKE '%"employee_id":' + CAST(d.id AS VARCHAR(20)) + '%'
                       OR c.custom_fields LIKE '%"sales_executive_id":"' + CAST(d.id AS VARCHAR(20)) + '"%'
                       OR c.custom_fields LIKE '%"sales_executive_id": "' + CAST(d.id AS VARCHAR(20)) + '"%'
                       OR c.custom_fields LIKE '%"sales_executive_id":' + CAST(d.id AS VARCHAR(20)) + '%'))
                THEN 1 ELSE 0 END AS matched_by_custom_fields,
           CASE WHEN c.executive_id IN (SELECT id FROM downline)
                 AND (c.custom_fields IS NULL
                      OR (c.custom_fields NOT LIKE '%"employee_id":%' AND c.custom_fields NOT LIKE '%"sales_executive_id":%'))
                THEN 1 ELSE 0 END AS matched_by_executive_id
    FROM new_invoices i
    INNER JOIN customers c ON c.id = i.secondary_customer_id
    WHERE i.deleted_at IS NULL AND c.deleted_at IS NULL
)
SELECT
    CASE WHEN matched_by_custom_fields = 1 OR matched_by_executive_id = 1
         THEN N'VISIBLE' ELSE N'hidden after V7.4' END AS [after_deploy],
    invoice_number, customer_name, customer_id, executive_id,
    matched_by_custom_fields, matched_by_executive_id
FROM matched
ORDER BY [after_deploy], customer_name, invoice_number
OPTION (MAXRECURSION 20);

-- 4. The headline: how many invoices this user sees now, and how many after.
WITH downline AS (
    SELECT id FROM users WHERE id = @user_id
    UNION ALL
    SELECT u.id FROM users u INNER JOIN downline d ON u.reportingid = d.id
    WHERE u.deleted_at IS NULL AND u.active = 'Y' AND u.isDeleted = 0 AND u.customer_id IS NULL
)
SELECT
    (SELECT COUNT(*) FROM new_invoices WHERE deleted_at IS NULL) AS [visible_today],
    (SELECT COUNT(*) FROM new_invoices i INNER JOIN customers c ON c.id = i.secondary_customer_id
      WHERE i.deleted_at IS NULL AND c.deleted_at IS NULL
        AND (EXISTS (SELECT 1 FROM downline d WHERE c.custom_fields IS NOT NULL
                     AND (c.custom_fields LIKE '%"employee_id":"' + CAST(d.id AS VARCHAR(20)) + '"%'
                       OR c.custom_fields LIKE '%"employee_id": "' + CAST(d.id AS VARCHAR(20)) + '"%'
                       OR c.custom_fields LIKE '%"employee_id":' + CAST(d.id AS VARCHAR(20)) + '%'
                       OR c.custom_fields LIKE '%"sales_executive_id":"' + CAST(d.id AS VARCHAR(20)) + '"%'
                       OR c.custom_fields LIKE '%"sales_executive_id": "' + CAST(d.id AS VARCHAR(20)) + '"%'
                       OR c.custom_fields LIKE '%"sales_executive_id":' + CAST(d.id AS VARCHAR(20)) + '%'))
             OR (c.executive_id IN (SELECT id FROM downline)
                 AND (c.custom_fields IS NULL
                      OR (c.custom_fields NOT LIKE '%"employee_id":%' AND c.custom_fields NOT LIKE '%"sales_executive_id":%')))
            )) AS [visible_after_v74]
OPTION (MAXRECURSION 20);
