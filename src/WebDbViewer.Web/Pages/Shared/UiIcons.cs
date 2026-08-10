using Microsoft.AspNetCore.Html;

namespace WebDbViewer.Web.Pages.Shared;

/// <summary>Иконки действий интерфейса.</summary>
/// <remarks>
/// Один штриховой набор 16×16 на currentColor — та же оптическая плотность, что у
/// иконок дерева (<see cref="DbObjectIcons"/>). Набор лежит в одном месте, чтобы
/// одинаковое действие на разных экранах не получило две разные пиктограммы.
///
/// Кнопки в системе — иконочные, поэтому у каждой обязаны быть aria-label
/// (доступное имя) и data-tip (видимая подсказка). Иконка внутри всегда
/// aria-hidden: она не должна попадать в дерево доступности второй раз.
/// </remarks>
public static class UiIcons
{
    private static HtmlString Stroke(string body) => new(
        "<svg class=\"ui-icon\" viewBox=\"0 0 16 16\" fill=\"none\" stroke=\"currentColor\" " +
        "stroke-width=\"1.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" +
        body + "</svg>");

    private static HtmlString Solid(string body) => new(
        "<svg class=\"ui-icon\" viewBox=\"0 0 16 16\" fill=\"currentColor\" aria-hidden=\"true\">" +
        body + "</svg>");

    // --- Выполнение запроса ---

    /// <summary>Треугольник воспроизведения — выполнить текущий запрос.</summary>
    public static readonly HtmlString Play =
        Solid("<path d='M4.6 3a.7.7 0 0 1 1.07-.6l6.6 5a.7.7 0 0 1 0 1.2l-6.6 5A.7.7 0 0 1 4.6 13z'/>");

    /// <summary>Двойной треугольник — выполнить весь скрипт.</summary>
    public static readonly HtmlString PlayScript =
        Solid("<path d='M2.2 3.3a.6.6 0 0 1 .95-.5l4.7 4.2a.6.6 0 0 1 0 1l-4.7 4.2a.6.6 0 0 1-.95-.5z'/>" +
              "<path d='M8.4 3.3a.6.6 0 0 1 .95-.5l4.7 4.2a.6.6 0 0 1 0 1l-4.7 4.2a.6.6 0 0 1-.95-.5z'/>");

    /// <summary>Квадрат — остановить выполнение.</summary>
    public static readonly HtmlString Stop =
        Solid("<rect x='3.8' y='3.8' width='8.4' height='8.4' rx='1.2'/>");

    // --- Транзакция ---

    /// <summary>Галочка — зафиксировать транзакцию.</summary>
    public static readonly HtmlString Commit =
        Stroke("<path d='M2.8 8.6 6.2 12l7-8'/>");

    /// <summary>Стрелка против часовой — откатить транзакцию, отменить изменения.</summary>
    public static readonly HtmlString Rollback =
        Stroke("<path d='M2.6 4.2v3.6h3.6'/><path d='M3.3 7.6a5.2 5.2 0 1 1 1.2 4.3'/>");

    // --- Общие действия ---

    /// <summary>Плюс — создать вкладку, подключение, строку.</summary>
    public static readonly HtmlString Plus =
        Stroke("<path d='M8 3.2v9.6M3.2 8h9.6'/>");

    /// <summary>Круговая стрелка — перечитать данные.</summary>
    public static readonly HtmlString Refresh =
        Stroke("<path d='M13.4 3.4v3.4H10'/><path d='M12.7 6.6a5.2 5.2 0 1 0-.5 5.2'/>");

    /// <summary>Воронка — применить фильтр, показать выборку.</summary>
    public static readonly HtmlString Filter =
        Stroke("<path d='M2.4 3.2h11.2l-4.3 5.1v4.4l-2.6 1.3V8.3z'/>");

    /// <summary>Карандаш — изменить запись.</summary>
    public static readonly HtmlString Pencil =
        Stroke("<path d='M11.1 2.6a1.4 1.4 0 0 1 2 2l-7.4 7.4-2.7.7.7-2.7z'/><path d='M10 3.7l2.3 2.3'/>");

