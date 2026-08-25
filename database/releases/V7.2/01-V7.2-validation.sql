-- FieldKonnect V7.2 SQL Server release script.
--
-- V7.2 adds no schema object and no permission. It corrects how the SFA app
-- reads a retailer's approval: the API now resolves it the same way the CRM
-- customer list does - the approval stored in customers.custom_fields wins over
-- the legacy customer_details.visit_status, trimmed and upper cased.
--
-- This script intentionally does not INSERT, UPDATE, DELETE, ALTER or DROP any
-- database object or business row. It reports readiness counts only. The
-- optional repair for the rows it reports is 02-V7.2-sync-retailer-status.sql.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 01-V7.2-validation.sql

SET NOCOUNT ON;

SELECT
    N'FieldKonnect V7.2 database validation completed. No database change is required for the fix.' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [checked_at_utc];

-- ---------------------------------------------------------------------------
-- 1. Retailers the SFA app refused an order for although the CRM shows them as
--    approved.
--
-- Before V7.2 the check-in and customer APIs returned customer_details.visit_status
-- as it stands, while the CRM list normalises custom_fields.status first. Where
-- the two disagree the app received something other than APPROVED and answered
-- "Your customer is not approved" on Add Order. V7.2 makes the API read the CRM
-- way, so these rows work from the moment the backend is deployed - the count
-- below is how many retailers were affected, not work that remains.
-- ---------------------------------------------------------------------------
SELECT
    N'Approved retailers whose visit_status disagreed with the CRM approval' AS [check],
    COUNT(*) AS [row_count]
FROM customers c
LEFT JOIN customer_types ct ON ct.id = c.customertype
LEFT JOIN customer_details cd ON cd.customer_id = c.id AND cd.deleted_at IS NULL
WHERE c.deleted_at IS NULL
  AND (ct.customertype_name LIKE '%Retailer%' OR ct.type_name LIKE '%Retailer%')
  AND COALESCE(NULLIF(UPPER(LTRIM(RTRIM(JSON_VALUE(c.custom_fields, '$.status')))), ''),
               NULLIF(UPPER(LTRIM(RTRIM(cd.visit_status))), ''), 'PENDING') = 'APPROVED'
  AND (cd.visit_status IS NULL OR cd.visit_status COLLATE Latin1_General_BIN <> 'APPROVED');

-- The first 50 of them, so the retailers that were blocked in the field can be
-- recognised.
SELECT TOP (50)
    c.id AS [customer_id],
    c.name AS [customer_name],
    c.mobile,
    ISNULL(cd.visit_status, N'(no customer_details row)') AS [visit_status],
    ISNULL(JSON_VALUE(c.custom_fields, '$.status'), N'(none)') AS [custom_fields_status]
FROM customers c
LEFT JOIN customer_types ct ON ct.id = c.customertype
LEFT JOIN customer_details cd ON cd.customer_id = c.id AND cd.deleted_at IS NULL
WHERE c.deleted_at IS NULL
  AND (ct.customertype_name LIKE '%Retailer%' OR ct.type_name LIKE '%Retailer%')
  AND COALESCE(NULLIF(UPPER(LTRIM(RTRIM(JSON_VALUE(c.custom_fields, '$.status')))), ''),
               NULLIF(UPPER(LTRIM(RTRIM(cd.visit_status))), ''), 'PENDING') = 'APPROVED'
  AND (cd.visit_status IS NULL OR cd.visit_status COLLATE Latin1_General_BIN <> 'APPROVED')
ORDER BY c.id;

-- ---------------------------------------------------------------------------
-- 2. Every distinct retailer approval value held in customer_details.
--
-- APPROVED, PENDING and REJECTED are the three the CRM writes. Anything else is
-- legacy data. V7.2 tolerates all of it, and 02-V7.2-sync-retailer-status.sql
-- can tidy it.
-- ---------------------------------------------------------------------------
SELECT
    ISNULL(cd.visit_status, N'(null)') AS [visit_status],
    COUNT(*) AS [row_count]
FROM customers c
LEFT JOIN customer_types ct ON ct.id = c.customertype
LEFT JOIN customer_details cd ON cd.customer_id = c.id AND cd.deleted_at IS NULL
WHERE c.deleted_at IS NULL
  AND (ct.customertype_name LIKE '%Retailer%' OR ct.type_name LIKE '%Retailer%')
GROUP BY cd.visit_status
ORDER BY COUNT(*) DESC;
