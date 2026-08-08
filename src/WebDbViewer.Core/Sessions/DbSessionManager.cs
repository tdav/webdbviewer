using System.Collections.Concurrent;
using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebDbViewer.Core.Sessions;

/// <summary>
/// Менеджер stateful-сессий БД: переиспользование по (пользователь, датасорс),
/// лимит сессий на пользователя, TTL-очистка, гарантированное освобождение.
/// </summary>
public sealed class DbSessionManager : IDbSessionManager, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, DbSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userGates = new(StringComparer.Ordinal);
    private readonly IDataSourceStore _dataSourceStore;
    private readonly IDbProviderRegistry _providerRegistry;
    private readonly ISecretProtector? _secretProtector;
    private readonly DbSessionOptions _options;
    private readonly ILogger<DbSessionManager>? _logger;

    public DbSessionManager(
        IDataSourceStore dataSourceStore,
        IDbProviderRegistry providerRegistry,
        ISecretProtector? secretProtector,
        IOptions<DbSessionOptions> options,
        ILogger<DbSessionManager>? logger = null)
    {
        _dataSourceStore = dataSourceStore;
        _providerRegistry = providerRegistry;
        _secretProtector = secretProtector;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Число живых сессий (для диагностики).</summary>
    public int ActiveSessionCount => _sessions.Count;

    public async Task<IDbSession> GetOrCreateAsync(string userName, Guid dataSourceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);

        // Сериализуем создание сессий по пользователю, чтобы корректно проверять лимит.
        var gate = _userGates.GetOrAdd(userName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Переиспользуем живую сессию этого пользователя с тем же датасорсом.
            foreach (var s in _sessions.Values)
            {
                if (!string.Equals(s.UserName, userName, StringComparison.Ordinal) || s.DataSourceId != dataSourceId)
                    continue;
                if (s.IsDisposed || s.Connection.State != ConnectionState.Open)
                {
                    await RemoveAndDisposeAsync(s.SessionId).ConfigureAwait(false);
                    continue;
                }
                s.Touch();
                return s;
            }

            var userCount = _sessions.Values.Count(s => string.Equals(s.UserName, userName, StringComparison.Ordinal));
            if (userCount >= _options.MaxSessionsPerUser)
                throw new InvalidOperationException(
                    $"Превышен лимит сессий на пользователя ({_options.MaxSessionsPerUser}). Закройте одну из открытых сессий.");

            var config = await _dataSourceStore.GetAsync(dataSourceId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Датасорс {dataSourceId} не найден.");

            var password = ResolvePassword(config);
            var provider = _providerRegistry.Get(config.Kind);
            var connection = await provider.OpenConnectionAsync(config, password, ct).ConfigureAwait(false);

            var session = new DbSession(dataSourceId, userName, connection);
            _sessions[session.SessionId] = session;
            _logger?.LogInformation("Открыта сессия {SessionId} пользователя {UserName} к датасорсу {DataSourceId}.",
                session.SessionId, userName, dataSourceId);
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<IDbSession?> FindAsync(Guid sessionId, CancellationToken ct)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && !session.IsDisposed)
        {
            session.Touch();
            return Task.FromResult<IDbSession?>(session);
        }
        return Task.FromResult<IDbSession?>(null);
    }

    public async Task CloseAsync(Guid sessionId, CancellationToken ct)
    {
        await RemoveAndDisposeAsync(sessionId).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<IDbSession>> ListForUserAsync(string userName, CancellationToken ct)
    {
        IReadOnlyList<IDbSession> result = _sessions.Values
            .Where(s => string.Equals(s.UserName, userName, StringComparison.Ordinal) && !s.IsDisposed)
            .OrderBy(s => s.LastUsedAt)
            .Cast<IDbSession>()
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>Закрывает сессии, простаивающие дольше TTL. Возвращает число закрытых.</summary>
    public async Task<int> SweepExpiredAsync(CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow - _options.IdleTtl;
        var closed = 0;
        foreach (var session in _sessions.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (session.LastUsedAt <= deadline || session.IsDisposed || session.Connection.State == ConnectionState.Broken)
            {
                if (await RemoveAndDisposeAsync(session.SessionId).ConfigureAwait(false))
                {
                    closed++;
                    _logger?.LogInformation("Сессия {SessionId} закрыта по TTL (последнее использование: {LastUsedAt:O}).",
                        session.SessionId, session.LastUsedAt);
                }
            }
        }
        return closed;
    }

    private async Task<bool> RemoveAndDisposeAsync(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return false;
        await session.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private string ResolvePassword(DataSourceConfig config)
    {
        if (string.IsNullOrEmpty(config.ProtectedPassword))
            return string.Empty;
        if (_secretProtector is null)
            throw new InvalidOperationException(
                "Пароль датасорса зашифрован, но ISecretProtector не зарегистрирован. Настройте Data Protection.");
        return _secretProtector.Unprotect(config.ProtectedPassword);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
            await RemoveAndDisposeAsync(id).ConfigureAwait(false);
        foreach (var gate in _userGates.Values)
            gate.Dispose();
        _userGates.Clear();
    }
}
