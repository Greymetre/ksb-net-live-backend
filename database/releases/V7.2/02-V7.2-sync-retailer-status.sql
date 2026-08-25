-- FieldKonnect V7.2 optional data repair for SQL Server.
--
-- V7.2's code fix does not need this script: the API resolves a retailer's
-- approval from customers.custom_fields first and upper cases it, so a stale or
-- differently cased customer_details.visit_status no longer blocks Add Order.
--
-- This script makes the stored data agree with what the CRM and the app show,
-- so that anything reading visit_status directly - a report, an export, a query
-- run by hand - reads the same approval. It rewrites customer_details.visit_status
-- for RETAILERS ONLY, to the value the CRM already displays for that customer.
--
-- It is idempotent: running it twice updates nothing the second time. It never
-- inserts, deletes or touches dealers/distributors, whose visit_status carries a
-- different meaning ('Active').
--
-- Take a database backup before running it, as with any UPDATE on live.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 02-V7.2-sync-retailer-status.sql

SET NOCOUNT ON;
SET XACT_ABORT ON;

-- Rows this script will change, before it changes them.
SELECT
    N'Retailer rows to be corrected' AS [check],
    COUNT(*) AS [row_count]
FROM customers c
INNER JOIN customer_types ct ON ct.id = c.customertype
INNER JOIN customer_details cd ON cd.customer_id = c.id AND cd.deleted_at IS NULL
WHERE c.deleted_at IS NULL
  AND (ct.customertype_name LIKE '%Retailer%' OR ct.type_name LIKE '%Retailer%')
  AND cd.visit_status COLLATE Latin1_General_BIN <> COALESCE(
        NULLIF(UPPER(LTRIM(RTRIM(JSON_VALUE(c.custom_fields, '$.status')))), ''),
        NULLIF(UPPER(LTRIM(RTRIM(cd.visit_status))), ''), 'PENDING');

BEGIN TRANSACTION;

UPDATE cd
SET cd.visit_status = COALESCE(
        NULLIF(UPPER(LTRIM(RTRIM(JSON_VALUE(c.custom_fields, '$.status')))), ''),
        NULLIF(UPPER(LTRIM(RTRIM(cd.visit_status))), ''), 'PENDING'),
    cd.updated_at = SYSUTCDATETIME()
FROM customer_details cd
INNER JOIN customers c ON c.id = cd.customer_id
INNER JOIN customer_types ct ON ct.id = c.customertype
WHERE cd.deleted_at IS NULL
  AND c.deleted_at IS NULL
  AND (ct.customertype_name LIKE '%Retailer%' OR ct.type_name LIKE '%Retailer%')
  AND cd.visit_status COLLATE Latin1_General_BIN <> COALESCE(
        NULLIF(UPPER(LTRIM(RTRIM(JSON_VALUE(c.custom_fields, '$.status')))), ''),
        NULLIF(UPPER(LTRIM(RTRIM(cd.visit_status))), ''), 'PENDING');

SELECT N'Retailer rows corrected' AS [result], @@ROWCOUNT AS [row_count];

COMMIT TRANSACTION;

-- What the retailer approvals look like afterwards. Only APPROVED, PENDING and
-- REJECTED should remain, plus retailers that have no customer_details row at
-- all - those read as PENDING and are left untouched on purpose.
SELECT
    ISNULL(cd.visit_status, N'(no customer_details row)') AS [visit_status],
    COUNT(*) AS [row_count]
FROM customers c
INNER JOIN customer_types ct ON ct.id = c.customertype
LEFT JOIN customer_details cd ON cd.customer_id = c.id AND cd.deleted_at IS NULL
WHERE c.deleted_at IS NULL
  AND (ct.customertype_name LIKE '%Retailer%' OR ct.type_name LIKE '%Retailer%')
GROUP BY cd.visit_status
ORDER BY COUNT(*) DESC;
