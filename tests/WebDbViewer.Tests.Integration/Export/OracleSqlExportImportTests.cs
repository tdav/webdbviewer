using Oracle.ManagedDataAccess.Client;
using WebDbViewer.Core;
using WebDbViewer.Core.Export;
using WebDbViewer.Parsing;
using WebDbViewer.Providers.Oracle;

namespace WebDbViewer.Tests.Integration.Export;

/// <summary>
/// Round-trip экспорта и импорта на живом Oracle: таблица выгружается в SQL-скрипт
/// (структура из <see cref="OracleDdlGenerator"/> плюс INSERT'ы из <see cref="InsertScriptWriter"/>),
/// затем удаляется и восстанавливается выполнением этого скрипта.
///
/// Подключение — <see cref="OracleTestDatabase"/> (WEBDBVIEWER_TEST_ORACLE). Сервера может не быть:
/// тогда тесты пропускаются. Таблица создаётся в схеме подключившегося пользователя с уникальным
/// именем и удаляется по завершении — отдельной схемы Oracle, в отличие от PostgreSQL, не даёт.
/// </summary>
public sealed class OracleSqlExportImportTests : IAsyncLifetime
{
    private readonly string tableName = "WDV_EXP_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private readonly OracleDdlGenerator generator = new();
    private readonly OracleProvider provider = new();
    private readonly StatementSplitter splitter = new();

    private OracleConnection? connection;
    private string? schema;
    private bool available;

