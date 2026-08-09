using System.Data.Common;

namespace WebDbViewer.Core.Sessions;

/// <summary>
/// Реализация <see cref="IDbConnectionFactory"/>: расшифровывает пароль датасорса и открывает
/// новое соединение через провайдер СУБД. Пул соединений держит сам провайдер (Npgsql, Oracle),
/// поэтому короткоживущие соединения дешевле, чем конкуренция за единственное соединение сессии.
/// </summary>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly IDataSourceStore store;
    private readonly IDbProviderRegistry providers;
    private readonly ISecretProtector? secretProtector;

    public DbConnectionFactory(IDataSourceStore store, IDbProviderRegistry providers, ISecretProtector? secretProtector)
    {
        this.store = store;
        this.providers = providers;
        this.secretProtector = secretProtector;
    }

    public async Task<DbConnection> OpenAsync(Guid dataSourceId, string? database, CancellationToken ct)
    {
        var config = await store.GetAsync(dataSourceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Датасорс {dataSourceId} не найден.");
        return await OpenAsync(config, database, ct).ConfigureAwait(false);
    }

    public async Task<DbConnection> OpenAsync(DataSourceConfig config, string? database, CancellationToken ct)
    {
        var effectiveDatabase = string.IsNullOrWhiteSpace(database) ? config.Database : database;
        var provider = providers.Get(config.Kind);
        return await provider
            .OpenConnectionAsync(config with { Database = effectiveDatabase }, ResolvePassword(config), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Пароль хранится зашифрованным; без ISecretProtector допустимы только датасорсы без пароля.</summary>
    private string ResolvePassword(DataSourceConfig config)
    {
        if (string.IsNullOrEmpty(config.ProtectedPassword))
            return string.Empty;
        if (secretProtector is null)
            throw new InvalidOperationException(
                "Пароль датасорса зашифрован, но ISecretProtector не зарегистрирован. Настройте Data Protection.");
        return secretProtector.Unprotect(config.ProtectedPassword);
    }
}
