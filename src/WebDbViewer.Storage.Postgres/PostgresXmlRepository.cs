using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace WebDbViewer.Storage.Postgres;

/// <summary>
/// Кольцо ключей Data Protection в метабазе PostgreSQL.
/// Критично: пароли датасорсов зашифрованы этими ключами — если ключи остаются на диске,
/// а конфигурации переезжают в БД, расшифровать пароли с другой машины невозможно.
/// Реализация намеренно не зависит от DI-графа приложения (bootstrap до Data Protection).
/// </summary>
public sealed class PostgresXmlRepository : IXmlRepository
{
    private readonly PostgresMetaStore meta;
    private readonly ILogger<PostgresXmlRepository>? logger;

    public PostgresXmlRepository(PostgresMetaStore meta, ILogger<PostgresXmlRepository>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(meta);
        this.meta = meta;
        this.logger = logger;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
        => GetAllElementsAsync(CancellationToken.None).GetAwaiter().GetResult();

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        StoreElementAsync(element, friendlyName, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task<IReadOnlyCollection<XElement>> GetAllElementsAsync(CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT xml FROM {meta.Schema}.data_protection_keys ORDER BY created_at", connection);

        var elements = new List<XElement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                elements.Add(XElement.Parse(reader.GetString(0)));
            }
            catch (System.Xml.XmlException ex)
            {
                logger?.LogWarning(ex, "Повреждённый ключ Data Protection в метабазе — пропущен");
            }
        }
        return elements;
    }

    private async Task StoreElementAsync(XElement element, string? friendlyName, CancellationToken ct)
    {
        // friendlyName может быть пустым — тогда генерируем уникальное имя.
        var name = string.IsNullOrWhiteSpace(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName;

        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {meta.Schema}.data_protection_keys (friendly_name, xml)
            VALUES (@name, @xml)
            ON CONFLICT (friendly_name) DO UPDATE SET xml = excluded.xml
            """, connection);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Одноразовый импорт ключей из файловой папки (переезд с PersistKeysToFileSystem).
    /// Уже существующие в метабазе ключи не перезаписываются. Возвращает число импортированных.
    /// </summary>
    public async Task<int> ImportFromDirectoryAsync(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
            return 0;

        var imported = 0;
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);

        foreach (var file in Directory.EnumerateFiles(directory, "*.xml"))
        {
            XElement element;
            try
            {
                element = XElement.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            }
            catch (System.Xml.XmlException ex)
            {
                logger?.LogWarning(ex, "Файл ключа {File} повреждён — пропущен", file);
                continue;
            }

            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO {meta.Schema}.data_protection_keys (friendly_name, xml)
                VALUES (@name, @xml)
                ON CONFLICT (friendly_name) DO NOTHING
                """, connection);
            cmd.Parameters.AddWithValue("name", Path.GetFileNameWithoutExtension(file));
            cmd.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));

            imported += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return imported;
    }
}