    public async Task InitializeAsync()
    {
        connection = await OracleTestDatabase.TryOpenAsync(CancellationToken.None);
        if (connection is null)
            return;

        try
        {
            schema = (await ScalarAsync("SELECT USER FROM dual"))?.ToString();

            // Типы подобраны так, чтобы задеть каждую ветку SqlLiteral.Format для Oracle.
            await ExecuteAsync($"""
                CREATE TABLE "{tableName}" (
                    ID        NUMBER(10) PRIMARY KEY,
                    NAME      VARCHAR2(200) NOT NULL,
                    AMOUNT    NUMBER(18,2),
                    RATIO     BINARY_DOUBLE,
                    CREATED   DATE,
                    STAMP     TIMESTAMP(6),
                    SHIFT     INTERVAL DAY TO SECOND(6),
                    PAYLOAD   RAW(100)
                )
                """);

            await ExecuteAsync($"""
                INSERT INTO "{tableName}" VALUES (
                    1, 'обычная строка', 10.50, 0.125,
                    TO_DATE('2024-01-15 10:30:00','YYYY-MM-DD HH24:MI:SS'),
                    TO_TIMESTAMP('2024-01-15 10:30:00.123456','YYYY-MM-DD HH24:MI:SS.FF6'),
                    INTERVAL '1 02:03:04' DAY TO SECOND, HEXTORAW('0A0B0C'))
                """);
            await ExecuteAsync($"""
                INSERT INTO "{tableName}" VALUES (
                    2, 'с ''кавычкой''', -0.01, BINARY_DOUBLE_INFINITY,
                    NULL, NULL, INTERVAL '-2 00:00:01' DAY TO SECOND, NULL)
                """);
            await ExecuteAsync($"""
                INSERT INTO "{tableName}" VALUES (
                    3, 'третья', NULL, BINARY_DOUBLE_NAN,
                    TO_DATE('1999-12-31 23:59:59','YYYY-MM-DD HH24:MI:SS'),
                    TO_TIMESTAMP('1999-12-31 23:59:59.999999','YYYY-MM-DD HH24:MI:SS.FF6'),
                    NULL, HEXTORAW('FF'))
                """);

            available = true;
        }
        catch (OracleException)
        {
            // Нет прав на создание таблицы или на DBMS_METADATA — для теста это тот же пропуск.
            available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (connection is null)
            return;

        if (available)
        {
            try
            {
                await ExecuteAsync($"DROP TABLE \"{tableName}\" PURGE");
            }
            catch (Exception)
            {
                // Уборка не должна маскировать причину падения теста.
            }
        }

        await connection.DisposeAsync();
    }

    [SkippableFact]
    public async Task Экспорт_и_импорт_возвращают_данные_без_потерь()
    {
        Skip.IfNot(available, $"Oracle недоступен: {OracleTestDatabase.ConnectionString}");

        var before = await ReadSnapshotAsync();
        Assert.Equal(3, before.Count);

        var script = await BuildScriptAsync(long.MaxValue);

        await ExecuteAsync($"DROP TABLE \"{tableName}\" PURGE");
        await RunScriptAsync(script);

        var after = await ReadSnapshotAsync();
        Assert.Equal(before, after);
    }

    [SkippableFact]
    public async Task Структура_завершена_точкой_с_запятой_и_отделена_от_данных()
    {
        Skip.IfNot(available, $"Oracle недоступен: {OracleTestDatabase.ConnectionString}");

        var script = await BuildScriptAsync(long.MaxValue);

        Assert.Contains("CREATE TABLE", script, StringComparison.Ordinal);
        Assert.Equal(3, CountInserts(script));

        // DBMS_METADATA отдаёт DDL без терминатора: без дописанной «;» первый INSERT
        // прилипнет к CREATE TABLE и скрипт перестанет выполняться.
        var statements = splitter.Split(script, DbKind.Oracle);
        Assert.Equal(4, statements.Count);
        Assert.Contains("CREATE TABLE", statements[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO", statements[0].Text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Литералы_записаны_в_синтаксисе_Oracle()
    {
        Skip.IfNot(available, $"Oracle недоступен: {OracleTestDatabase.ConnectionString}");

        var script = await BuildScriptAsync(long.MaxValue);

        Assert.Contains("TO_TIMESTAMP(", script, StringComparison.Ordinal);
        Assert.Contains("HEXTORAW('0A0B0C')", script, StringComparison.Ordinal);
        Assert.Contains("DAY TO SECOND", script, StringComparison.Ordinal);
        Assert.Contains("'с ''кавычкой'''", script, StringComparison.Ordinal);

        // Синтаксис PostgreSQL в Oracle-скрипт попасть не должен.
        Assert.DoesNotContain("::bytea", script, StringComparison.Ordinal);
        Assert.DoesNotContain("::uuid", script, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Предел_строк_обрывает_выгрузку_и_отмечается_в_результате()
    {
        Skip.IfNot(available, $"Oracle недоступен: {OracleTestDatabase.ConnectionString}");

        var writer = new StringWriter();
        var result = await WriteDataAsync(writer, maxRows: 2);

        Assert.Equal(2, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Equal(2, CountInserts(writer.ToString()));
    }

    // ---------------------------------------------------------------- Вспомогательное

    /// <summary>
    /// Полный скрипт: DDL таблицы плюс INSERT'ы. DBMS_METADATA возвращает DDL без «;»,
    /// поэтому терминатор дописывается здесь — так же, как это делает endpoint экспорта.
    /// </summary>
    private async Task<string> BuildScriptAsync(long maxRows)
    {
        var writer = new StringWriter();

        var ddl = (await generator.GetTableDdlAsync(connection!, schema!, tableName, CancellationToken.None)).TrimEnd();
        writer.WriteLine(ddl.EndsWith(';') ? ddl : ddl + ";");
        writer.WriteLine();

        await WriteDataAsync(writer, maxRows);
        return writer.ToString();
    }

    private async Task<InsertScriptResult> WriteDataAsync(TextWriter writer, long maxRows)
    {
        var target = Target();
        await using var command = connection!.CreateCommand();
        command.CommandText = $"SELECT * FROM {target} ORDER BY \"ID\"";

        await using var reader = await command.ExecuteReaderAsync();
        return await InsertScriptWriter.WriteAsync(
            reader, target, writer, DbKind.Oracle, provider.QuoteIdentifier, maxRows, CancellationToken.None);
    }

    /// <summary>Выполняет скрипт так же, как это делает импорт: по statements из общего сплиттера.</summary>
    private async Task RunScriptAsync(string script)
    {
        foreach (var statement in splitter.Split(script, DbKind.Oracle))
            await ExecuteAsync(statement.Text);
    }

    /// <summary>
    /// Снимок содержимого таблицы в текстовом виде. Значения приводятся тем же форматтером,
    /// что и при экспорте: сравниваются именно данные, а не представление конкретного типа.
    /// </summary>
    private async Task<List<string>> ReadSnapshotAsync()
    {
        var rows = new List<string>();

        await using var command = connection!.CreateCommand();
        command.CommandText = $"SELECT * FROM {Target()} ORDER BY \"ID\"";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = SqlLiteral.Format(reader.IsDBNull(i) ? null : reader.GetValue(i), DbKind.Oracle);
            rows.Add(string.Join('|', values));
        }

        return rows;
    }

    private string Target() =>
        $"{provider.QuoteIdentifier(schema!)}.{provider.QuoteIdentifier(tableName)}";

    private static int CountInserts(string script) =>
        script.Split('\n').Count(line => line.StartsWith("INSERT INTO", StringComparison.Ordinal));

    private async Task ExecuteAsync(string sql)
    {
        await using var command = connection!.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var command = connection!.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
