using WebDbViewer.Web.Api;

namespace WebDbViewer.Tests.Unit.Api;

/// <summary>Тесты сериализации SSE-формата (text/event-stream).</summary>
public class SseFormatterTests
{
    [Fact]
    public void Format_ПростоеСобытие_ИмяИДанныеИПустаяСтрока()
    {
        var result = SseFormatter.Format("done", "{\"rowCount\":5}");

        Assert.Equal("event: done\ndata: {\"rowCount\":5}\n\n", result);
    }

    [Fact]
    public void Format_МногострочныеДанные_КаждаяСтрокаСПрефиксомData()
    {
        var result = SseFormatter.Format("row", "line1\nline2\nline3");

        Assert.Equal("event: row\ndata: line1\ndata: line2\ndata: line3\n\n", result);
    }

    [Fact]
    public void Format_ДанныеСWindowsПереносами_CRОтбрасывается()
    {
        var result = SseFormatter.Format("meta", "a\r\nb");

        Assert.Equal("event: meta\ndata: a\ndata: b\n\n", result);
    }

    [Fact]
    public void Format_ПустыеДанные_ОднаПустаяDataСтрока()
    {
        var result = SseFormatter.Format("ping", "");

        Assert.Equal("event: ping\ndata: \n\n", result);
    }

    [Fact]
    public void Format_ПустоеИмяСобытия_Исключение()
    {
        Assert.ThrowsAny<ArgumentException>(() => SseFormatter.Format("", "x"));
    }

    [Fact]
    public void FormatJson_АнонимныйОбъект_CamelCaseИКорректныйJson()
    {
        var result = SseFormatter.FormatJson("done", new { RowCount = 3, Truncated = false });

        Assert.Equal("event: done\ndata: {\"rowCount\":3,\"truncated\":false}\n\n", result);
    }

    [Fact]
    public void FormatJson_Null_СериализуетсяКакNull()
    {
        var result = SseFormatter.FormatJson<object?>("error", null);

        Assert.Equal("event: error\ndata: null\n\n", result);
    }

    [Fact]
    public void FormatJson_СтрокаСПереносом_ЭкранируетсяВнутриJsonБезРазбиения()
    {
        // JSON экранирует \n внутри строки, поэтому событие остаётся однострочным.
        var result = SseFormatter.FormatJson("error", new { Message = "стр1\nстр2" });

        Assert.Equal("event: error\ndata: {\"message\":\"стр1\\nстр2\"}\n\n", result);
    }

    [Fact]
    public void Format_СобытиеЗаканчиваетсяПустойСтрокой_РазделительСобытий()
    {
        var result = SseFormatter.Format("meta", "x");

        Assert.EndsWith("\n\n", result);
        Assert.DoesNotContain("\n\n\n", result);
    }
}
