using Oracle.ManagedDataAccess.Client;

namespace WebDbViewer.Tests.Integration.Export;

/// <summary>
/// Подключение к Oracle для интеграционных тестов. Живого сервера может не быть,
/// поэтому тест сначала пробует открыть соединение и при неудаче пропускает себя
/// через <c>Skip.If</c>, а не падает.
/// </summary>
internal static class OracleTestDatabase
{
    private const string DefaultConnectionString =
        "User Id=system;Password=oracle;Data Source=localhost:1521/XEPDB1;";

    /// <summary>
    /// Строка подключения: переменная окружения <c>WEBDBVIEWER_TEST_ORACLE</c>, иначе значение по умолчанию.
    /// Тест показывает её в сообщении о пропуске, чтобы было видно, куда он пытался достучаться.
    /// </summary>
    public static string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("WEBDBVIEWER_TEST_ORACLE") ?? DefaultConnectionString;

    /// <summary>
    /// Пробное подключение к Oracle. Возвращает открытое соединение, которое освобождает вызывающий,
    /// либо <c>null</c>, если сервер недоступен по любой причине.
    /// </summary>
    public static async Task<OracleConnection?> TryOpenAsync(CancellationToken ct)
    {
        var connection = new OracleConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch (Exception)
        {
            // Сервера нет, нет прав, не тот сервис — для теста всё это одно и то же: пропуск.
            await connection.DisposeAsync();
            return null;
        }
    }
}
