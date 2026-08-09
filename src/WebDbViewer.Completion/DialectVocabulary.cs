using WebDbViewer.Core;

namespace WebDbViewer.Completion;

/// <summary>Встроенный элемент диалекта: имя, сигнатура для подсказки, краткое описание.</summary>
/// <param name="Name">Имя как оно вставляется в текст.</param>
/// <param name="Signature">Сигнатура для колонки Detail; null — элемент без аргументов.</param>
/// <param name="Summary">Краткое описание на русском для панели документации.</param>
internal sealed record BuiltinEntry(string Name, string? Signature, string Summary);

/// <summary>
/// Справочник встроенных средств диалекта: функции, константы без скобок, типы данных.
/// Каталог БД их не отдаёт (в ALL_PROCEDURES и pg_proc нет ни SYSDATE, ни COALESCE),
/// поэтому список статический. Он намеренно не полон: включено то, что реально набирают.
/// </summary>
internal static class DialectVocabulary
{
    public static IReadOnlyList<BuiltinEntry> Functions(DbKind dialect) =>
        dialect == DbKind.Oracle ? OracleFunctions : PostgresFunctions;

    /// <summary>Элементы, вызываемые без скобок: SYSDATE, ROWNUM, CURRENT_DATE и т.п.</summary>
    public static IReadOnlyList<BuiltinEntry> Constants(DbKind dialect) =>
        dialect == DbKind.Oracle ? OracleConstants : PostgresConstants;

    public static IReadOnlyList<string> DataTypes(DbKind dialect) =>
        dialect == DbKind.Oracle ? OracleTypes : PostgresTypes;

    // ================================================================== Oracle

