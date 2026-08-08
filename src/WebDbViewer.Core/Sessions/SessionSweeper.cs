using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebDbViewer.Core.Sessions;

/// <summary>Фоновый сервис: периодически закрывает просроченные по TTL сессии БД.</summary>
public sealed class SessionSweeper : BackgroundService
{
    private readonly DbSessionManager _manager;
    private readonly DbSessionOptions _options;
    private readonly ILogger<SessionSweeper>? _logger;

    public SessionSweeper(DbSessionManager manager, IOptions<DbSessionOptions> options, ILogger<SessionSweeper>? logger = null)
    {
        _manager = manager;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.SweepInterval > TimeSpan.Zero ? _options.SweepInterval : TimeSpan.FromMinutes(1);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var closed = await _manager.SweepExpiredAsync(stoppingToken).ConfigureAwait(false);
                    if (closed > 0)
                        _logger?.LogInformation("Свипер закрыл {Count} просроченных сессий БД.", closed);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка фоновой очистки сессий БД.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка приложения.
        }
    }
}
