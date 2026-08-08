using System.Collections.Concurrent;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Api;

/// <summary>
/// Описание одного запущенного выполнения SQL: набор statements, сессия БД,
/// токен отмены и состояние (ожидает стрима / стримится / завершено).
/// </summary>
public sealed class RunningQuery : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    // Состояние: 0 — создан (ждёт подключения SSE-клиента), 1 — стримится, 2 — завершён.
    private int _state;

    public Guid ExecutionId { get; } = Guid.NewGuid();
    public required Guid DataSourceId { get; init; }
    public required string UserName { get; init; }
    /// <summary>Полный текст, выбранный к выполнению (для аудита).</summary>
    public required string SqlText { get; init; }
    /// <summary>Statements к выполнению (уже отфильтрованы: под курсором или весь скрипт).</summary>
    public required IReadOnlyList<SqlStatement> Statements { get; init; }
    public required IDbSession Session { get; init; }
    /// <summary>Лимит строк на выполнение; при превышении — Truncated=true.</summary>
    public int MaxRows { get; init; } = 10_000;
    public int CommandTimeoutSeconds { get; init; } = 120;
    public string? ClientIp { get; init; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Токен отмены выполнения (эндпоинт cancel).</summary>
    public CancellationToken Cancellation => _cts.Token;

    public bool IsFinished => Volatile.Read(ref _state) == 2;

    /// <summary>
    /// Помечает выполнение как «стримится». Возвращает false, если стрим уже был начат
    /// (защита от двойного подключения к одному executionId).
    /// </summary>
    public bool TryBeginStreaming() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    public void MarkFinished() => Volatile.Write(ref _state, 2);

    /// <summary>Отмена: взводит токен и отменяет текущую команду в сессии БД.</summary>
    public void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* уже завершено */ }
        try { Session.CancelRunning(); }
        catch { /* best effort */ }
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>
/// Реестр выполняющихся запросов: выдаёт executionId для SSE-стрима и отмены.
/// Регистрируется в DI методом <see cref="ResultStreamingExtensions.AddResultStreaming"/>.
/// </summary>
public sealed class ExecutionRegistry
{
    private readonly ConcurrentDictionary<Guid, RunningQuery> _running = new();

    /// <summary>Максимальный возраст записи: «брошенные» выполнения зачищаются при регистрации новых.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(10);

    public int Count => _running.Count;

    public void Register(RunningQuery query)
    {
        // Пассивная зачистка брошенных выполнений (SSE-клиент так и не подключился).
        CleanupExpired(DefaultMaxAge);
        _running[query.ExecutionId] = query;
    }

    public bool TryGet(Guid executionId, out RunningQuery query) =>
        _running.TryGetValue(executionId, out query!);

    /// <summary>Удаляет запись и освобождает её ресурсы.</summary>
    public bool TryRemove(Guid executionId)
    {
        if (!_running.TryRemove(executionId, out var query))
            return false;
        query.Dispose();
        return true;
    }

    /// <summary>Удаляет записи старше maxAge (с отменой). Возвращает число удалённых.</summary>
    public int CleanupExpired(TimeSpan maxAge)
    {
        var deadline = DateTimeOffset.UtcNow - maxAge;
        var removed = 0;
        foreach (var pair in _running)
        {
            if (pair.Value.CreatedAt > deadline && !pair.Value.IsFinished)
                continue;
            if (_running.TryRemove(pair.Key, out var query))
            {
                query.Cancel();
                query.Dispose();
                removed++;
            }
        }
        return removed;
    }
}
