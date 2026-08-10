using System.Collections;
using System.Data.Common;
using WebDbViewer.Core;
using WebDbViewer.Core.Export;

namespace WebDbViewer.Tests.Unit.Export;

/// <summary>Тесты записи INSERT-скрипта поверх курсора.</summary>
public class InsertScriptWriterTests
{
    /// <summary>Квотирование в тестах намеренно тривиальное — чтобы не зависеть от провайдеров.</summary>
    private static readonly Func<string, string> QuoteIdentifier = n => "\"" + n + "\"";

    private const string Target = "\"s\".\"t\"";

    private static async Task<(InsertScriptResult Result, string Text)> WriteAsync(
        string[] columns,
        object?[][] rows,
        DbKind kind = DbKind.Postgres,
        long maxRows = long.MaxValue)
    {
        using var reader = new FakeDataReader(columns, rows);
        var output = new StringWriter();

        var result = await InsertScriptWriter.WriteAsync(
            reader, Target, output, kind, QuoteIdentifier, maxRows, CancellationToken.None);

        return (result, output.ToString());
    }

    private static string[] Lines(string text)
        => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    // ---------------------------------------------------------------- Форма оператора

    [Fact]
    public async Task SingleRow_ProducesExpectedStatement()
    {
        var (result, text) = await WriteAsync(["id", "name"], [[1, "a"]]);

        Assert.Equal("INSERT INTO \"s\".\"t\" (\"id\", \"name\") VALUES (1, 'a');", Assert.Single(Lines(text)));
        Assert.Equal(1, result.RowCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Columns_KeepReaderOrderAndUseQuoteDelegate()
    {
        var (_, text) = await WriteAsync(["Order Id", "select", "Qty"], [[1, 2, 3]]);

        Assert.Equal(
            "INSERT INTO \"s\".\"t\" (\"Order Id\", \"select\", \"Qty\") VALUES (1, 2, 3);",
            Assert.Single(Lines(text)));
    }

    [Fact]
    public async Task MultipleRows_OneStatementPerLine()
    {
        var (result, text) = await WriteAsync(["id", "name"], [[1, "a"], [2, "b'c"], [3, null]]);

        var lines = Lines(text);
        Assert.Equal(3, lines.Length);
        Assert.Equal("INSERT INTO \"s\".\"t\" (\"id\", \"name\") VALUES (1, 'a');", lines[0]);
        Assert.Equal("INSERT INTO \"s\".\"t\" (\"id\", \"name\") VALUES (2, 'b''c');", lines[1]);
        Assert.Equal("INSERT INTO \"s\".\"t\" (\"id\", \"name\") VALUES (3, NULL);", lines[2]);
        Assert.Equal(3, result.RowCount);
        Assert.All(lines, l => Assert.EndsWith(");", l));
    }

    [Fact]
    public async Task NullValue_IsWrittenAsNullKeyword()
    {
        var (_, text) = await WriteAsync(["id", "name"], [[null, null]]);

        Assert.Contains("VALUES (NULL, NULL);", text);
    }

    [Fact]
    public async Task Oracle_UsesOracleLiterals()
    {
        var (_, text) = await WriteAsync(["flag", "ts"],
            [[true, new DateTime(2024, 1, 15, 12, 0, 0)]], DbKind.Oracle);

        Assert.Contains("VALUES (1, TO_TIMESTAMP('2024-01-15 12:00:00.000000'", text);
    }

    // ---------------------------------------------------------------- Счётчик строк и предел

    [Fact]
    public async Task RowCount_MatchesRowsRead()
    {
        var rows = Enumerable.Range(1, 5).Select(i => new object?[] { i }).ToArray();

        var (result, text) = await WriteAsync(["id"], rows);

        Assert.Equal(5, result.RowCount);
        Assert.False(result.Truncated);
        Assert.Equal(5, Lines(text).Length);
    }

    [Fact]
    public async Task MaxRows_BelowRowCount_Truncates()
    {
        var rows = Enumerable.Range(1, 5).Select(i => new object?[] { i }).ToArray();

        var (result, text) = await WriteAsync(["id"], rows, maxRows: 2);

        Assert.Equal(2, result.RowCount);
        Assert.True(result.Truncated);

        var lines = Lines(text);
        Assert.Equal(2, lines.Length);
        Assert.Contains("VALUES (1);", lines[0]);
        Assert.Contains("VALUES (2);", lines[1]);
    }

    [Fact]
    public async Task MaxRows_EqualToRowCount_DoesNotTruncate()
    {
        var rows = Enumerable.Range(1, 3).Select(i => new object?[] { i }).ToArray();

        var (result, text) = await WriteAsync(["id"], rows, maxRows: 3);

        Assert.Equal(3, result.RowCount);
        Assert.False(result.Truncated);
        Assert.Equal(3, Lines(text).Length);
    }

    [Fact]
    public async Task MaxRows_Zero_WritesNothingButReportsTruncation()
    {
        var (result, text) = await WriteAsync(["id"], [[1]], maxRows: 0);

        Assert.Equal(0, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Equal("", text);
    }

    [Fact]
    public async Task NoRows_ProducesEmptyOutput()
    {
        var (result, text) = await WriteAsync(["id"], []);

        Assert.Equal(0, result.RowCount);
        Assert.False(result.Truncated);
        Assert.Equal("", text);
    }

    [Fact]
    public async Task NoColumns_ProducesEmptyOutput()
    {
        var (result, text) = await WriteAsync([], [[], []]);

        Assert.Equal(0, result.RowCount);
        Assert.False(result.Truncated);
        Assert.Equal("", text);
    }

    // ---------------------------------------------------------------- Двоичное значение сверх предела Oracle

    [Fact]
    public async Task Oracle_OversizedBinary_WarnsAndWritesNull()
    {
        var big = new byte[SqlLiteral.OracleRawLiteralLimit + 1];

        var (result, text) = await WriteAsync(["id", "data"], [[7, big]], DbKind.Oracle);

        var lines = Lines(text);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("-- ВНИМАНИЕ", lines[0]);
        Assert.Contains("\"data\"", lines[0]);
        Assert.Contains("2001", lines[0]);
        Assert.Equal("INSERT INTO \"s\".\"t\" (\"id\", \"data\") VALUES (7, NULL);", lines[1]);
        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public async Task Postgres_OversizedBinary_IsWrittenWithoutWarning()
    {
        var big = new byte[SqlLiteral.OracleRawLiteralLimit + 1];

        var (_, text) = await WriteAsync(["id", "data"], [[7, big]]);

        Assert.Single(Lines(text));
        Assert.DoesNotContain("ВНИМАНИЕ", text);
        Assert.Contains("::bytea", text);
    }

    // ---------------------------------------------------------------- Фейковый курсор

    /// <summary>
    /// Минимальный курсор поверх списка строк: реализовано только то, что использует писатель.
    /// </summary>
    private sealed class FakeDataReader(string[] names, object?[][] rows) : DbDataReader
    {
        private int index = -1;

        public override int FieldCount => names.Length;

        public override bool HasRows => rows.Length > 0;

        public override bool IsClosed => false;

        public override int Depth => 0;

        public override int RecordsAffected => -1;

        public override string GetName(int ordinal) => names[ordinal];

        public override object GetValue(int ordinal) => rows[index][ordinal] ?? DBNull.Value;

        public override bool IsDBNull(int ordinal) => rows[index][ordinal] is null or DBNull;

        public override bool Read()
        {
            if (index >= rows.Length)
                return false;

            index++;
            return index < rows.Length;
        }

        // -------- ниже писателем не используется

        public override object this[int ordinal] => throw new NotSupportedException();

        public override object this[string name] => throw new NotSupportedException();

        public override bool GetBoolean(int ordinal) => throw new NotSupportedException();

        public override byte GetByte(int ordinal) => throw new NotSupportedException();

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
            => throw new NotSupportedException();

        public override char GetChar(int ordinal) => throw new NotSupportedException();

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
            => throw new NotSupportedException();

        public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();

        public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

        public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

        public override double GetDouble(int ordinal) => throw new NotSupportedException();

        public override Type GetFieldType(int ordinal) => throw new NotSupportedException();

        public override float GetFloat(int ordinal) => throw new NotSupportedException();

        public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

        public override short GetInt16(int ordinal) => throw new NotSupportedException();

        public override int GetInt32(int ordinal) => throw new NotSupportedException();

        public override long GetInt64(int ordinal) => throw new NotSupportedException();

        public override int GetOrdinal(string name) => throw new NotSupportedException();

        public override string GetString(int ordinal) => throw new NotSupportedException();

        public override int GetValues(object[] values) => throw new NotSupportedException();

        public override bool NextResult() => throw new NotSupportedException();

        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
    }
}
