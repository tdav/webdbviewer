using WebDbViewer.Completion.Semantics;
using WebDbViewer.Core;

namespace WebDbViewer.Completion;

/// <summary>Подсказка по вызову функции, внутри скобок которой стоит каретка.</summary>
/// <param name="Label">Сигнатура целиком, как её показывать пользователю.</param>
/// <param name="Documentation">Комментарий или краткое описание; null — описания нет.</param>
/// <param name="ActiveParameter">Номер аргумента под кареткой, с нуля.</param>
public sealed record SignatureInfo(string Label, string? Documentation, int ActiveParameter);

/// <summary>
/// Поиск сигнатуры вызова: сначала функции и процедуры схемы из кэша метаданных,
/// затем встроенные средства диалекта. Ничего не найдено — подсказки нет.
/// </summary>
internal static class SignatureHelp
{
    public static SignatureInfo? Describe(
        string sql, int caret, DbKind dialect, IReadOnlyList<SchemaSnapshot> snapshots)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        if (CaretText.EnclosingCall(sql[..caret]) is not { } call)
            return null;

        foreach (var snapshot in snapshots)
        {
            foreach (var routine in snapshot.Routines)
            {
                if (!string.Equals(routine.Name, call.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var arguments = string.IsNullOrWhiteSpace(routine.ArgumentsSignature)
                    ? ""
                    : routine.ArgumentsSignature;
                var label = $"{routine.Schema}.{routine.Name}({arguments})";
                var documentation = routine.Comment;
                if (!string.IsNullOrWhiteSpace(routine.ReturnType))
                {
                    var returns = $"возвращает {routine.ReturnType}";
                    documentation = string.IsNullOrWhiteSpace(documentation)
                        ? returns
                        : $"{documentation} — {returns}";
                }
                return new SignatureInfo(label, documentation, call.ArgumentIndex);
            }
        }

        foreach (var entry in DialectVocabulary.Functions(dialect))
        {
            if (entry.Signature is null)
                continue;
            // Пакетные подпрограммы записаны полным именем (DBMS_OUTPUT.PUT_LINE), а разбор
            // текста видит только часть после точки — сравниваем и с ней.
            var shortName = entry.Name[(entry.Name.LastIndexOf('.') + 1)..];
            if (!string.Equals(shortName, call.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            return new SignatureInfo(entry.Signature, entry.Summary, call.ArgumentIndex);
        }

        return null;
    }
}
