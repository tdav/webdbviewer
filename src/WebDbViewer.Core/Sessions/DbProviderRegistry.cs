namespace WebDbViewer.Core.Sessions;

/// <summary>Реестр провайдеров СУБД: разрешает <see cref="IDbProvider"/> по <see cref="DbKind"/>.</summary>
public sealed class DbProviderRegistry : IDbProviderRegistry
{
    private readonly Dictionary<DbKind, IDbProvider> _providers;

    public DbProviderRegistry(IEnumerable<IDbProvider> providers)
    {
        _providers = new Dictionary<DbKind, IDbProvider>();
        // При дублировании регистрации побеждает последняя (стандартное поведение DI).
        foreach (var provider in providers)
            _providers[provider.Kind] = provider;
    }

    public IDbProvider Get(DbKind kind)
        => _providers.TryGetValue(kind, out var provider)
            ? provider
            : throw new NotSupportedException($"Провайдер для СУБД «{kind}» не зарегистрирован. Добавьте Add{kind}Provider() в конфигурацию служб.");
}
