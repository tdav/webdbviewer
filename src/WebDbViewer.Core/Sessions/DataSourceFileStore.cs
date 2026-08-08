using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebDbViewer.Core.Sessions;

/// <summary>
/// Файловое JSON-хранилище конфигураций датасорсов (метабаза приложения).
/// Потокобезопасно (SemaphoreSlim), атомарная запись через временный файл.
/// Пароли хранятся ТОЛЬКО в защищённом виде (<see cref="DataSourceConfig.ProtectedPassword"/>).
/// </summary>
public sealed class DataSourceFileStore : IDataSourceStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, DataSourceConfig>? _cache;

    public DataSourceFileStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public async Task<IReadOnlyList<DataSourceConfig>> GetAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = await LoadUnsafeAsync(ct).ConfigureAwait(false);
            return cache.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DataSourceConfig?> GetAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = await LoadUnsafeAsync(ct).ConfigureAwait(false);
            return cache.TryGetValue(id, out var config) ? config : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DataSourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = await LoadUnsafeAsync(ct).ConfigureAwait(false);
            cache[config.Id] = config;
            await PersistUnsafeAsync(cache, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = await LoadUnsafeAsync(ct).ConfigureAwait(false);
            if (cache.Remove(id))
                await PersistUnsafeAsync(cache, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<Guid, DataSourceConfig>> LoadUnsafeAsync(CancellationToken ct)
    {
        if (_cache is not null)
            return _cache;

        if (!File.Exists(_filePath))
        {
            _cache = new Dictionary<Guid, DataSourceConfig>();
            return _cache;
        }

        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var list = await JsonSerializer.DeserializeAsync<List<DataSourceConfig>>(stream, JsonOptions, ct)
            .ConfigureAwait(false) ?? [];
        _cache = list.ToDictionary(c => c.Id);
        return _cache;
    }

    private async Task PersistUnsafeAsync(Dictionary<Guid, DataSourceConfig> cache, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Атомарная запись: сериализуем во временный файл и заменяем целевой.
        var tmpPath = _filePath + ".tmp";
        await using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var list = cache.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            await JsonSerializer.SerializeAsync(stream, list, JsonOptions, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    public void Dispose() => _gate.Dispose();
}