    private static readonly BuiltinEntry[] OracleFunctions =
    [
        new("NVL", "NVL(expr, replacement)", "Заменяет NULL указанным значением"),
        new("NVL2", "NVL2(expr, if_not_null, if_null)", "Выбор значения по признаку NULL"),
        new("COALESCE", "COALESCE(expr, ...)", "Первое не-NULL значение из списка"),
        new("NULLIF", "NULLIF(expr1, expr2)", "NULL, если значения равны"),
        new("DECODE", "DECODE(expr, search, result, ..., default)", "Поиск значения по списку пар"),
        new("CASE", "CASE WHEN condition THEN result ELSE other END", "Условное выражение"),
        new("GREATEST", "GREATEST(expr, ...)", "Наибольшее из значений"),
        new("LEAST", "LEAST(expr, ...)", "Наименьшее из значений"),

        new("TO_CHAR", "TO_CHAR(expr [, format])", "Преобразование в строку"),
        new("TO_DATE", "TO_DATE(text, format)", "Разбор строки в дату"),
        new("TO_NUMBER", "TO_NUMBER(text [, format])", "Разбор строки в число"),
        new("TO_TIMESTAMP", "TO_TIMESTAMP(text, format)", "Разбор строки в timestamp"),
        new("CAST", "CAST(expr AS type)", "Приведение типа"),
        new("EXTRACT", "EXTRACT(field FROM expr)", "Часть даты или интервала"),
        new("TRUNC", "TRUNC(expr [, unit])", "Усечение числа или даты"),
        new("ROUND", "ROUND(expr [, digits])", "Округление числа или даты"),
        new("ADD_MONTHS", "ADD_MONTHS(date, months)", "Сдвиг даты на месяцы"),
        new("MONTHS_BETWEEN", "MONTHS_BETWEEN(date1, date2)", "Разница дат в месяцах"),
        new("LAST_DAY", "LAST_DAY(date)", "Последний день месяца"),
        new("NEXT_DAY", "NEXT_DAY(date, weekday)", "Ближайший указанный день недели"),

        new("SUBSTR", "SUBSTR(text, start [, length])", "Подстрока"),
        new("INSTR", "INSTR(text, substring [, start [, occurrence]])", "Позиция подстроки"),
        new("LENGTH", "LENGTH(text)", "Длина строки"),
        new("LPAD", "LPAD(text, length [, pad])", "Дополнение слева"),
        new("RPAD", "RPAD(text, length [, pad])", "Дополнение справа"),
        new("TRIM", "TRIM([LEADING|TRAILING|BOTH] [char FROM] text)", "Обрезка символов по краям"),
        new("LTRIM", "LTRIM(text [, chars])", "Обрезка слева"),
        new("RTRIM", "RTRIM(text [, chars])", "Обрезка справа"),
        new("REPLACE", "REPLACE(text, search [, replacement])", "Замена подстроки"),
        new("TRANSLATE", "TRANSLATE(text, from_chars, to_chars)", "Посимвольная замена"),
        new("UPPER", "UPPER(text)", "Верхний регистр"),
        new("LOWER", "LOWER(text)", "Нижний регистр"),
        new("INITCAP", "INITCAP(text)", "Первая буква каждого слова заглавная"),
        new("CONCAT", "CONCAT(text1, text2)", "Склейка двух строк"),
        new("REGEXP_LIKE", "REGEXP_LIKE(text, pattern [, flags])", "Проверка соответствия регулярному выражению"),
        new("REGEXP_SUBSTR", "REGEXP_SUBSTR(text, pattern [, position [, occurrence [, flags]]])", "Извлечение по регулярному выражению"),
        new("REGEXP_REPLACE", "REGEXP_REPLACE(text, pattern [, replacement])", "Замена по регулярному выражению"),
        new("REGEXP_INSTR", "REGEXP_INSTR(text, pattern [, position])", "Позиция совпадения регулярного выражения"),

        new("COUNT", "COUNT(expr | *)", "Количество строк"),
        new("SUM", "SUM(expr)", "Сумма"),
        new("AVG", "AVG(expr)", "Среднее"),
        new("MIN", "MIN(expr)", "Минимум"),
        new("MAX", "MAX(expr)", "Максимум"),
        new("LISTAGG", "LISTAGG(expr, delimiter) WITHIN GROUP (ORDER BY ...)", "Склейка значений группы в строку"),
        new("ROW_NUMBER", "ROW_NUMBER() OVER (ORDER BY ...)", "Номер строки в окне"),
        new("RANK", "RANK() OVER (ORDER BY ...)", "Ранг с пропусками"),
        new("DENSE_RANK", "DENSE_RANK() OVER (ORDER BY ...)", "Ранг без пропусков"),
        new("LAG", "LAG(expr [, offset [, default]]) OVER (ORDER BY ...)", "Значение предыдущей строки окна"),
        new("LEAD", "LEAD(expr [, offset [, default]]) OVER (ORDER BY ...)", "Значение следующей строки окна"),

        new("ABS", "ABS(number)", "Модуль числа"),
        new("CEIL", "CEIL(number)", "Округление вверх"),
        new("FLOOR", "FLOOR(number)", "Округление вниз"),
        new("MOD", "MOD(dividend, divisor)", "Остаток от деления"),
        new("POWER", "POWER(base, exponent)", "Возведение в степень"),
        new("SQRT", "SQRT(number)", "Квадратный корень"),
        new("SYS_GUID", "SYS_GUID()", "Новый GUID в виде RAW(16)"),

        // Пакетные подпрограммы: в ALL_PROCEDURES схемы пользователя их нет — они в SYS.
        new("DBMS_OUTPUT.PUT_LINE", "DBMS_OUTPUT.PUT_LINE(text)", "Вывод строки в буфер сессии"),
        new("DBMS_RANDOM.VALUE", "DBMS_RANDOM.VALUE([low, high])", "Случайное число"),
        new("DBMS_LOB.SUBSTR", "DBMS_LOB.SUBSTR(lob [, amount [, offset]])", "Подстрока из LOB"),
        new("DBMS_LOB.GETLENGTH", "DBMS_LOB.GETLENGTH(lob)", "Длина LOB"),
    ];

