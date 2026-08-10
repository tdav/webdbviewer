using System.Data.Common;
using Microsoft.Extensions.Options;
using Npgsql;
using WebDbViewer.Core;
using WebDbViewer.Metadata;
using WebDbViewer.Providers.Postgres;
using WebDbViewer.Web.Api;

namespace WebDbViewer.Tests.Integration.Completion;

/// <summary>
/// Интеграционный тест связки «провайдер → кэш метаданных → SchemaMapDto» на живой демонстрационной базе.
/// Строка подключения переопределяется через WEBDBVIEWER_TEST_DEMO_DB (см. PostgresTreeIntrospectionTests).
/// HTTP-хост не поднимается: MetadataCache собирается напрямую поверх PostgresProvider на открытом соединении,
/// так как IMetadataLoader Web-слоя (DbMetadataLoader) тянет инфраструктуру датасорсов, которой у теста нет.
/// </summary>
public sealed class SchemaMapIntegrationTests : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=webdbviewer_demo;Username=postgres;Password=1;Pooling=true;";

    private const string SchemaName = "demo_core";
    private const string ProbeTable = "wdb_schema_map_probe";

    private readonly string connectionString =
        Environment.GetEnvironmentVariable("WEBDBVIEWER_TEST_DEMO_DB") ?? DefaultConnectionString;

    private readonly PostgresProvider provider = new();
    private readonly Guid dataSourceId = Guid.NewGuid();
    private NpgsqlConnection? connection;
    private bool available;

    public async Task InitializeAsync()
    {
        try
        {
            connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var probe = new NpgsqlCommand(
                "SELECT count(*) FROM pg_namespace WHERE nspname = 'demo_core'", connection);
            available = Convert.ToInt64(await probe.ExecuteScalarAsync()) == 1;
        }
        catch (Exception)
        {
            available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync();
    }

    private DbConnection Db => connection!;

    /// <summary>Минимальный IMetadataLoader поверх уже открытого соединения теста — без стора датасорсов и секретов Web-слоя.</summary>
    private sealed class DirectLoader(PostgresProvider provider, DbConnection connection) : IMetadataLoader
    {
        public Task<SchemaSnapshot> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct)
            => provider.LoadSchemaSnapshotAsync(connection, schema, ct);
    }

    /// <summary>Заглушка persistent-хранилища: тест проверяет только in-memory кэш, диск ему не нужен.</summary>
    private sealed class NoopSnapshotStore : ISnapshotStore
    {
        public Task SaveAsync(Guid dataSourceId, SchemaSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<PersistedSnapshot>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PersistedSnapshot>>([]);

        public Task<SchemaSnapshot?> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct)
            => Task.FromResult<SchemaSnapshot?>(null);

        public Task DeleteAsync(Guid dataSourceId, string? schema, CancellationToken ct) => Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Новая_колонка_видна_только_после_инвалидации()
    {
        Skip.IfNot(available);

        var ct = CancellationToken.None;
        var cache = new MetadataCache(
            new DirectLoader(provider, Db),
            new NoopSnapshotStore(),
            Options.Create(new MetadataCacheOptions()));

        await using var create = new NpgsqlCommand(
            $"CREATE TABLE {SchemaName}.{ProbeTable} (id bigint primary key, note text)", connection);
        await create.ExecuteNonQueryAsync(ct);

        try
        {
            // 1) Первая загрузка: таблица видна, id — первичный ключ, снапшот не урезан.
            var snapshot1 = await cache.GetSchemaAsync(dataSourceId, SchemaName, ct);
            var dto1 = SchemaMapDto.From(snapshot1);

            Assert.False(dto1.Partial);
            var table1 = Assert.Single(dto1.Tables, t => t.Name == ProbeTable);
            var idColumn = Assert.Single(table1.Columns, c => c.Name == "id");
            Assert.True(idColumn.IsPrimaryKey);

            var etag1 = SchemaMapDto.ETagFor(snapshot1);
            // Находка для отчёта, а не повод ослаблять тест: если ETag null, клиентское
            // кэширование по If-None-Match на PostgreSQL не будет работать.
            Assert.NotNull(etag1);

            // 2) DDL мимо кэша: добавляем колонку напрямую через соединение.
            await using (var alter = new NpgsqlCommand(
                $"ALTER TABLE {SchemaName}.{ProbeTable} ADD COLUMN extra text", connection))
            {
                await alter.ExecuteNonQueryAsync(ct);
            }

            // 3) Повторный запрос в пределах TTL — отдаётся закэшированный снапшот без колонки extra.
            var snapshot2 = await cache.GetSchemaAsync(dataSourceId, SchemaName, ct);
            var table2 = Assert.Single(snapshot2.Tables, t => t.Name == ProbeTable);
            Assert.DoesNotContain(table2.Columns, c => c.Name == "extra");

            // 4) Инвалидация — следующая загрузка идёт заново и видит новую колонку.
            await cache.InvalidateAsync(dataSourceId, SchemaName, ct);

            var snapshot3 = await cache.GetSchemaAsync(dataSourceId, SchemaName, ct);
            var dto3 = SchemaMapDto.From(snapshot3);
            var table3 = Assert.Single(dto3.Tables, t => t.Name == ProbeTable);
            Assert.Contains(table3.Columns, c => c.Name == "extra");

            var etag3 = SchemaMapDto.ETagFor(snapshot3);
            Assert.NotEqual(etag1, etag3);
        }
        finally
        {
            await using var drop = new NpgsqlCommand(
                $"DROP TABLE IF EXISTS {SchemaName}.{ProbeTable}", connection);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
