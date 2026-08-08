using WebDbViewer.Core;
using WebDbViewer.Metadata;

namespace WebDbViewer.Web.Services;

/// <summary>
/// Реализация <see cref="IMetadataLoader"/> для кэша метаданных:
/// открывает собственное короткоживущее соединение через провайдер СУБД и загружает снапшот схемы.
/// </summary>
public sealed class DbMetadataLoader : IMetadataLoader
{
    private readonly IDataSourceStore _store;
    private readonly IDbProviderRegistry _providers;
    private readonly ISecretProtector _protector;

    public DbMetadataLoader(IDataSourceStore store, IDbProviderRegistry providers, ISecretProtector protector)
    {
        _store = store;
        _providers = providers;
        _protector = protector;
    }

    public async Task<SchemaSnapshot> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct)
    {
        var config = await _store.GetAsync(dataSourceId, ct)
                     ?? throw new InvalidOperationException($"Датасорс {dataSourceId} не найден.");

        var provider = _providers.Get(config.Kind);
        var password = config.ProtectedPassword is { Length: > 0 } p ? _protector.Unprotect(p) : "";

        await using var connection = await provider.OpenConnectionAsync(config, password, ct);
        return await provider.LoadSchemaSnapshotAsync(connection, schema, ct);
    }
}
