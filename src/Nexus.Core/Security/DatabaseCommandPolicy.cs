using System.Text.RegularExpressions;

namespace Nexus.Core.Security;

public static partial class DatabaseCommandPolicy
{
    [GeneratedRegex(@"(?is)(;|--|/\*|\*/|\b(insert|update|delete|merge|alter|drop|truncate|create|grant|revoke|execute|exec|begin|declare|call|commit|rollback)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeReadSql();

    [GeneratedRegex(@"(?is)(;|--|/\*|\*/|\b(alter|drop|truncate|create|grant|revoke|execute|exec|begin|declare|call|commit|rollback|shutdown|startup|audit|noaudit)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeWriteSql();

    [GeneratedRegex(@"(?is)^\s*(insert|update|delete|merge)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedWriteSql();

    [GeneratedRegex(@"(?is)\bwhere\b", RegexOptions.CultureInvariant)]
    private static partial Regex WhereClause();

    public static void ValidateReadOnly(string query)
    {
        var normalized = RequireQuery(query);
        if (!(normalized.StartsWith("select", StringComparison.OrdinalIgnoreCase) ||
              normalized.StartsWith("with", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Only SELECT and WITH queries are allowed for database.query.");
        if (UnsafeReadSql().IsMatch(query))
            throw new InvalidOperationException("The query contains a write, administrative, comment, or multi-statement operation.");
    }

    public static void ValidateControlledWrite(string query)
    {
        var normalized = RequireQuery(query);
        if (!AllowedWriteSql().IsMatch(normalized))
            throw new InvalidOperationException("database.execute only allows one INSERT, UPDATE, DELETE, or MERGE statement.");
        if (UnsafeWriteSql().IsMatch(query))
            throw new InvalidOperationException("Schema, security, transaction-control, comments, and multi-statement SQL are blocked.");
        if ((normalized.StartsWith("update", StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith("delete", StringComparison.OrdinalIgnoreCase)) && !WhereClause().IsMatch(normalized))
            throw new InvalidOperationException("UPDATE and DELETE require a WHERE clause.");
    }

    private static string RequireQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("A SQL statement is required.");
        if (query.Length > 100_000) throw new InvalidOperationException("The SQL statement is too large.");
        return query.Trim();
    }
}
