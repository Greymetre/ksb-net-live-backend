using System.Text.RegularExpressions;

namespace Infrastructure.Data;

/// <summary>
/// Normalizes the small amount of legacy MySQL SQL still used by the mobile
/// compatibility endpoints. New data access code should use LINQ/EF Core or
/// native SQL Server syntax directly.
/// </summary>
public static partial class SqlServerSql
{
    public static object ParameterValue(object? value) =>
        value switch
        {
            null => DBNull.Value,
            ulong unsignedLong => Convert.ToDecimal(unsignedLong),
            uint unsignedInt => Convert.ToInt64(unsignedInt),
            ushort unsignedShort => Convert.ToInt32(unsignedShort),
            byte unsignedByte => Convert.ToInt16(unsignedByte),
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            TimeOnly time => time.ToTimeSpan(),
            _ => value
        };

    public static string Normalize(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return sql;
        }

        var normalized = sql
            .Replace("UTC_TIMESTAMP()", "SYSUTCDATETIME()", StringComparison.OrdinalIgnoreCase)
            .Replace("LAST_INSERT_ID()", "SCOPE_IDENTITY()", StringComparison.OrdinalIgnoreCase)
            .Replace("`", string.Empty, StringComparison.Ordinal);

        normalized = JsonExtractRegex().Replace(normalized, "JSON_VALUE(${expression}, '${path}')");
        normalized = UnsignedCastRegex().Replace(normalized, "TRY_CONVERT(decimal(20,0), ${expression})");
        normalized = FindInSetRegex().Replace(
            normalized,
            "CHARINDEX(',' + CONVERT(nvarchar(max), ${needle}) + ',', ',' + COALESCE(${haystack}, '') + ',') > 0");
        normalized = SubstringIndexRegex().Replace(
            normalized,
            "LEFT(${expression}, CHARINDEX(',', ${expression} + ',') - 1)");
        normalized = LimitOffsetRegex().Replace(
            normalized,
            "OFFSET ${offset} ROWS FETCH NEXT ${count} ROWS ONLY");

        // Resolve innermost SELECT ... LIMIT clauses first. Repeating allows
        // scalar subqueries and then their containing query to be converted.
        string previous;
        do
        {
            previous = normalized;
            normalized = SelectLimitRegex().Replace(
                normalized,
                match => $"SELECT TOP ({match.Groups["count"].Value}) {match.Groups["body"].Value}");
        }
        while (!string.Equals(previous, normalized, StringComparison.Ordinal));

        return normalized;
    }

    [GeneratedRegex(
        @"JSON_UNQUOTE\s*\(\s*JSON_EXTRACT\s*\(\s*(?<expression>[^,]+?)\s*,\s*'(?<path>\$\.[^']+)'\s*\)\s*\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex JsonExtractRegex();

    [GeneratedRegex(
        @"CAST\s*\(\s*(?<expression>.*?)\s+AS\s+UNSIGNED\s*\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnsignedCastRegex();

    [GeneratedRegex(
        @"FIND_IN_SET\s*\(\s*(?<needle>[^,]+?)\s*,\s*(?<haystack>[^)]+?)\s*\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex FindInSetRegex();

    [GeneratedRegex(
        @"SUBSTRING_INDEX\s*\(\s*(?<expression>[^,]+?)\s*,\s*','\s*,\s*1\s*\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SubstringIndexRegex();

    [GeneratedRegex(
        @"LIMIT\s+(?<count>\d+)\s+OFFSET\s+(?<offset>\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex LimitOffsetRegex();

    [GeneratedRegex(
        @"SELECT\s+(?!TOP\s*\()(?<body>(?:(?!\bSELECT\b).)*?)\s+LIMIT\s+(?<count>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SelectLimitRegex();
}
