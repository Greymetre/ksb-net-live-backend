-- FieldKonnect V6.7 SQL Server release script.
--
-- V6.7 reuses the V6.6 schema: no new table, column or index. The one data
-- change is the expense permission grant below, which the SFA app needs before
-- field users can file or check an expense. Everything after it is read-only
-- readiness reporting.
--
-- Idempotent: running it twice grants nothing twice.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 01-V6.7-update.sql

SET NOCOUNT ON;
SET XACT_ABORT ON;

SELECT
    N'FieldKonnect V6.7 database update starting.' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [started_at_utc];

-- ---------------------------------------------------------------------------
-- 1. Expense permissions for the SFA app roles.
--
-- Field users file and track their own expenses. Reporting managers get the
-- same, plus expense_checked, which is the app's only approval action
-- ("Checked By Reporting"). expenses_authority - the final Approve, Reject and
-- Hold - is deliberately NOT granted here: that stays with the CRM roles that
-- already hold it.
-- ---------------------------------------------------------------------------

DECLARE @grants TABLE (role_name NVARCHAR(100), permission_name NVARCHAR(100));

INSERT INTO @grants (role_name, permission_name)
SELECT r.name, p.name
FROM (VALUES ('ASR'), ('DSR')) AS r(name)
CROSS JOIN (VALUES ('expense_access'), ('expenses_create'), ('expenses_edit'), ('expenses_delete')) AS p(name);

INSERT INTO @grants (role_name, permission_name)
SELECT r.name, p.name
FROM (VALUES ('TM.'), ('ASM'), ('BDM'), ('BM.'), ('ZM.')) AS r(name)
CROSS JOIN (VALUES ('expense_access'), ('expenses_create'), ('expenses_edit'), ('expenses_delete'), ('expense_checked')) AS p(name);

BEGIN TRANSACTION;

    INSERT INTO role_has_permissions (permission_id, role_id)
    SELECT p.id, r.id
    FROM @grants g
    JOIN roles r ON r.name = g.role_name
    JOIN permissions p ON p.name = g.permission_name
    WHERE NOT EXISTS (
        SELECT 1 FROM role_has_permissions rp
        WHERE rp.role_id = r.id AND rp.permission_id = p.id
    );

    SELECT N'Expense permissions granted (new rows)' AS [step], @@ROWCOUNT AS [row_count];

COMMIT TRANSACTION;

SELECT
    r.name AS [role_name],
    p.name AS [permission_name]
FROM role_has_permissions rp
JOIN roles r ON r.id = rp.role_id
JOIN permissions p ON p.id = rp.permission_id
WHERE p.name LIKE 'expense%'
  AND r.name IN ('ASR', 'DSR', 'TM.', 'ASM', 'BDM', 'BM.', 'ZM.')
ORDER BY r.name, p.name;

-- ---------------------------------------------------------------------------
-- 2. Readiness checks. Read-only; review these before announcing V6.7.
-- ---------------------------------------------------------------------------

-- 2a. Expense types and rates are decided by the employee's payroll grade, and
--     from V6.7 an employee with no grade cannot file an expense at all. Fill
--     these grades in on the Users page (field "Payroll Grade") first.
SELECT
    N'Active users with no payroll grade (cannot file expenses)' AS [check],
    COUNT(*) AS [row_count]
FROM users
WHERE active = 'Y'
  AND isDeleted = 0
  AND customerid IS NULL
  AND (payroll IS NULL OR LTRIM(RTRIM(payroll)) = '');

-- 2b. Values other than a grade 1-5 in users.payroll. The V6.7 Users form only
--     offers Grade 1-5 and the API rejects anything else, but rows written
--     before V6.7 may still hold something different.
SELECT
    N'Users with a payroll value outside grades 1-5' AS [check],
    COUNT(*) AS [row_count]
FROM users
WHERE active = 'Y'
  AND isDeleted = 0
  AND payroll IS NOT NULL
  AND LTRIM(RTRIM(payroll)) <> ''
  AND LTRIM(RTRIM(payroll)) NOT IN ('1', '2', '3', '4', '5');

SELECT TOP 50
    id AS [user_id],
    name AS [user_name],
    employee_codes AS [employee_code],
    payroll AS [payroll_value]
FROM users
WHERE active = 'Y'
  AND isDeleted = 0
  AND payroll IS NOT NULL
  AND LTRIM(RTRIM(payroll)) <> ''
  AND LTRIM(RTRIM(payroll)) NOT IN ('1', '2', '3', '4', '5')
ORDER BY id;

-- 2c. Expense types with no grade are offered to every employee. That is
--     allowed, but a graded rate is usually intended - informational.
SELECT
    N'Active expense types with no grade (offered to everyone)' AS [check],
    COUNT(*) AS [row_count]
FROM expenses_types
WHERE is_active = 1
  AND (payroll_id IS NULL OR payroll_id = 0);

-- 2d. Grades that have no expense type at all. An employee on such a grade
--     sees an empty type list in the app.
SELECT
    N'Payroll grades with no active expense type' AS [check],
    COUNT(*) AS [row_count]
FROM (VALUES (1), (2), (3), (4), (5)) AS g(grade)
WHERE NOT EXISTS (
    SELECT 1 FROM expenses_types t
    WHERE t.is_active = 1 AND t.payroll_id = g.grade
);

-- 2e. Expense claims currently on the system. Script 02 clears these; the count
--     is here so the number removed is known in advance.
SELECT
    N'Expense claims currently stored' AS [check],
    COUNT(*) AS [row_count]
FROM expenses;

SELECT
    N'FieldKonnect V6.7 database update completed.' AS [message],
    SYSUTCDATETIME() AS [completed_at_utc];
