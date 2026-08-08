using System.Data.Common;
using System.Text;
using Npgsql;

namespace WebDbViewer.Providers.Postgres;

/// <summary>
/// Быстрый CSV-экспорт PostgreSQL через COPY … TO STDOUT (FORMAT csv):
/// сервер сам форматирует CSV (RFC4180-совместимо), клиент лишь перекачивает поток.
/// Используется endpoint'ом экспорта вместо построчного чтения для PG-датасорсов.
/// </summary>
public static class PgExportCopy
{
    /// <summary>Соединение поддерживает COPY-экспорт (является NpgsqlConnection)?</summary>
    public static bool CanExport(DbConnection connection) => connection is NpgsqlConnection;

    /// <summary>
    /// Стримит результат <paramref name="selectSql"/> в <paramref name="output"/> как CSV
    /// (с заголовком и разделителем <paramref name="delimiter"/>). BOM пишет вызывающая сторона.
    /// Возвращает false, если соединение не Npgsql (вызывающий делает fallback на построчный экспорт).
    /// </summary>
    public static async Task<bool> TryExportCsvAsync(
        DbConnection connection, string selectSql, char delimiter, Stream output, CancellationToken ct)
    {
        if (connection is not NpgsqlConnection npgsql)
            return false;

        // QUOTE/ESCAPE по умолчанию — двойная кавычка: соответствует RFC4180.
        var copySql = $"COPY ({selectSql}) TO STDOUT (FORMAT csv, HEADER true, DELIMITER '{delimiter}')";

        await using var textReader = await npgsql.BeginTextExportAsync(copySql, ct).ConfigureAwait(false);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true);

        var buffer = new char[32 * 1024];
        int read;
        while ((read = await textReader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            // COPY отдаёт строки с '\n' — приводим к CRLF по RFC4180 не требуется (допустимы оба),
            // пишем как есть, чтобы не буферизовать границы строк.
            await writer.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        await writer.FlushAsync(ct).ConfigureAwait(false);
        return true;
    }
}
