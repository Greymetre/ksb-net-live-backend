SET NOCOUNT ON;

SELECT N'SET ANSI_NULLS ON;';
SELECT N'SET ANSI_PADDING ON;';
SELECT N'SET ANSI_WARNINGS ON;';
SELECT N'SET ARITHABORT ON;';
SELECT N'SET CONCAT_NULL_YIELDS_NULL ON;';
SELECT N'SET QUOTED_IDENTIFIER ON;';
SELECT N'SET NUMERIC_ROUNDABORT OFF;';
SELECT N'SET NOCOUNT ON;';
SELECT N'GO';
SELECT N'';

DECLARE @seed_tables TABLE ([sort_order] int NOT NULL, [table_name] sysname NOT NULL);
INSERT INTO @seed_tables ([sort_order], [table_name]) VALUES
    (10, N'countries'),
    (20, N'states'),
    (30, N'districts'),
    (40, N'cities'),
    (50, N'pincodes'),
    (60, N'permissions'),
    (70, N'roles'),
    (80, N'users'),
    (90, N'role_has_permissions'),
    (100, N'model_has_roles');

DECLARE @table sysname;
DECLARE seed_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT [table_name] FROM @seed_tables ORDER BY [sort_order];

OPEN seed_cursor;
FETCH NEXT FROM seed_cursor INTO @table;

WHILE @@FETCH_STATUS = 0
BEGIN
    DECLARE @qualified nvarchar(517) = QUOTENAME(N'dbo') + N'.' + QUOTENAME(@table);
    DECLARE @row_filter nvarchar(max) =
        CASE @table
            WHEN N'users' THEN
                N' WHERE [email] IN (N''gajendra@greymetre.io'', N''swaraj.khalate@ksb.com'')'
            WHEN N'model_has_roles' THEN
                N' WHERE [model_id] IN (SELECT [id] FROM [dbo].[users] WHERE [email] IN (N''gajendra@greymetre.io'', N''swaraj.khalate@ksb.com''))'
            ELSE N''
        END;
    DECLARE @columns nvarchar(max);
    DECLARE @values_expression nvarchar(max);
    DECLARE @has_identity bit =
        CASE WHEN EXISTS (
            SELECT 1
            FROM sys.identity_columns
            WHERE [object_id] = OBJECT_ID(@qualified)
        ) THEN 1 ELSE 0 END;

    SELECT @columns = STRING_AGG(CAST(QUOTENAME(c.[name]) AS nvarchar(max)), N', ')
        WITHIN GROUP (ORDER BY c.[column_id])
    FROM sys.columns c
    WHERE c.[object_id] = OBJECT_ID(@qualified)
      AND c.[is_computed] = 0
      AND c.[system_type_id] <> 189;

    SELECT @values_expression = STRING_AGG(
        CAST(
            N'CASE WHEN ' + QUOTENAME(c.[name]) + N' IS NULL THEN N''NULL'' ' +
            N'WHEN N''' + t.[name] + N''' IN (N''char'',N''varchar'',N''nchar'',N''nvarchar'',N''text'',N''ntext'',N''uniqueidentifier'') ' +
            N'THEN N''N'''''' + REPLACE(CONVERT(nvarchar(max),' + QUOTENAME(c.[name]) + N'),N'''''''',N'''''''''''') + N'''''''' ' +
            N'WHEN N''' + t.[name] + N''' IN (N''date'',N''datetime'',N''datetime2'',N''smalldatetime'',N''datetimeoffset'',N''time'') ' +
            N'THEN N'''''''' + CONVERT(nvarchar(48),' + QUOTENAME(c.[name]) + N',126) + N'''''''' ' +
            N'WHEN N''' + t.[name] + N''' IN (N''binary'',N''varbinary'',N''image'',N''timestamp'') ' +
            N'THEN CONVERT(nvarchar(max),' + QUOTENAME(c.[name]) + N',1) ' +
            N'ELSE CONVERT(nvarchar(max),' + QUOTENAME(c.[name]) + N') END'
            AS nvarchar(max)),
        N' + N'', '' + '
    ) WITHIN GROUP (ORDER BY c.[column_id])
    FROM sys.columns c
    INNER JOIN sys.types t ON c.[user_type_id] = t.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(@qualified)
      AND c.[is_computed] = 0
      AND c.[system_type_id] <> 189;

    SELECT N'-- Seed table: ' + @qualified;
    SELECT N'IF NOT EXISTS (SELECT 1 FROM ' + @qualified + N')';
    SELECT N'BEGIN';
    IF @has_identity = 1
        SELECT N'    SET IDENTITY_INSERT ' + @qualified + N' ON;';

    DECLARE @data_sql nvarchar(max) =
        N'SELECT N''    INSERT INTO ' + @qualified + N' (' + @columns + N') VALUES ('' + ' +
        @values_expression + N' + N'');'' FROM ' + @qualified + @row_filter + N';';
    EXEC sys.sp_executesql @data_sql;

    IF @has_identity = 1
        SELECT N'    SET IDENTITY_INSERT ' + @qualified + N' OFF;';
    SELECT N'END';
    SELECT N'GO';
    SELECT N'';

    FETCH NEXT FROM seed_cursor INTO @table;
END

CLOSE seed_cursor;
DEALLOCATE seed_cursor;
