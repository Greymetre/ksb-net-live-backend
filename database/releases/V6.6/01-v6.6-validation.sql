-- FieldKonnect V6.6 additive-only SQL Server release script.
-- V6.6 reuses the V6.5 schema. Server-side pagination, the dealer dashboard,
-- the dealer scheme detail page and the dealer-export fix are all code-only.
-- This script intentionally does not INSERT, UPDATE, DELETE, ALTER, or DROP
-- any live business data or database object.

SET NOCOUNT ON;

SELECT
    N'FieldKonnect V6.6 database validation completed. No database changes are required.' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [checked_at_utc];

-- Post-deployment readiness checks. Read-only; review the counts before
-- announcing V6.6 to dealers.

-- 1. Dealers without a code. From V6.5 the dealer code is the CRM login password
--    and is required on save, so any dealer listed here cannot be edited until a
--    code is filled in.
SELECT
    N'Dealers with no code (blocked on edit)' AS [check],
    COUNT(*) AS [row_count]
FROM customers
WHERE customertype = 1
  AND deleted_at IS NULL
  AND (customer_code IS NULL OR LTRIM(RTRIM(customer_code)) = '');

-- 2. Dealers whose code lives only on the customer_code column and not in the
--    legacy custom_fields JSON. V6.6 makes the dealer export fall back to the
--    column, so these now export correctly; the count is informational.
SELECT
    N'Dealers exporting via the V6.6 customer_code fallback' AS [check],
    COUNT(*) AS [row_count]
FROM customers
WHERE customertype = 1
  AND deleted_at IS NULL
  AND customer_code IS NOT NULL
  AND LTRIM(RTRIM(customer_code)) <> ''
  AND (
        ISJSON(custom_fields) <> 1
     OR NULLIF(JSON_VALUE(custom_fields, '$.distributor_code'), '') IS NULL
  );

-- 3. Cities that share a name inside one district. V6.6 blocks new duplicates;
--    existing pairs must be merged before either row can be edited.
SELECT
    N'Duplicate city name within a district' AS [check],
    COUNT(*) AS [row_count]
FROM (
    SELECT city_name, district_id
    FROM cities
    WHERE deleted_at IS NULL
    GROUP BY city_name, district_id
    HAVING COUNT(*) > 1
) AS duplicates;
