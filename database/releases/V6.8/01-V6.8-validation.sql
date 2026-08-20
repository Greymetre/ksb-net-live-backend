-- FieldKonnect V6.8 SQL Server release script.
--
-- V6.8 reuses the V6.7 schema and permissions. The stored-file URLs, the invoice
-- listing stage counts and the customer point totals are all code-only changes.
-- This script intentionally does not INSERT, UPDATE, DELETE, ALTER or DROP any
-- database object or business row.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 01-V6.8-validation.sql

SET NOCOUNT ON;

SELECT
    N'FieldKonnect V6.8 database validation completed. No database changes are required.' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [checked_at_utc];

-- Post-deployment readiness checks. Read-only; review the counts before
-- announcing V6.8.

-- 1. Retailers whose point totals depend on a Branch-scoped scheme but who have
--    no assigned employee to take a branch from. V6.8 fixes the case where the
--    branch was only read from whoever created the invoice - typically a dealer
--    login with no branch of its own - but a customer with no assigned employee
--    at all still cannot match a Branch-scoped scheme. Assign an employee to
--    these customers if their schemes are branch scoped.
SELECT
    N'Customers with HO-approved invoices but no assigned employee' AS [check],
    COUNT(DISTINCT c.id) AS [row_count]
FROM customers c
JOIN new_invoices i ON i.secondary_customer_id = c.id AND i.approval_status = 3
WHERE c.deleted_at IS NULL
  AND (c.executive_id IS NULL OR c.executive_id = 0);

SELECT TOP 50
    c.id AS [customer_id],
    c.name AS [customer_name],
    c.customer_code,
    COUNT(i.id) AS [ho_approved_invoices]
FROM customers c
JOIN new_invoices i ON i.secondary_customer_id = c.id AND i.approval_status = 3
WHERE c.deleted_at IS NULL
  AND (c.executive_id IS NULL OR c.executive_id = 0)
GROUP BY c.id, c.name, c.customer_code
ORDER BY COUNT(i.id) DESC;

-- 2. Assigned employees carrying no branch at all. A Branch-scoped scheme cannot
--    match through them either.
SELECT
    N'Assigned employees with no branch' AS [check],
    COUNT(DISTINCT u.id) AS [row_count]
FROM customers c
JOIN users u ON u.id = c.executive_id
WHERE c.deleted_at IS NULL
  AND u.primary_branch_id IS NULL
  AND (u.branch_id IS NULL OR LTRIM(RTRIM(u.branch_id)) = '');

-- 3. Expense attachments on record. Their files live under the API's
--    wwwroot\uploads\expenses folder, which must survive the deployment - the
--    backend drop replaces assemblies only.
SELECT
    N'Expense attachments on record' AS [check],
    COUNT(*) AS [row_count]
FROM media
WHERE model_type = 'App\Models\Expenses'
  AND collection_name = 'expense_file';