    /// <summary>Корзина — удалить запись.</summary>
    public static readonly HtmlString Trash =
        Stroke("<path d='M2.8 4.4h10.4'/><path d='M6.4 4.4V3.2a.8.8 0 0 1 .8-.8h1.6a.8.8 0 0 1 .8.8v1.2'/>" +
               "<path d='M4.2 4.4l.6 8.2a1 1 0 0 0 1 .9h4.4a1 1 0 0 0 1-.9l.6-8.2'/>");

    /// <summary>Разъём — проверить соединение с базой.</summary>
    public static readonly HtmlString Plug =
        Stroke("<path d='M5.6 6V2.6M10.4 6V2.6'/><path d='M3.8 6h8.4v2.9a3.2 3.2 0 0 1-3.2 3.2H7a3.2 3.2 0 0 1-3.2-3.2z'/>" +
               "<path d='M8 12.1v2.3'/>");

    /// <summary>Крест — закрыть без сохранения.</summary>
    public static readonly HtmlString Close =
        Stroke("<path d='M4 4l8 8M12 4l-8 8'/>");

    /// <summary>Дискета — сохранить форму. Отличается от галочки Commit намеренно:
    /// одинаковая иконка на «зафиксировать транзакцию» и «сохранить подключение»
    /// склеила бы два разных действия.</summary>
    public static readonly HtmlString Save =
        Stroke("<path d='M3.4 2.6h7.2l2.8 2.8v8a1 1 0 0 1-1 1H3.4a1 1 0 0 1-1-1V3.6a1 1 0 0 1 1-1z'/>" +
               "<path d='M5.2 2.6v3.6h5.2V2.6'/><path d='M5.2 14.4v-4.2h5.6v4.2'/>");

    // --- Сессия ---

    /// <summary>Стрелка внутрь — войти в систему.</summary>
    public static readonly HtmlString Login =
        Stroke("<path d='M9.6 2.6h2.8a1.2 1.2 0 0 1 1.2 1.2v8.4a1.2 1.2 0 0 1-1.2 1.2H9.6'/>" +
               "<path d='M6.6 11 9.6 8 6.6 5'/><path d='M9.6 8H2.4'/>");

    /// <summary>Стрелка наружу — выйти из системы.</summary>
    public static readonly HtmlString Logout =
        Stroke("<path d='M6.4 2.6H3.6a1.2 1.2 0 0 0-1.2 1.2v8.4a1.2 1.2 0 0 0 1.2 1.2h2.8'/>" +
               "<path d='M10.6 11 13.6 8l-3-3'/><path d='M13.6 8H6.4'/>");

    /// <summary>Солнце — переключить тему.</summary>
    public static readonly HtmlString Theme =
        Stroke("<circle cx='8' cy='8' r='3.4'/>" +
               "<path d='M8 .9v1.8M8 13.3v1.8M15.1 8h-1.8M2.7 8H.9M13.1 2.9l-1.3 1.3M4.2 11.8l-1.3 1.3M13.1 13.1l-1.3-1.3M4.2 4.2 2.9 2.9'/>");

    /// <summary>Глаз — показать значение поля пароля.</summary>
    public static readonly HtmlString Eye =
        Stroke("<path d='M1.5 8S4.1 3.7 8 3.7 14.5 8 14.5 8 11.9 12.3 8 12.3 1.5 8 1.5 8Z'/>" +
               "<circle cx='8' cy='8' r='2'/>");

    /// <summary>Перечёркнутый глаз — снова скрыть значение поля пароля.</summary>
    public static readonly HtmlString EyeOff =
        Stroke("<path d='M6.4 4A6.6 6.6 0 0 1 8 3.7c3.9 0 6.5 4.3 6.5 4.3a12.4 12.4 0 0 1-2.2 2.7'/>" +
               "<path d='M3.9 5.1A12.4 12.4 0 0 0 1.5 8s2.6 4.3 6.5 4.3a6.6 6.6 0 0 0 2.4-.4'/>" +
               "<path d='M6.6 6.6a2 2 0 0 0 2.8 2.8'/><path d='m2.6 2.6 10.8 10.8'/>");

