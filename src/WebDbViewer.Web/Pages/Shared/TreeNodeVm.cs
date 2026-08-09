using Microsoft.AspNetCore.Html;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Pages.Shared;

/// <summary>Модель узла дерева навигатора для partial _TreeNode.</summary>
public sealed record TreeNodeVm
{
    public required Guid DsId { get; init; }
    public required DbObjectNode Node { get; init; }

    /// <summary>Путь к ДЕТЯМ узла (сегменты через «/», каждый URL-экранирован). Пустая строка — корень датасорса.</summary>
    public required string Path { get; init; }

    /// <summary>База данных, в которой находится объект; null — база из конфигурации датасорса.</summary>
    public string? Database { get; init; }

    /// <summary>true — узел нельзя раскрывать (результат поиска и т.п.).</summary>
    public bool NoExpand { get; init; }
}

/// <summary>Иконки типов объектов БД для навигатора.</summary>
/// <remarks>
/// Единый штриховой набор 14×14, currentColor: emoji в профессиональном
/// инструменте запрещены дизайн-системой (см. DESIGN.md), и на разных ОС они
/// приезжают в чужой цветовой гамме и разной оптической плотности.
/// </remarks>
public static class DbObjectIcons
{
    // Одна и та же обёртка для всех иконок: размер и цвет задаёт .tree-icon в CSS.
    private static HtmlString Icon(string body) => new(
        "<svg class=\"obj-icon\" viewBox=\"0 0 16 16\" fill=\"none\" stroke=\"currentColor\" " +
        "stroke-width=\"1.4\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" +
        body + "</svg>");