    /// <summary>Псевдоколонки последовательности: единственное, что бывает после «имя_последовательности.».</summary>
    public static readonly BuiltinEntry[] OracleSequenceMembers =
    [
        new("NEXTVAL", null, "Следующее значение последовательности"),
        new("CURRVAL", null, "Текущее значение последовательности в сессии"),
    ];

    private static readonly BuiltinEntry[] OracleConstants =
    [
        new("SYSDATE", null, "Текущие дата и время сервера"),
        new("SYSTIMESTAMP", null, "Текущий timestamp с часовым поясом"),
        new("CURRENT_DATE", null, "Текущая дата в часовом поясе сессии"),
        new("CURRENT_TIMESTAMP", null, "Текущий timestamp в часовом поясе сессии"),
        new("USER", null, "Имя текущего пользователя БД"),
        new("ROWNUM", null, "Псевдоколонка: номер строки до сортировки"),
        new("ROWID", null, "Псевдоколонка: физический адрес строки"),
        new("LEVEL", null, "Псевдоколонка иерархического запроса CONNECT BY"),
        new("DUAL", null, "Служебная таблица из одной строки"),
    ];

    private static readonly string[] OracleTypes =
    [
        "NUMBER", "INTEGER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE",
        "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR", "CLOB", "NCLOB", "LONG",
        "BLOB", "RAW", "BFILE",
        "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE", "TIMESTAMP WITH LOCAL TIME ZONE",
        "INTERVAL YEAR TO MONTH", "INTERVAL DAY TO SECOND",
        "BOOLEAN", "JSON", "XMLTYPE", "ROWID", "UROWID",
    ];

    // ================================================================== PostgreSQL

