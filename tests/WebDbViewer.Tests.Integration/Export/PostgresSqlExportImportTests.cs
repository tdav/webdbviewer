using Npgsql;
using WebDbViewer.Core;
using WebDbViewer.Core.Export;
using WebDbViewer.Parsing;
using WebDbViewer.Providers.Postgres;

namespace WebDbViewer.Tests.Integration.Export;

/// <summary>
/// Round-trip экспорта и импорта на живом PostgreSQL: таблица выгружается в SQL-скрипт
/// (структура из <see cref="PgDdlGenerator"/> плюс INSERT'ы из <see cref="InsertScriptWriter"/>),
/// затем удаляется и восстанавливается выполнением этого скрипта. Проверяется, что данные
/// вернулись в точности, а не «скрипт получился непустым».
///
/// Строка подключения переопределяется через WEBDBVIEWER_TEST_DEMO_DB. Если сервер недоступен —
/// тесты пропускаются. Каждый запуск работает в собственной схеме, которая удаляется по завершении.
/// </summary>
public sealed class PostgresSqlExportImportTests : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=webdbviewer_demo;Username=postgres;Password=1;Pooling=true;";

    private const string TableName = "export_probe";

    private readonly string connectionString =
        Environment.GetEnvironmentVariable("WEBDBVIEWER_TEST_DEMO_DB") ?? DefaultConnectionString;

    private readonly string schema = "wdv_exp_" + Guid.NewGuid().ToString("N")[..12];

    private readonly PgDdlGenerator generator = new();
    private readonly PostgresProvider provider = new();
    private readonly StatementSplitter splitter = new();

    private NpgsqlConnection? connection;
    private bool available;

    public async Task InitializeAsync()
    {
        try
        {
            connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            available = true;
        }
        catch (Exception)
        {
            available = false;
            return;
        }

        await ExecuteAsync($"CREATE SCHEMA \"{schema}\"");

        // Типы подобраны так, чтобы задеть каждую ветку SqlLiteral.Format.
        await ExecuteAsync($"""
            CREATE TABLE "{schema}"."{TableName}" (
                id        integer PRIMARY KEY,
                name      text NOT NULL,
                amount    numeric(18,2),
                ratio     double precision,
                flag      boolean,
                created   timestamp,
                shift     interval,
                payload   bytea,
                uid       uuid
            )
            """);

        await ExecuteAsync($"""
            INSERT INTO "{schema}"."{TableName}" VALUES
                (1, 'обычная строка', 10.50, 0.125, true,
                 TIMESTAMP '2024-01-15 10:30:00.123456', INTERVAL '1 02:03:04',
                 '\x0a0b0c'::bytea, 'd0b1e2c3-0000-4000-8000-000000000001'::uuid),
                (2, 'с ''кавычкой'' и переводом
            строки', -0.01, 'NaN'::double precision, false,
                 NULL, INTERVAL '-2 00:00:01', NULL, NULL),
                (3, '', NULL, 'Infinity'::double precision, NULL,
                 TIMESTAMP '1999-12-31 23:59:59.999999', NULL, '\x'::bytea, NULL)
            """);
    }

    public async Task DisposeAsync()
    {
        if (connection is null)
            return;

        if (available)
        {
            try
            {
                await ExecuteAsync($"DROP SCHEMA \"{schema}\" CASCADE");
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
        Skip.IfNot(available);

        var before = await ReadSnapshotAsync();
        Assert.Equal(3, before.Count);

        var script = await BuildScriptAsync(long.MaxValue);

        // Таблицы больше нет — всё, что нужно для её восстановления, должно быть в скрипте.
        await ExecuteAsync($"DROP TABLE \"{schema}\".\"{TableName}\"");
        await RunScriptAsync(script);

        var after = await ReadSnapshotAsync();
        Assert.Equal(before, after);
    }

    [SkippableFact]
    public async Task Скрипт_содержит_структуру_и_по_одному_INSERT_на_строку()
    {
        Skip.IfNot(available);

        var script = await BuildScriptAsync(long.MaxValue);

        Assert.Contains("CREATE TABLE", script, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY", script, StringComparison.Ordinal);
        Assert.Equal(3, CountInserts(script));

        // Значения ушли литералами, а не параметрами.
        Assert.Contains("'обычная строка'", script, StringComparison.Ordinal);
        Assert.Contains("'с ''кавычкой''", script, StringComparison.Ordinal);
        Assert.Contains("NULL", script, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Восстановленная_таблица_сохраняет_первичный_ключ()
    {
        Skip.IfNot(available);

        var script = await BuildScriptAsync(long.MaxValue);
        await ExecuteAsync($"DROP TABLE \"{schema}\".\"{TableName}\"");
        await RunScriptAsync(script);

        await using var command = connection!.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = @schema AND t.relname = @table AND c.contype = 'p'
            """;
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", TableName);

        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [SkippableFact]
    public async Task Предел_строк_обрывает_выгрузку_и_отмечается_в_результате()
    {
        Skip.IfNot(available);

        var writer = new StringWriter();
        var result = await WriteDataAsync(writer, maxRows: 2);

        Assert.Equal(2, result.RowCount);
        Assert.True(result.Truncated);
        Assert.Equal(2, CountInserts(writer.ToString()));
    }

    // ---------------------------------------------------------------- Вспомогательное

    /// <summary>Полный скрипт: DDL таблицы плюс INSERT'ы. PostgreSQL отдаёт DDL уже с «;».</summary>
    private async Task<string> BuildScriptAsync(long maxRows)
    {
        var writer = new StringWriter();
        var ddl = await generator.GetTableDdlAsync(connection!, schema, TableName, CancellationToken.None);
        writer.WriteLine(ddl.TrimEnd());
        writer.WriteLine();

        await WriteDataAsync(writer, maxRows);
        return writer.ToString();
    }

    private async Task<InsertScriptResult> WriteDataAsync(TextWriter writer, long maxRows)
    {
        var target = Target();
        await using var command = connection!.CreateCommand();
        command.CommandText = $"SELECT * FROM {target} ORDER BY \"id\"";

        await using var reader = await command.ExecuteReaderAsync();
        return await InsertScriptWriter.WriteAsync(
            reader, target, writer, DbKind.Postgres, provider.QuoteIdentifier, maxRows, CancellationToken.None);
    }

    /// <summary>Выполняет скрипт так же, как это делает импорт: по statements из общего сплиттера.</summary>
    private async Task RunScriptAsync(string script)
    {
        foreach (var statement in splitter.Split(script, DbKind.Postgres))
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
        command.CommandText = $"SELECT * FROM {Target()} ORDER BY \"id\"";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = SqlLiteral.Format(reader.IsDBNull(i) ? null : reader.GetValue(i), DbKind.Postgres);
            rows.Add(string.Join('|', values));
        }

        return rows;
    }

    private string Target() =>
        $"{provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(TableName)}";

    private static int CountInserts(string script) =>
        script.Split('\n').Count(line => line.StartsWith("INSERT INTO", StringComparison.Ordinal));

    private async Task ExecuteAsync(string sql)
    {
        await using var command = connection!.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
