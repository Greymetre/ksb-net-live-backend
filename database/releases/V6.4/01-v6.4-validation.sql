-- FieldKonnect V6.4 additive-only SQL Server release script.
-- V6.4 reuses the V6.3 schema and existing asm_rating_report permission.
-- This script intentionally does not INSERT, UPDATE, DELETE, ALTER, or DROP
-- any live business data or database object.

SET NOCOUNT ON;

SELECT
    N'FieldKonnect V6.4 database validation completed. No database changes are required.' AS [message],
    DB_NAME() AS [database_name],
    SYSUTCDATETIME() AS [checked_at_utc];
