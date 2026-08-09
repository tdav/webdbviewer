using System.Data;
using System.Data.Common;

namespace WebDbViewer.Core.Sessions;

/// <summary>
/// Stateful-сессия пользователя с датасорсом: держит открытое соединение
/// (транзакции, temp tables, SET), отслеживает выполняющуюся команду для отмены.
/// </summary>
public sealed class DbSession : IDbSession
{
    /// <summary>
    /// Сколько ждать освобождения соединения. Ожидание «сколько угодно» превращает случайную
    /// параллельность (навигатор, вкладка данных) в зависший запрос, поэтому предел нужен.
    /// </summary>
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    /// <summary>Одно физическое соединение — одна команда за раз (см. <see cref="AcquireAsync"/>).</summary>
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private DbTransaction? _transaction;
    private DbCommand? _currentCommand;
    private int _disposed;

    public DbSession(Guid dataSourceId, string userName, DbConnection connection, string database, bool isPrimary)
    {
        DataSourceId = dataSourceId;
        UserName = userName;
        Connection = connection;
        Database = database;
        IsPrimary = isPrimary;
    }

    public Guid SessionId { get; } = Guid.NewGuid();
    public Guid DataSourceId { get; }
    public string UserName { get; }
    public string Database { get; }
    public bool IsPrimary { get; }
    public DbConnection Connection { get; }
    public bool AutoCommit { get; set; } = true;
    public bool InTransaction => _transaction is not null;
    public DateTimeOffset LastUsedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Текущая открытая транзакция (для привязки команд); null — если нет.</summary>
    public DbTransaction? CurrentTransaction => _transaction;

    /// <summary>Признак того, что сессия освобождена.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Обновляет отметку последнего использования (для TTL).</summary>
    public void Touch() => LastUsedAt = DateTimeOffset.UtcNow;

    public async Task BeginTransactionAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (InTransaction)
            throw new InvalidOperationException("Транзакция уже начата.");
        _transaction = await Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        Touch();
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var tx = _transaction ?? throw new InvalidOperationException("Нет активной транзакции для фиксации.");
        try
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
            Touch();
        }
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var tx = _transaction ?? throw new InvalidOperationException("Нет активной транзакции для отката.");
        try
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await tx.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
            Touch();
        }
    }

    public void CancelRunning()
    {
        DbCommand? cmd;
        lock (_sync)
        {
            cmd = _currentCommand;
        }
        try
        {
            cmd?.Cancel();
        }
        catch
        {
            // Отмена — best effort: провайдер может не поддерживать Cancel или команда уже завершилась.
        }
    }

    /// <summary>
    /// Занимает соединение под одну команду. Захват НЕ реентерабельный: внутри удерживаемого
    /// scope нельзя брать его повторно — вложенный вызов повиснет до таймаута.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        bool acquired;
        try
        {
            acquired = await _connectionGate.WaitAsync(AcquireTimeout, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Сессию закрыли (TTL, лимит), пока запрос стоял в очереди за соединением.
            throw new ObjectDisposedException(nameof(DbSession), "Сессия БД была закрыта во время ожидания.");
        }
        if (!acquired)
            throw new DbSessionBusyException(
                "В сессии уже выполняется запрос. Дождитесь завершения или остановите его.");
        Touch();
        return new ConnectionLease(this);
    }

    /// <summary>
    /// Регистрирует команду как «текущую выполняющуюся» (для <see cref="CancelRunning"/>)
    /// и привязывает её к открытой транзакции сессии. Возвращает scope, снимающий регистрацию.
    /// </summary>
    public IDisposable TrackCommand(DbCommand command)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _currentCommand = command;
        }
        if (_transaction is not null)
            command.Transaction = _transaction;
        Touch();
        return new CommandScope(this, command);
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(DbSession), "Сессия БД уже закрыта.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Гарантированное освобождение: откат незавершённой транзакции и закрытие соединения.
        var tx = _transaction;
        _transaction = null;
        if (tx is not null)
        {
            try { await tx.RollbackAsync().ConfigureAwait(false); }
            catch { /* соединение могло быть разорвано */ }
            try { await tx.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        try { await Connection.DisposeAsync().ConfigureAwait(false); }
        catch { }

        _connectionGate.Dispose();
    }

    /// <summary>Освобождает соединение сессии для следующей команды.</summary>
    private sealed class ConnectionLease : IAsyncDisposable
    {
        private readonly DbSession _session;
        private int _released;

        public ConnectionLease(DbSession session) => _session = session;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _session.Touch();
                // Сессия могла быть закрыта, пока команда выполнялась: семафор уже освобождён вместе с ней.
                try { _session._connectionGate.Release(); }
                catch (ObjectDisposedException) { }
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CommandScope : IDisposable
    {
        private readonly DbSession _session;
        private readonly DbCommand _command;

        public CommandScope(DbSession session, DbCommand command)
        {
            _session = session;
            _command = command;
        }

        public void Dispose()
        {
            lock (_session._sync)
            {
                if (ReferenceEquals(_session._currentCommand, _command))
                    _session._currentCommand = null;
            }
            _session.Touch();
        }
    }
}
