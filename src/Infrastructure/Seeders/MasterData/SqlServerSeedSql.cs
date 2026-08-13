using System.Text.RegularExpressions;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Seeders.MasterData;

internal static partial class SqlServerSeedSql
{
    public static Task ExecuteUpsertAsync(
        AppDbContext db,
        string mysqlInsert,
        CancellationToken cancellationToken)
    {
        var match = InsertRegex().Match(mysqlInsert);
        if (!match.Success)
        {
            throw new InvalidOperationException("Unsupported legacy master-data seed SQL.");
        }

        var table = Unquote(match.Groups["table"].Value);
        var columns = match.Groups["columns"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unquote)
            .ToArray();
        var values = match.Groups["values"].Value.Trim().TrimEnd(';');

        if (columns.Length == 0 || !columns.Contains("id", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Seed table '{table}' must contain an id column.");
        }

        var quotedColumns = columns.Select(Quote).ToArray();
        var updateColumns = columns.Where(column => !column.Equals("id", StringComparison.OrdinalIgnoreCase)).ToArray();
        var updateSql = string.Join(", ", updateColumns.Select(column => $"target.{Quote(column)} = source.{Quote(column)}"));
        var insertValues = string.Join(", ", columns.Select(column => $"source.{Quote(column)}"));
        var matchSql = table.Equals("permissions", StringComparison.OrdinalIgnoreCase)
            && columns.Contains("name", StringComparer.OrdinalIgnoreCase)
            && columns.Contains("guard_name", StringComparer.OrdinalIgnoreCase)
                ? "target.[name] = source.[name] AND target.[guard_name] = source.[guard_name]"
                : "target.[id] = source.[id]";

        var sql = $"""
SET IDENTITY_INSERT {Quote(table)} ON;
MERGE {Quote(table)} WITH (HOLDLOCK) AS target
USING (VALUES
{values}
) AS source ({string.Join(", ", quotedColumns)})
ON {matchSql}
WHEN MATCHED THEN UPDATE SET {updateSql}
WHEN NOT MATCHED THEN
    INSERT ({string.Join(", ", quotedColumns)})
    VALUES ({insertValues});
SET IDENTITY_INSERT {Quote(table)} OFF;
""";

        return db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    private static string Unquote(string identifier) => identifier.Trim().Trim('`', '[', ']');

    [GeneratedRegex(
        @"^\s*INSERT\s+INTO\s+(?<table>`?[\w]+`?)\s*\((?<columns>.*?)\)\s*VALUES\s*(?<values>.*?)\s*ON\s+DUPLICATE\s+KEY\s+UPDATE\b.*?;\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InsertRegex();
}
