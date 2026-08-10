using System.Globalization;
using WebDbViewer.Core;
using WebDbViewer.Core.Export;

namespace WebDbViewer.Tests.Unit.Export;

/// <summary>Тесты форматирования SQL-литералов для скриптового экспорта (оба диалекта).</summary>
public class SqlLiteralTests
{
    /// <summary>Выполняет проверку под подменённой культурой и всегда возвращает исходную.</summary>
    private static void WithCulture(string name, Action assert)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try
        {
            assert();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ---------------------------------------------------------------- NULL

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void Null_BecomesNullKeyword(DbKind kind)
    {
        Assert.Equal("NULL", SqlLiteral.Format(null, kind));
        Assert.Equal("NULL", SqlLiteral.Format(DBNull.Value, kind));
    }

    // ---------------------------------------------------------------- Строки

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void String_Plain_IsQuoted(DbKind kind)
        => Assert.Equal("'abc'", SqlLiteral.Format("abc", kind));

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void String_SingleQuote_IsDoubled(DbKind kind)
        => Assert.Equal("'O''Brien'", SqlLiteral.Format("O'Brien", kind));

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void String_Newline_IsKeptVerbatim(DbKind kind)
        => Assert.Equal("'a\nb'", SqlLiteral.Format("a\nb", kind));

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void String_Empty_IsTwoQuotes(DbKind kind)
        => Assert.Equal("''", SqlLiteral.Format("", kind));

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void String_Cyrillic_IsKeptAsIs(DbKind kind)
        => Assert.Equal("'Привет, мир'", SqlLiteral.Format("Привет, мир", kind));

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void Char_IsQuotedAsString(DbKind kind)
        => Assert.Equal("'x'", SqlLiteral.Format('x', kind));

    [Fact]
    public void Quote_DoublesEveryQuote()
    {
        Assert.Equal("'a''b'", SqlLiteral.Quote("a'b"));
        Assert.Equal("''", SqlLiteral.Quote(""));
        // Две кавычки подряд превращаются в четыре — плюс обрамляющая пара.
        Assert.Equal("''''''", SqlLiteral.Quote("''"));
    }

    // ---------------------------------------------------------------- Числа

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void Integers_AreWrittenWithoutQuotes(DbKind kind)
    {
        Assert.Equal("42", SqlLiteral.Format(42, kind));
        Assert.Equal("-7", SqlLiteral.Format(-7, kind));
        Assert.Equal("9223372036854775807", SqlLiteral.Format(long.MaxValue, kind));
        Assert.Equal("255", SqlLiteral.Format((byte)255, kind));
        Assert.Equal("-32768", SqlLiteral.Format(short.MinValue, kind));
    }

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void Reals_UseDotAsDecimalSeparator(DbKind kind)
    {
        Assert.Equal("1.5", SqlLiteral.Format(1.5m, kind));
        Assert.Equal("-0.25", SqlLiteral.Format(-0.25m, kind));
        Assert.Equal("1.25", SqlLiteral.Format(1.25d, kind));
        Assert.Equal("0.5", SqlLiteral.Format(0.5f, kind));
    }

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void Numbers_AreCultureInvariant(DbKind kind) => WithCulture("ru-RU", () =>
    {
        // Проверка осмысленна только если культура действительно с запятой.
        Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

        Assert.Equal("1234.5", SqlLiteral.Format(1234.5m, kind));
        Assert.Equal("1234.5", SqlLiteral.Format(1234.5d, kind));
        Assert.Equal("1234.5", SqlLiteral.Format(1234.5f, kind));
        // Разделитель групп разрядов не должен появиться.
        Assert.Equal("1234567", SqlLiteral.Format(1234567, kind));
        Assert.DoesNotContain(",", SqlLiteral.Format(1234.5d, kind));
    });

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void DateTimeAndInterval_AreCultureInvariant(DbKind kind) => WithCulture("ru-RU", () =>
    {
        var dt = SqlLiteral.Format(new DateTime(2024, 1, 15, 12, 34, 56, 789), kind);
        Assert.Contains("2024-01-15 12:34:56.789000", dt);

        var date = SqlLiteral.Format(new DateOnly(2024, 1, 15), kind);
        Assert.Contains("2024-01-15", date);

        var interval = SqlLiteral.Format(new TimeSpan(1, 2, 3, 4, 500), kind);
        Assert.Contains("1 02:03:04.500000", interval);
    });

    // ---------------------------------------------------------------- Специальные значения плавающей точки

    [Fact]
    public void Double_SpecialValues_Postgres()
    {
        Assert.Equal("'NaN'::double precision", SqlLiteral.Format(double.NaN, DbKind.Postgres));
        Assert.Equal("'Infinity'::double precision", SqlLiteral.Format(double.PositiveInfinity, DbKind.Postgres));
        Assert.Equal("'-Infinity'::double precision", SqlLiteral.Format(double.NegativeInfinity, DbKind.Postgres));
    }

    [Fact]
    public void Float_SpecialValues_Postgres()
    {
        Assert.Equal("'NaN'::real", SqlLiteral.Format(float.NaN, DbKind.Postgres));
        Assert.Equal("'Infinity'::real", SqlLiteral.Format(float.PositiveInfinity, DbKind.Postgres));
        Assert.Equal("'-Infinity'::real", SqlLiteral.Format(float.NegativeInfinity, DbKind.Postgres));
    }

    [Fact]
    public void Double_SpecialValues_Oracle()
    {
        Assert.Equal("BINARY_DOUBLE_NAN", SqlLiteral.Format(double.NaN, DbKind.Oracle));
        Assert.Equal("BINARY_DOUBLE_INFINITY", SqlLiteral.Format(double.PositiveInfinity, DbKind.Oracle));
        Assert.Equal("-BINARY_DOUBLE_INFINITY", SqlLiteral.Format(double.NegativeInfinity, DbKind.Oracle));
    }

    [Fact]
    public void Float_SpecialValues_Oracle()
    {
        Assert.Equal("BINARY_FLOAT_NAN", SqlLiteral.Format(float.NaN, DbKind.Oracle));
        Assert.Equal("BINARY_FLOAT_INFINITY", SqlLiteral.Format(float.PositiveInfinity, DbKind.Oracle));
        Assert.Equal("-BINARY_FLOAT_INFINITY", SqlLiteral.Format(float.NegativeInfinity, DbKind.Oracle));
    }

    // ---------------------------------------------------------------- Логический тип

    [Fact]
    public void Bool_Postgres_UsesKeywords()
    {
        Assert.Equal("true", SqlLiteral.Format(true, DbKind.Postgres));
        Assert.Equal("false", SqlLiteral.Format(false, DbKind.Postgres));
    }

    [Fact]
    public void Bool_Oracle_UsesDigits()
    {
        Assert.Equal("1", SqlLiteral.Format(true, DbKind.Oracle));
        Assert.Equal("0", SqlLiteral.Format(false, DbKind.Oracle));
    }

    // ---------------------------------------------------------------- Дата и время

    [Fact]
    public void DateTime_Postgres()
        => Assert.Equal("TIMESTAMP '2024-01-15 12:34:56.789000'",
            SqlLiteral.Format(new DateTime(2024, 1, 15, 12, 34, 56, 789), DbKind.Postgres));

    [Fact]
    public void DateTime_Oracle()
        => Assert.Equal("TO_TIMESTAMP('2024-01-15 12:34:56.789000','YYYY-MM-DD HH24:MI:SS.FF6')",
            SqlLiteral.Format(new DateTime(2024, 1, 15, 12, 34, 56, 789), DbKind.Oracle));

    [Fact]
    public void DateTimeOffset_Postgres()
    {
        var literal = SqlLiteral.Format(
            new DateTimeOffset(2024, 1, 15, 12, 34, 56, 789, TimeSpan.FromHours(3)), DbKind.Postgres);

        Assert.StartsWith("TIMESTAMP WITH TIME ZONE '", literal);
        Assert.Contains("2024-01-15 12:34:56.789000 +03:00", literal);
    }

    [Fact]
    public void DateTimeOffset_Oracle()
    {
        var literal = SqlLiteral.Format(
            new DateTimeOffset(2024, 1, 15, 12, 34, 56, 789, TimeSpan.FromHours(3)), DbKind.Oracle);

        Assert.StartsWith("TO_TIMESTAMP_TZ('", literal);
        Assert.Contains("2024-01-15 12:34:56.789000 +03:00", literal);
        Assert.Contains("TZH:TZM", literal);
    }

    [Fact]
    public void DateOnly_Postgres()
        => Assert.Equal("DATE '2024-01-15'", SqlLiteral.Format(new DateOnly(2024, 1, 15), DbKind.Postgres));

    [Fact]
    public void DateOnly_Oracle()
        => Assert.Equal("TO_DATE('2024-01-15','YYYY-MM-DD')",
            SqlLiteral.Format(new DateOnly(2024, 1, 15), DbKind.Oracle));

    [Fact]
    public void TimeOnly_Postgres()
        => Assert.Equal("TIME '12:34:56.789000'",
            SqlLiteral.Format(new TimeOnly(12, 34, 56, 789), DbKind.Postgres));

    [Fact]
    public void TimeOnly_Oracle_IsPlainString()
        => Assert.Equal("'12:34:56.789000'",
            SqlLiteral.Format(new TimeOnly(12, 34, 56, 789), DbKind.Oracle));

    [Fact]
    public void TimeSpan_Postgres()
        => Assert.Equal("INTERVAL '1 02:03:04.500000'",
            SqlLiteral.Format(new TimeSpan(1, 2, 3, 4, 500), DbKind.Postgres));

    [Fact]
    public void TimeSpan_Oracle()
        => Assert.Equal("INTERVAL '1 02:03:04.500000' DAY TO SECOND(6)",
            SqlLiteral.Format(new TimeSpan(1, 2, 3, 4, 500), DbKind.Oracle));

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void TimeSpan_Negative_HasLeadingMinus(DbKind kind)
    {
        var literal = SqlLiteral.Format(new TimeSpan(1, 2, 3, 4, 500).Negate(), kind);

        Assert.StartsWith("-INTERVAL '", literal);
        // Знак вынесен наружу: внутри литерала минуса быть не должно.
        Assert.Contains("1 02:03:04.500000", literal);
        Assert.Equal("-" + SqlLiteral.Format(new TimeSpan(1, 2, 3, 4, 500), kind), literal);
    }

    // ---------------------------------------------------------------- Guid

    [Fact]
    public void Guid_Postgres_HasUuidCast()
    {
        var g = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("'0f8fad5b-d9cb-469f-a165-70867728950e'::uuid", SqlLiteral.Format(g, DbKind.Postgres));
    }

    [Fact]
    public void Guid_Oracle_IsPlainString()
    {
        var g = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("'0f8fad5b-d9cb-469f-a165-70867728950e'", SqlLiteral.Format(g, DbKind.Oracle));
    }

    // ---------------------------------------------------------------- Двоичные данные

    [Fact]
    public void Bytes_Postgres_IsByteaHex()
    {
        var literal = SqlLiteral.Format(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, DbKind.Postgres);

        Assert.Equal(@"'\xDEADBEEF'::bytea", literal);
        // Регистр hex — верхний.
        Assert.DoesNotContain("deadbeef", literal);
    }

    [Fact]
    public void Bytes_Oracle_IsHexToRaw()
    {
        var literal = SqlLiteral.Format(new byte[] { 0x00, 0x0F, 0xAB }, DbKind.Oracle);

        Assert.Equal("HEXTORAW('000FAB')", literal);
        Assert.DoesNotContain("ab'", literal);
    }

    [Fact]
    public void Bytes_Empty_IsEmptyLiteral()
    {
        Assert.Equal(@"'\x'::bytea", SqlLiteral.Format(Array.Empty<byte>(), DbKind.Postgres));
        Assert.Equal("HEXTORAW('')", SqlLiteral.Format(Array.Empty<byte>(), DbKind.Oracle));
    }

    [Fact]
    public void Bytes_AtOracleLimit_StillFits()
    {
        var bytes = new byte[SqlLiteral.OracleRawLiteralLimit];

        Assert.StartsWith("HEXTORAW('", SqlLiteral.Format(bytes, DbKind.Oracle));
        Assert.False(SqlLiteral.ExceedsOracleRawLimit(bytes, DbKind.Oracle));
    }

    [Fact]
    public void Bytes_OverOracleLimit_BecomeNull()
    {
        var bytes = new byte[SqlLiteral.OracleRawLiteralLimit + 1];

        Assert.Equal("NULL", SqlLiteral.Format(bytes, DbKind.Oracle));
        Assert.True(SqlLiteral.ExceedsOracleRawLimit(bytes, DbKind.Oracle));

        // Для PostgreSQL предела нет: значение остаётся в скрипте.
        Assert.False(SqlLiteral.ExceedsOracleRawLimit(bytes, DbKind.Postgres));
        Assert.StartsWith(@"'\x", SqlLiteral.Format(bytes, DbKind.Postgres));
    }

    [Fact]
    public void ExceedsOracleRawLimit_IgnoresNonBinaryValues()
    {
        Assert.False(SqlLiteral.ExceedsOracleRawLimit(null, DbKind.Oracle));
        Assert.False(SqlLiteral.ExceedsOracleRawLimit(new string('x', 100_000), DbKind.Oracle));
        Assert.False(SqlLiteral.ExceedsOracleRawLimit(DBNull.Value, DbKind.Oracle));
    }

    // ---------------------------------------------------------------- Прочие типы

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void UnknownType_FallsBackToStringLiteral(DbKind kind)
        => Assert.Equal("'Postgres'", SqlLiteral.Format(DbKind.Postgres, kind));
}