    private static readonly Dictionary<DbObjectType, HtmlString> icons = new()
    {
        // Цилиндр — база данных.
        [DbObjectType.Database] = Icon("<ellipse cx='8' cy='3.6' rx='5' ry='2.1'/><path d='M3 3.6v8.8c0 1.16 2.24 2.1 5 2.1s5-.94 5-2.1V3.6'/><path d='M3 8c0 1.16 2.24 2.1 5 2.1s5-.94 5-2.1'/>"),
        // Папка — схема.
        [DbObjectType.Schema] = Icon("<path d='M2 4.2a1 1 0 0 1 1-1h3.2l1.3 1.6H13a1 1 0 0 1 1 1v6.6a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1z'/>"),
        // Сетка — таблица.
        [DbObjectType.Table] = Icon("<rect x='2' y='3' width='12' height='10' rx='1'/><path d='M2 6.4h12M6.6 6.4V13'/>"),
        // Глаз — представление.
        [DbObjectType.View] = Icon("<path d='M1.4 8S3.8 3.8 8 3.8 14.6 8 14.6 8 12.2 12.2 8 12.2 1.4 8 1.4 8z'/><circle cx='8' cy='8' r='1.9'/>"),
        // Глаз в рамке — материализованное представление: результат сохранён на диск.
        [DbObjectType.MaterializedView] = Icon("<rect x='1.8' y='3.4' width='12.4' height='9.2' rx='1'/><path d='M4 8s1.6-2.2 4-2.2S12 8 12 8s-1.6 2.2-4 2.2S4 8 4 8z'/><circle cx='8' cy='8' r='1'/>"),
        // Ступени — последовательность.
        [DbObjectType.Sequence] = Icon("<path d='M2.4 12.6h3.2V9.4h3.2V6.2h3.2V3'/><path d='M11 3h2v2'/>"),
        // Скобки — функция.
        [DbObjectType.Function] = Icon("<path d='M6 3.2C4.4 4.4 3.6 6 3.6 8s.8 3.6 2.4 4.8M10 3.2c1.6 1.2 2.4 2.8 2.4 4.8s-.8 3.6-2.4 4.8'/><path d='M6.9 8h2.2'/>"),
        // Шестерня — процедура.
        [DbObjectType.Procedure] = Icon("<circle cx='8' cy='8' r='2.1'/><path d='M8 1.9v1.6M8 12.5v1.6M14.1 8h-1.6M3.5 8H1.9M12.3 3.7l-1.1 1.1M4.8 11.2l-1.1 1.1M12.3 12.3l-1.1-1.1M4.8 4.8 3.7 3.7'/>"),
        // Коробка — пакет.
        [DbObjectType.Package] = Icon("<path d='M8 1.9 14 5v6L8 14.1 2 11V5z'/><path d='M2 5l6 3.1L14 5M8 8.1V14'/>"),
        // Ярлык — тип.
        [DbObjectType.Type] = Icon("<path d='M7.4 2.2H13a.8.8 0 0 1 .8.8v5.6a.8.8 0 0 1-.23.57l-5.6 5.6a.8.8 0 0 1-1.14 0L2.2 10.2a.8.8 0 0 1 0-1.14l5.6-5.6a.8.8 0 0 1 .57-.23z'/><circle cx='10.9' cy='5.1' r='.9'/>"),
        // Разъём — расширение.
        [DbObjectType.Extension] = Icon("<path d='M5.4 6V2.6M10.6 6V2.6'/><path d='M3.6 6h8.8v3a3.4 3.4 0 0 1-3.4 3.4H7A3.4 3.4 0 0 1 3.6 9z'/><path d='M8 12.4v2'/>"),
        // Стопка карточек — индекс.
        [DbObjectType.Index] = Icon("<path d='M2.4 5.4 8 2.6l5.6 2.8L8 8.2z'/><path d='m2.4 8.2 5.6 2.8 5.6-2.8M2.4 11l5.6 2.8L13.6 11'/>"),
        // Молния — триггер.
        [DbObjectType.Trigger] = Icon("<path d='M8.9 1.9 3.6 9h3.6l-.9 5.1L13.4 7H9.8z'/>"),
        // Две ссылки — синоним.
        [DbObjectType.Synonym] = Icon("<path d='M6.6 9.4a2.8 2.8 0 0 0 4.2.3l1.7-1.7a2.8 2.8 0 0 0-4-4l-1 1'/><path d='M9.4 6.6a2.8 2.8 0 0 0-4.2-.3L3.5 8a2.8 2.8 0 0 0 4 4l1-1'/>"),
        // Глобус — линк на внешнюю БД.
        [DbObjectType.DbLink] = Icon("<circle cx='8' cy='8' r='6.1'/><path d='M1.9 8h12.2'/><path d='M8 1.9a9.4 9.4 0 0 1 0 12.2 9.4 9.4 0 0 1 0-12.2z'/>"),
        // Диск — табличное пространство.
        [DbObjectType.Tablespace] = Icon("<rect x='1.9' y='1.9' width='12.2' height='12.2' rx='1.4'/><circle cx='8' cy='8' r='2.6'/><circle cx='8' cy='8' r='.5'/>"),
        // Вертикальный столбец — колонка.
        [DbObjectType.Column] = Icon("<rect x='5.6' y='2.4' width='4.8' height='11.2' rx='1'/><path d='M5.6 6h4.8'/>"),
        // Замок — ограничение.
        [DbObjectType.Constraint] = Icon("<rect x='3.2' y='7' width='9.6' height='6.6' rx='1.2'/><path d='M5.6 7V5.2a2.4 2.4 0 0 1 4.8 0V7'/>"),
    };

    private static readonly HtmlString fallbackIcon =
        Icon("<circle cx='8' cy='8' r='2.4'/>");

    /// <summary>Возвращает готовую SVG-иконку для типа объекта.</summary>
    public static IHtmlContent For(DbObjectType type) =>
        icons.TryGetValue(type, out var icon) ? icon : fallbackIcon;

    /// <summary>Русское название типа объекта (для подсказок).</summary>
    public static string Title(DbObjectType type) => type switch
    {
        DbObjectType.Database => "База данных",
        DbObjectType.Schema => "Схема",
        DbObjectType.Table => "Таблица",
        DbObjectType.View => "Представление",
        DbObjectType.MaterializedView => "Материализованное представление",
        DbObjectType.Sequence => "Последовательность",
        DbObjectType.Function => "Функция",
        DbObjectType.Procedure => "Процедура",
        DbObjectType.Package => "Пакет",
        DbObjectType.Type => "Тип",
        DbObjectType.Extension => "Расширение",
        DbObjectType.Index => "Индекс",
        DbObjectType.Trigger => "Триггер",
        DbObjectType.Synonym => "Синоним",
        DbObjectType.DbLink => "Линк БД",
        DbObjectType.Tablespace => "Табличное пространство",
        DbObjectType.Column => "Колонка",
        DbObjectType.Constraint => "Ограничение",
        _ => type.ToString()
    };
}
