using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WebDbViewer.Web.Api;

/// <summary>
/// Сериализация событий Server-Sent Events (text/event-stream):
/// «event: имя», затем строки «data: …» (многострочные данные разбиваются по спецификации), пустая строка — конец события.
/// </summary>
public static class SseFormatter
{
    /// <summary>Настройки JSON для полезной нагрузки событий (camelCase, как в вебе).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Не экранировать кириллицу: полезная нагрузка SSE не встраивается в HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Форматирует одно SSE-событие с текстовыми данными.</summary>
    public static string Format(string eventName, string data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var sb = new StringBuilder();
        sb.Append("event: ").Append(eventName).Append('\n');
        // По спецификации SSE каждая строка данных передаётся отдельной строкой «data:».
        foreach (var line in (data ?? string.Empty).Split('\n'))
            sb.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Форматирует SSE-событие с JSON-полезной нагрузкой.</summary>
    public static string FormatJson<T>(string eventName, T payload) =>
        Format(eventName, JsonSerializer.Serialize(payload, JsonOptions));
}
