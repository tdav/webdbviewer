using System.Text.RegularExpressions;
using WebDbViewer.Core;

namespace WebDbViewer.Completion;

/// <summary>
/// Локальное правило квотирования идентификаторов для InsertText автодополнения.
/// Дублирует провайдерное правило (IDbProvider.QuoteIdentifier) простой функцией,
/// чтобы движок не зависел от провайдеров.
/// </summary>
internal static partial class SqlIdentifierQuoting
{
    [GeneratedRegex("^[a-z_][a-z0-9_$]*$")]
    private static partial Regex PgPlainIdentifier();

    [GeneratedRegex("^[A-Z][A-Z0-9_$#]*$")]
    private static partial Regex OraclePlainIdentifier();

    /// <summary>Квотирует идентификатор, только если без кавычек он изменит регистр/смысл.</summary>
    public static string Quote(string identifier, DbKind kind)
    {
        if (string.IsNullOrEmpty(identifier))
            return identifier;

        var plain = kind == DbKind.Oracle
            ? OraclePlainIdentifier().IsMatch(identifier)
            : PgPlainIdentifier().IsMatch(identifier);

        return plain ? identifier : "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}
