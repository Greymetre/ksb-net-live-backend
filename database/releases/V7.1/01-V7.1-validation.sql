-- FieldKonnect V7.1 SQL Server release script.
--
-- V7.1 reuses the V7.0 schema and permissions. The export changes, the hold
-- remark and the dealer invoice author are all code-only.
--
-- This script intentionally does not INSERT, UPDATE, DELETE, ALTER or DROP any
-- database object or business row. It reports readiness counts only.
--
-- Run:
--   sqlcmd -S <server> -U <user> -P <password> -d ksb_pr -i 01-V7.1-validation.sql

SET NOCOUNT ON;

SELECT
    N'FieldKonnect V7.1 database validation completed. No database changes are required.' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [checked_at_utc];

-- ---------------------------------------------------------------------------
-- 1. Dealers that cannot be given a login automatically.
--
-- From V7.1 a dealer filing an invoice from the Loyalty app gets a login user
-- provisioned on the spot if it does not already have one. Provisioning needs a
-- usable mobile number and a dealer code; without either, the invoice is
-- refused rather than being filed under somebody else. Fill these in before the
-- dealer next files an invoice.
-- ---------------------------------------------------------------------------
SELECT
    N'Dealers with no login user that cannot be provisioned' AS [check],
    COUNT(*) AS [row_count]
FROM customers c
WHERE c.customertype = 1
  AND c.deleted_at IS NULL
  AND c.active = 'Y'
  AND NOT EXISTS (SELECT 1 FROM users u WHERE u.customerid = c.id AND u.deleted_at IS NULL)
  AND (
        c.mobile IS NULL OR LTRIM(RTRIM(c.mobile)) = '' OR LEN(LTRIM(RTRIM(c.mobile))) > 11
        OR (
             (c.customer_code IS NULL OR LTRIM(RTRIM(c.customer_code)) = '')
             AND (ISJSON(c.custom_fields) = 0
                  OR JSON_VALUE(c.custom_fields, '$.distributor_code') IS NULL
                  OR LTRIM(RTRIM(JSON_VALUE(c.custom_fields, '$.distributor_code'))) = '')
           )
      );

SELECT TOP 50
    c.id AS [dealer_id],
    c.name AS [dealer_name],
    c.customer_code,
    c.mobile
FROM customers c
WHERE c.customertype = 1
  AND c.deleted_at IS NULL
  AND c.active = 'Y'
  AND NOT EXISTS (SELECT 1 FROM users u WHERE u.customerid = c.id AND u.deleted_at IS NULL)
ORDER BY c.name;

-- ---------------------------------------------------------------------------
-- 2. Invoices filed under somebody other than the dealer.
--
-- Before V7.1, a dealer app invoice fell back to the retailer's sales employee
-- when the dealer had no login. Those rows keep that author; the count is
-- historical and nothing needs correcting for V7.1 to work. Review it if the
-- Created By column in the export looks wrong for old invoices.
-- ---------------------------------------------------------------------------
SELECT
    N'Invoices created by a user who is neither internal nor the retailer''s dealer' AS [check],
    COUNT(*) AS [row_count]
FROM new_invoices i
JOIN users u ON u.id = i.created_by
JOIN customers c ON c.id = i.secondary_customer_id
WHERE u.customerid IS NOT NULL
  AND u.customerid <> 0
  AND u.customerid <> COALESCE(
        TRY_CONVERT(BIGINT, CASE WHEN ISJSON(c.custom_fields) = 1
                                 THEN JSON_VALUE(c.custom_fields, '$.distributor_name') END),
        TRY_CONVERT(BIGINT, CASE WHEN ISJSON(c.custom_fields) = 1
                                 THEN JSON_VALUE(c.custom_fields, '$.agri_distributor') END),
        TRY_CONVERT(BIGINT, c.parent_id),
        0
      );

-- ---------------------------------------------------------------------------
-- 3. Holds recorded without a reason.
--
-- A hold remark is mandatory from V7.1. Anything counted here was held before
-- the rule existed; the export falls back to the most recent hold that does
-- carry a remark, so these only matter if an invoice has no remarked hold at all.
-- ---------------------------------------------------------------------------
SELECT
    N'Hold entries recorded without a remark' AS [check],
    COUNT(*) AS [row_count]
FROM new_invoice_approval_logs
WHERE to_status = 5
  AND (remark IS NULL OR LTRIM(RTRIM(CAST(remark AS nvarchar(max)))) = '');