    /// <summary>Силуэт пользователя — текущая учётная запись.</summary>
    public static readonly HtmlString User =
        Stroke("<circle cx='8' cy='5.4' r='2.8'/><path d='M2.6 14c0-2.9 2.4-4.6 5.4-4.6s5.4 1.7 5.4 4.6'/>");

    /// <summary>Цилиндр — знак приложения.</summary>
    public static readonly HtmlString Brand =
        Stroke("<ellipse cx='8' cy='3.6' rx='5' ry='2.1'/>" +
               "<path d='M3 3.6v8.8c0 1.16 2.24 2.1 5 2.1s5-.94 5-2.1V3.6'/>" +
               "<path d='M3 8c0 1.16 2.24 2.1 5 2.1s5-.94 5-2.1'/>");

    /// <summary>Стрелка из рамки — открыть данные объекта.</summary>
    public static readonly HtmlString OpenData =
        Stroke("<path d='M13.4 9.2v3.4a1.2 1.2 0 0 1-1.2 1.2H3.4a1.2 1.2 0 0 1-1.2-1.2V3.8a1.2 1.2 0 0 1 1.2-1.2h3.4'/>" +
               "<path d='M10.2 2.6h3.2v3.2M13.4 2.6 7.6 8.4'/>");

    /// <summary>Лист с угловыми скобками — показать DDL объекта.</summary>
    public static readonly HtmlString Ddl =
        Stroke("<path d='M9 1.9H4.2a1.2 1.2 0 0 0-1.2 1.2v9.8a1.2 1.2 0 0 0 1.2 1.2h7.6a1.2 1.2 0 0 0 1.2-1.2V5.9z'/>" +
               "<path d='M9 1.9v4h4'/><path d='M6.6 8.6 5.2 10l1.4 1.4M9.4 8.6 10.8 10l-1.4 1.4'/>");

    /// <summary>Стрелка вниз к полке — выгрузить объект в SQL-скрипт.</summary>
    public static readonly HtmlString Export =
        Stroke("<path d='M8 2.4v7.2'/><path d='M5.2 6.8 8 9.6l2.8-2.8'/>" +
               "<path d='M2.6 11.4v1.4a1.2 1.2 0 0 0 1.2 1.2h8.4a1.2 1.2 0 0 0 1.2-1.2v-1.4'/>");

    /// <summary>Стрелка вверх от полки — залить SQL-скрипт в базу.</summary>
    public static readonly HtmlString Import =
        Stroke("<path d='M8 9.6V2.4'/><path d='M5.2 5.2 8 2.4l2.8 2.8'/>" +
               "<path d='M2.6 11.4v1.4a1.2 1.2 0 0 0 1.2 1.2h8.4a1.2 1.2 0 0 0 1.2-1.2v-1.4'/>");

    // --- Правка данных в гриде ---

    /// <summary>Строка со плюсом — добавить строку в таблицу.</summary>
    public static readonly HtmlString RowAdd =
        Stroke("<path d='M2.4 4.6h11.2M2.4 8h6.2M2.4 11.4h4.4'/><path d='M11.6 9.2v5.2M9 11.8h5.2'/>");

    /// <summary>Строка с крестом — пометить выделенные строки на удаление.</summary>
    public static readonly HtmlString RowDelete =
        Stroke("<path d='M2.4 4.6h11.2M2.4 8h6.2M2.4 11.4h4.4'/><path d='M9.8 10 14 14.2M14 10l-4.2 4.2'/>");

    /// <summary>Шеврон вниз — свернуть панель. Развёрнутое состояние разворачивает иконку через CSS.</summary>
    public static readonly HtmlString ChevronDown =
        Stroke("<path d='M3.6 6 8 10.4 12.4 6'/>");
}
