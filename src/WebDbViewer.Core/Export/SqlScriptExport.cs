using System.Data.Common;
using System.Globalization;
using System.Text;

namespace WebDbViewer.Core.Export;

/// <summary>
/// Форматирование значения из <see cref="DbDataReader"/> в SQL-литерал под диалект.
/// Здесь собраны все расхождения PostgreSQL и Oracle: даты, двоичные данные,
/// логический тип и специальные значения плавающей точки. Отдельного интерфейса
/// с двумя реализациями (как у DDL и DML) нет намеренно: диалектных различий
/// на десяток строк, а работы с каталогом БД — никакой.
/// </summary>
public static class SqlLiteral
{
    /// <summary>
    /// Предел литерала RAW в Oracle — 2000 байт (4000 шестнадцатеричных символов в HEXTORAW).
    /// Значение крупнее в скрипт не помещается; см. <see cref="ExceedsOracleRawLimit"/>.
    /// </summary>
    public const int OracleRawLiteralLimit = 2000;

    /// <summary>Доли секунды: шесть знаков — микросекунды, общий знаменатель PostgreSQL и Oracle.</summary>
    private const string FractionFormat = "ffffff";

    /// <summary>
    /// SQL-литерал значения. Значение <c>null</c> и <see cref="DBNull"/> дают <c>NULL</c>;
    /// двоичное значение сверх предела Oracle — тоже <c>NULL</c> (проверяйте
    /// <see cref="ExceedsOracleRawLimit"/> заранее, если потеря должна быть видна в скрипте).
    /// </summary>
    public static string Format(object? value, DbKind kind) => value switch
    {
        null or DBNull => "NULL",

        string s => Quote(s),
        char c => Quote(c.ToString()),

        bool b => kind == DbKind.Oracle ? (b ? "1" : "0") : (b ? "true" : "false"),

        byte or sbyte or short or ushort or int or uint or long or ulong
            => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        float f => Real(f, double.IsNaN(f), double.IsInfinity(f), f < 0, f.ToString("R", CultureInfo.InvariantCulture), kind, single: true),
        double db => Real(db, double.IsNaN(db), double.IsInfinity(db), db < 0, db.ToString("R", CultureInfo.InvariantCulture), kind, single: false),

        DateTime dt => DateTimeLiteral(dt, kind),
        DateTimeOffset dto => DateTimeOffsetLiteral(dto, kind),
        // Культура обязательна: под негригорианским календарём (например, ar-SA)
        // формат по умолчанию даёт другую дату, а не другое написание той же.
        DateOnly date => kind == DbKind.Oracle
            ? $"TO_DATE('{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}','YYYY-MM-DD')"
            : $"DATE '{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'",
        // В Oracle нет типа TIME: значение уходит строковым литералом, который
        // сервер приведёт по типу колонки (обычно VARCHAR2 или INTERVAL).
        TimeOnly time => kind == DbKind.Oracle
            ? Quote(time.ToString("HH:mm:ss." + FractionFormat, CultureInfo.InvariantCulture))
            : $"TIME '{time.ToString("HH:mm:ss." + FractionFormat, CultureInfo.InvariantCulture)}'",
        TimeSpan ts => IntervalLiteral(ts, kind),

        // ponytail: Oracle-колонка RAW(16) под GUID потребовала бы HEXTORAW, но тип колонки
        // здесь неизвестен. Строковый литерал верен для VARCHAR2 — самого частого варианта.
        Guid g => kind == DbKind.Oracle ? Quote(g.ToString()) : $"'{g}'::uuid",

        byte[] bytes => BinaryLiteral(bytes, kind),

        // Провайдер-специфичные типы (диапазоны, массивы, геометрия): строковый литерал
        // без явного типа сервер приводит к типу колонки сам.
        _ => Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
    };

    /// <summary>
    /// Двоичное значение не помещается в литерал Oracle и будет заменено на <c>NULL</c>.
    /// Для PostgreSQL всегда false — предела на литерал bytea нет.
    /// </summary>
    public static bool ExceedsOracleRawLimit(object? value, DbKind kind) =>
        kind == DbKind.Oracle && value is byte[] { Length: > OracleRawLiteralLimit };

    /// <summary>Строковый литерал: одинарная кавычка удваивается.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    // ---------------------------------------------------------------- Диалектные различия

    /// <summary>
    /// Плавающая точка. NaN и бесконечности не имеют обычного литерала:
    /// PostgreSQL принимает строку с приведением типа, Oracle — специальные константы.
    /// </summary>
    private static string Real(
        object value, bool isNan, bool isInfinity, bool isNegative, string plain, DbKind kind, bool single)
    {
        if (!isNan && !isInfinity)
            return plain;

        if (kind == DbKind.Oracle)
        {
            var prefix = single ? "BINARY_FLOAT" : "BINARY_DOUBLE";
            if (isNan)
                return $"{prefix}_NAN";
            return isNegative ? $"-{prefix}_INFINITY" : $"{prefix}_INFINITY";
        }

        var pgType = single ? "real" : "double precision";
        var text = isNan ? "NaN" : isNegative ? "-Infinity" : "Infinity";
        return $"'{text}'::{pgType}";
    }

