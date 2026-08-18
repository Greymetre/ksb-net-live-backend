-- FieldKonnect V6.5 additive-only SQL Server release script.
--
-- Scope: two nullable columns for the Promotional Activity module.
--   promotional_activities.activity_code             -- readable activity id, e.g. ACT-RTL-2608-0042
--   promotional_activity_participants.participant_type
--
-- Safety:
--   * Every statement is guarded and the script can be run more than once.
--   * No existing row is INSERTed, UPDATEd or DELETEd.
--   * No table, index, constraint or permission is dropped or altered.
--   * Both columns are NULLable, so existing rows stay valid and the previous
--     release keeps working against this schema if a rollback is needed.

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    ------------------------------------------------------------------
    -- 1. promotional_activities.activity_code
    ------------------------------------------------------------------
    IF OBJECT_ID(N'promotional_activities', N'U') IS NULL
    BEGIN
        RAISERROR(N'Table promotional_activities is missing. Apply the V6.3 release script first.', 16, 1);
    END;

    IF COL_LENGTH(N'promotional_activities', N'activity_code') IS NULL
    BEGIN
        ALTER TABLE promotional_activities ADD activity_code NVARCHAR(40) NULL;
        PRINT N'Added promotional_activities.activity_code';
    END
    ELSE
    BEGIN
        PRINT N'promotional_activities.activity_code already present, skipped';
    END;

    ------------------------------------------------------------------
    -- 2. promotional_activity_participants.participant_type
    ------------------------------------------------------------------
    IF OBJECT_ID(N'promotional_activity_participants', N'U') IS NULL
    BEGIN
        RAISERROR(N'Table promotional_activity_participants is missing. Apply the V6.3 release script first.', 16, 1);
    END;

    IF COL_LENGTH(N'promotional_activity_participants', N'participant_type') IS NULL
    BEGIN
        ALTER TABLE promotional_activity_participants ADD participant_type NVARCHAR(100) NULL;
        PRINT N'Added promotional_activity_participants.participant_type';
    END
    ELSE
    BEGIN
        PRINT N'promotional_activity_participants.participant_type already present, skipped';
    END;

    ------------------------------------------------------------------
    -- 3. Record the matching EF Core migration as applied.
    --
    -- Control-S runs with SKIP_DB_BOOTSTRAP=true, so EF Core never applies
    -- migrations there and this schema change arrives through this script.
    -- Stamping the history row keeps the two in agreement, so the migration is
    -- not attempted a second time if automatic bootstrap is ever switched on.
    ------------------------------------------------------------------
    IF OBJECT_ID(N'__EFMigrationsHistory', N'U') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM __EFMigrationsHistory
                       WHERE MigrationId = N'20260818103309_AddActivityCodeAndParticipantType')
    BEGIN
        INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
        VALUES (N'20260818103309_AddActivityCodeAndParticipantType', N'8.0.13');
        PRINT N'Recorded EF migration 20260818103309_AddActivityCodeAndParticipantType';
    END;

    COMMIT TRANSACTION;

    SELECT
        N'FieldKonnect V6.5 database update completed successfully.' AS [message],
        DB_NAME() AS [database_name],
        CASE WHEN COL_LENGTH(N'promotional_activities', N'activity_code') IS NULL
             THEN N'MISSING' ELSE N'OK' END AS [activity_code],
        CASE WHEN COL_LENGTH(N'promotional_activity_participants', N'participant_type') IS NULL
             THEN N'MISSING' ELSE N'OK' END AS [participant_type],
        SYSUTCDATETIME() AS [applied_at_utc];
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    SELECT
        N'FieldKonnect V6.5 database update FAILED. No change was committed.' AS [message],
        ERROR_NUMBER()  AS [error_number],
        ERROR_MESSAGE() AS [error_message];
    THROW;
END CATCH;
