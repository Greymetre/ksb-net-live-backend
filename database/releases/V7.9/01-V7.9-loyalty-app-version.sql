/* ============================================================================
   V7.9 - Loyalty app force update: version columns on loyalty_app_settings

   The table already exists and carries one row, but it cannot hold a version:
   app_version is a FLOAT, so "1.0.1" is not storable and "1.10" would collapse
   to 1.1. There is also no column for iOS at all.

   This script adds two NVARCHAR columns beside the existing ones and seeds the
   Android one from whatever the float holds today. The float column is left
   exactly as it is - the legacy Laravel side may still read it, and nothing
   here needs it gone.

   Safe to run more than once. Every statement that touches the new columns
   goes through sp_executesql, because SQL Server compiles a batch as a whole
   and a column added in the same batch is not visible to a plain statement in
   it - the failure V7.7 hit on live.
   ============================================================================ */

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.loyalty_app_settings', 'U') IS NULL
BEGIN
    RAISERROR('loyalty_app_settings table not found - wrong database.', 16, 1);
    RETURN;
END

BEGIN TRANSACTION;

/* 1. The two version columns ------------------------------------------------ */
IF COL_LENGTH('dbo.loyalty_app_settings', 'app_android_version') IS NULL
BEGIN
    ALTER TABLE dbo.loyalty_app_settings ADD app_android_version NVARCHAR(20) NULL;
    PRINT 'Added column app_android_version.';
END
ELSE
    PRINT 'Column app_android_version already present.';

IF COL_LENGTH('dbo.loyalty_app_settings', 'app_ios_version') IS NULL
BEGIN
    ALTER TABLE dbo.loyalty_app_settings ADD app_ios_version NVARCHAR(20) NULL;
    PRINT 'Added column app_ios_version.';
END
ELSE
    PRINT 'Column app_ios_version already present.';

/* 2. Exactly one settings row must exist ------------------------------------ */
IF NOT EXISTS (SELECT 1 FROM dbo.loyalty_app_settings)
BEGIN
    INSERT INTO dbo.loyalty_app_settings (created_at, updated_at)
    VALUES (SYSUTCDATETIME(), SYSUTCDATETIME());
    PRINT 'Inserted the first loyalty_app_settings row.';
END

/* 3. Seed the Android version from the old float, once ----------------------- */
EXEC sp_executesql N'
    UPDATE dbo.loyalty_app_settings
    SET app_android_version = CASE
            WHEN app_version IS NULL THEN NULL
            /* 20.0 reads as "20", 1.5 stays "1.5" */
            WHEN app_version = FLOOR(app_version) THEN CONVERT(NVARCHAR(20), CONVERT(BIGINT, app_version))
            /* FLOAT rather than DECIMAL here: 1.5 reads as "1.5", not "1.5000" */
            ELSE CONVERT(NVARCHAR(20), CONVERT(FLOAT, app_version))
        END,
        updated_at = SYSUTCDATETIME()
    WHERE app_android_version IS NULL;';

PRINT 'Seeded app_android_version from the existing app_version value.';

COMMIT TRANSACTION;

/* 4. Show the result --------------------------------------------------------- */
EXEC sp_executesql N'
    SELECT id,
           app_version          AS old_float_version,
           app_android_version,
           app_ios_version,
           updated_at
    FROM dbo.loyalty_app_settings
    ORDER BY id;';