    private static string DateTimeLiteral(DateTime value, DbKind kind)
    {
        var text = value.ToString("yyyy-MM-dd HH:mm:ss." + FractionFormat, CultureInfo.InvariantCulture);
        return kind == DbKind.Oracle
            ? $"TO_TIMESTAMP('{text}','YYYY-MM-DD HH24:MI:SS.FF6')"
            : $"TIMESTAMP '{text}'";
    }

    private static string DateTimeOffsetLiteral(DateTimeOffset value, DbKind kind)
    {
        var text = value.ToString("yyyy-MM-dd HH:mm:ss." + FractionFormat + " zzz", CultureInfo.InvariantCulture);
        return kind == DbKind.Oracle
            ? $"TO_TIMESTAMP_TZ('{text}','YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM')"
            : $"TIMESTAMP WITH TIME ZONE '{text}'";
    }

    /// <summary>
    /// Интервал. Знак выносится наружу унарным минусом: он допустим в обоих диалектах
    /// и избавляет от разнобоя в записи отрицательных полей внутри литерала.
    /// </summary>
    private static string IntervalLiteral(TimeSpan value, DbKind kind)
    {
        var sign = value < TimeSpan.Zero ? "-" : "";
        var abs = value < TimeSpan.Zero ? value.Negate() : value;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{abs.Days} {abs.Hours:00}:{abs.Minutes:00}:{abs.Seconds:00}.{abs.Milliseconds * 1000:000000}");

        return kind == DbKind.Oracle
            ? $"{sign}INTERVAL '{text}' DAY TO SECOND(6)"
            : $"{sign}INTERVAL '{text}'";
    }

    private static string BinaryLiteral(byte[] bytes, DbKind kind)
    {
        if (kind != DbKind.Oracle)
            return $"'\\x{Convert.ToHexString(bytes)}'::bytea";

        return bytes.Length > OracleRawLiteralLimit
            ? "NULL"
            : $"HEXTORAW('{Convert.ToHexString(bytes)}')";
    }
}

/// <summary>Итог записи INSERT-скрипта.</summary>
/// <param name="RowCount">Сколько строк записано.</param>
/// <param name="Truncated">true — достигнут предел строк, в источнике осталось ещё.</param>
public readonly record struct InsertScriptResult(long RowCount, bool Truncated);

/// <summary>
/// Писатель INSERT-скрипта: одна строка данных — один оператор
/// <c>INSERT INTO … VALUES (…);</c>. Многострочный VALUES не используется намеренно:
/// Oracle его не поддерживает, а построчная форма переживает разбиение скрипта
/// сплиттером при импорте. Данные не буферизуются — пишутся по мере чтения курсора.
/// </summary>
public static class InsertScriptWriter
{
    /// <summary>
    /// Пишет содержимое <paramref name="reader"/> в <paramref name="output"/>.
    /// <paramref name="target"/> — уже квотированное имя таблицы-приёмника
    /// (обычно «схема».«таблица»), <paramref name="quoteIdentifier"/> — квотирование
    /// имён колонок по правилам диалекта (<see cref="IDbProvider.QuoteIdentifier"/>).
    /// </summary>
    public static async Task<InsertScriptResult> WriteAsync(
        DbDataReader reader,
        string target,
        TextWriter output,
        DbKind kind,
        Func<string, string> quoteIdentifier,
        long maxRows,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(quoteIdentifier);

        var fieldCount = reader.FieldCount;
        if (fieldCount == 0)
            return new InsertScriptResult(0, false);

        var names = new string[fieldCount];
        for (var i = 0; i < fieldCount; i++)
            names[i] = reader.GetName(i);

        var prefix = new StringBuilder("INSERT INTO ").Append(target).Append(" (")
            .AppendJoin(", ", names.Select(quoteIdentifier))
            .Append(") VALUES (")
            .ToString();

        long rows = 0;
        var line = new StringBuilder(prefix.Length + 128);

        while (rows < maxRows && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            line.Clear().Append(prefix);

            for (var i = 0; i < fieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);

                // Потеря значения должна быть видна в скрипте, а не случиться молча.
                if (SqlLiteral.ExceedsOracleRawLimit(value, kind))
                {
                    var size = ((byte[])value!).Length;
                    await output.WriteLineAsync(
                        $"-- ВНИМАНИЕ: значение колонки {quoteIdentifier(names[i])} ({size} байт) превышает "
                        + $"предел литерала RAW ({SqlLiteral.OracleRawLiteralLimit} байт) и заменено на NULL.")
                        .ConfigureAwait(false);
                }

                if (i > 0)
                    line.Append(", ");
                line.Append(SqlLiteral.Format(value, kind));
            }

            line.Append(");");
            await output.WriteLineAsync(line.ToString()).ConfigureAwait(false);
            rows++;
        }

        // Предел достигнут, но источник не исчерпан — вызывающий предупредит пользователя.
        var truncated = rows >= maxRows && await reader.ReadAsync(ct).ConfigureAwait(false);
        return new InsertScriptResult(rows, truncated);
    }
}