    private static readonly BuiltinEntry[] PostgresFunctions =
    [
        new("coalesce", "coalesce(expr, ...)", "Первое не-NULL значение из списка"),
        new("nullif", "nullif(expr1, expr2)", "NULL, если значения равны"),
        new("greatest", "greatest(expr, ...)", "Наибольшее из значений"),
        new("least", "least(expr, ...)", "Наименьшее из значений"),

        new("now", "now()", "Текущий timestamp начала транзакции"),
        new("age", "age(timestamp [, timestamp])", "Разница дат как интервал"),
        new("date_trunc", "date_trunc(field, timestamp)", "Усечение даты до единицы"),
        new("date_part", "date_part(field, timestamp)", "Часть даты числом"),
        new("extract", "extract(field FROM timestamp)", "Часть даты или интервала"),
        new("to_char", "to_char(expr, format)", "Преобразование в строку"),
        new("to_date", "to_date(text, format)", "Разбор строки в дату"),
        new("to_number", "to_number(text, format)", "Разбор строки в число"),
        new("to_timestamp", "to_timestamp(text, format)", "Разбор строки в timestamp"),
        new("make_date", "make_date(year, month, day)", "Сборка даты из частей"),
        new("generate_series", "generate_series(start, stop [, step])", "Ряд значений как набор строк"),

        new("substring", "substring(text FROM start FOR length)", "Подстрока"),
        new("position", "position(substring IN text)", "Позиция подстроки"),
        new("length", "length(text)", "Длина строки"),
        new("lpad", "lpad(text, length [, fill])", "Дополнение слева"),
        new("rpad", "rpad(text, length [, fill])", "Дополнение справа"),
        new("btrim", "btrim(text [, chars])", "Обрезка символов по краям"),
        new("ltrim", "ltrim(text [, chars])", "Обрезка слева"),
        new("rtrim", "rtrim(text [, chars])", "Обрезка справа"),
        new("replace", "replace(text, search, replacement)", "Замена подстроки"),
        new("split_part", "split_part(text, delimiter, n)", "N-я часть строки по разделителю"),
        new("concat", "concat(expr, ...)", "Склейка значений, NULL игнорируются"),
        new("concat_ws", "concat_ws(separator, expr, ...)", "Склейка через разделитель"),
        new("format", "format(formatstr, ...)", "Форматирование строки"),
        new("upper", "upper(text)", "Верхний регистр"),
        new("lower", "lower(text)", "Нижний регистр"),
        new("initcap", "initcap(text)", "Первая буква каждого слова заглавная"),
        new("md5", "md5(text)", "MD5-хэш строки"),
        new("regexp_replace", "regexp_replace(text, pattern, replacement [, flags])", "Замена по регулярному выражению"),
        new("regexp_matches", "regexp_matches(text, pattern [, flags])", "Совпадения регулярного выражения"),

        new("count", "count(expr | *)", "Количество строк"),
        new("sum", "sum(expr)", "Сумма"),
        new("avg", "avg(expr)", "Среднее"),
        new("min", "min(expr)", "Минимум"),
        new("max", "max(expr)", "Максимум"),
        new("string_agg", "string_agg(expr, delimiter [ORDER BY ...])", "Склейка значений группы в строку"),
        new("array_agg", "array_agg(expr [ORDER BY ...])", "Сборка значений группы в массив"),
        new("json_agg", "json_agg(expr)", "Сборка значений группы в JSON-массив"),
        new("jsonb_agg", "jsonb_agg(expr)", "Сборка значений группы в jsonb-массив"),
        new("jsonb_build_object", "jsonb_build_object(key, value, ...)", "Сборка jsonb-объекта из пар"),
        new("jsonb_array_elements", "jsonb_array_elements(jsonb)", "Элементы jsonb-массива как строки"),
        new("array_length", "array_length(array, dimension)", "Длина массива по измерению"),
        new("unnest", "unnest(array)", "Развёртка массива в строки"),
        new("row_number", "row_number() OVER (ORDER BY ...)", "Номер строки в окне"),
        new("rank", "rank() OVER (ORDER BY ...)", "Ранг с пропусками"),
        new("dense_rank", "dense_rank() OVER (ORDER BY ...)", "Ранг без пропусков"),
        new("lag", "lag(expr [, offset [, default]]) OVER (ORDER BY ...)", "Значение предыдущей строки окна"),
        new("lead", "lead(expr [, offset [, default]]) OVER (ORDER BY ...)", "Значение следующей строки окна"),

        new("abs", "abs(number)", "Модуль числа"),
        new("ceil", "ceil(number)", "Округление вверх"),
        new("floor", "floor(number)", "Округление вниз"),
        new("round", "round(number [, digits])", "Округление"),
        new("power", "power(base, exponent)", "Возведение в степень"),
        new("sqrt", "sqrt(number)", "Квадратный корень"),
        new("mod", "mod(dividend, divisor)", "Остаток от деления"),
        new("random", "random()", "Случайное число от 0 до 1"),
        new("gen_random_uuid", "gen_random_uuid()", "Новый UUID версии 4"),
    ];

    private static readonly BuiltinEntry[] PostgresConstants =
    [
        new("current_date", null, "Текущая дата"),
        new("current_time", null, "Текущее время"),
        new("current_timestamp", null, "Текущий timestamp начала транзакции"),
        new("localtimestamp", null, "Текущий timestamp без часового пояса"),
        new("current_user", null, "Имя текущего пользователя"),
        new("session_user", null, "Имя пользователя сессии"),
        new("current_schema", null, "Текущая схема"),
    ];

    private static readonly string[] PostgresTypes =
    [
        "smallint", "integer", "bigint", "numeric", "real", "double precision",
        "smallserial", "serial", "bigserial", "money",
        "text", "varchar", "char", "bytea",
        "boolean", "uuid", "xml", "json", "jsonb",
        "date", "time", "timetz", "timestamp", "timestamptz", "interval",
        "inet", "cidr", "macaddr", "tsvector", "tsquery",
        "int4range", "int8range", "numrange", "tsrange", "tstzrange", "daterange",
    ];
}
