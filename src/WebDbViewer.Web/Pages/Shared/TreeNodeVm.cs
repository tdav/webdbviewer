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

    /// <summary>СУБД датасорса; задаётся только у корневого узла, чтобы иконка отличала Oracle от PostgreSQL.</summary>
    public DbKind? Kind { get; init; }

    /// <summary>
    /// Узел занимает в дереве место базы данных: схема Oracle сразу под датасорсом.
    /// Рисуется цилиндром, чтобы второй уровень выглядел одинаково в обеих СУБД;
    /// на тип объекта и поведение узла не влияет.
    /// </summary>
    public bool AsDatabase { get; init; }

    /// <summary>true — узел нельзя раскрывать (результат поиска и т.п.).</summary>
    public bool NoExpand { get; init; }

    /// <summary>
    /// Датасорс открыт только для чтения. Скрывает действия, ведущие к записи;
    /// сам запрет обеспечивает сессия (default_transaction_read_only), а не разметка.
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>Соответствие типа объекта значению параметра <c>type</c> в /api/ddl и обработчике DdlTab.</summary>
public static class DdlObjectTypes
{
    private static readonly Dictionary<DbObjectType, string> map = new()
    {
        [DbObjectType.Table] = "table",
        [DbObjectType.View] = "view",
        [DbObjectType.MaterializedView] = "matview",
        [DbObjectType.Index] = "index",
        [DbObjectType.Function] = "function",
        [DbObjectType.Procedure] = "procedure",
        [DbObjectType.Package] = "package",
        [DbObjectType.Sequence] = "sequence",
        [DbObjectType.Type] = "type",
        [DbObjectType.Domain] = "domain",
        [DbObjectType.ForeignTable] = "foreigntable",
        [DbObjectType.Aggregate] = "aggregate",
        [DbObjectType.Operator] = "operator",
        [DbObjectType.Collation] = "collation",
        [DbObjectType.TextSearchConfig] = "tsconfig",
        [DbObjectType.TextSearchDictionary] = "tsdictionary",
        [DbObjectType.Trigger] = "trigger",
        [DbObjectType.Rule] = "rule",
        [DbObjectType.Policy] = "policy",
    };

    /// <summary>Строковый тип для запроса DDL; null — для типа объекта DDL не генерируется.</summary>
    public static string? ForApi(DbObjectType type) => map.GetValueOrDefault(type);
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
        // Сетка со стрелкой наружу — внешняя таблица (данные лежат на другом сервере).
        [DbObjectType.ForeignTable] = Icon("<path d='M13.6 8.6V12a1 1 0 0 1-1 1H3.4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h3.4'/><path d='M2.4 6.4h5.2'/><path d='M10 6h3.6V2.4'/><path d='M9.2 6.8 13.6 2.4'/>"),
        // Воронка — агрегатная функция: множество строк сводится к одному значению.
        [DbObjectType.Aggregate] = Icon("<path d='M2.2 2.8h11.6l-4.4 5.2v5.2l-2.8-1.6V8z'/>"),
        // Ярлык с рамкой — домен: тип с ограничением.
        [DbObjectType.Domain] = Icon("<path d='M7.4 2.4H13a.7.7 0 0 1 .7.7v5.4a.7.7 0 0 1-.2.5l-5.4 5.4a.7.7 0 0 1-1 0L2.4 9.3a.7.7 0 0 1 0-1l5.4-5.4a.7.7 0 0 1 .5-.2z'/><path d='M5.6 8.4 7.4 10.2l3.4-3.4'/>"),
        // Плюс-минус — оператор.
        [DbObjectType.Operator] = Icon("<circle cx='8' cy='8' r='6.1'/><path d='M4.6 6.2h3.2M6.2 4.6v3.2M8.6 10.6h3'/>"),
        // Буквы с порядком — правило сортировки.
        [DbObjectType.Collation] = Icon("<path d='M2.2 11.4 4.8 4.6l2.6 6.8'/><path d='M3 9.4h3.6'/><path d='M11.4 3.4v9.2'/><path d='M9.4 10.6 11.4 12.6 13.4 10.6'/>"),
        // Щит — политика RLS.
        [DbObjectType.Policy] = Icon("<path d='M8 1.9 13.2 3.6v4.1c0 3-2.1 5.2-5.2 6.4-3.1-1.2-5.2-3.4-5.2-6.4V3.6z'/><path d='m5.9 7.9 1.5 1.5 2.7-2.7'/>"),
        // Стрелка с ветвлением — правило перезаписи.
        [DbObjectType.Rule] = Icon("<path d='M2.4 4.2h4.2c2 0 2 7.6 4 7.6h3'/><path d='M11.6 9.6 14 11.8l-2.4 2.2'/><path d='M2.4 11.8h3.2'/>"),
        // Лупа над текстом — конфигурация полнотекстового поиска.
        [DbObjectType.TextSearchConfig] = Icon("<path d='M2.4 3.6h11.2M2.4 6.4h6.4M2.4 9.2h4'/><circle cx='10.4' cy='10.4' r='2.8'/><path d='m12.6 12.6 1.6 1.6'/>"),
        // Книга — словарь полнотекстового поиска.
        [DbObjectType.TextSearchDictionary] = Icon("<path d='M2.4 3.2a1 1 0 0 1 1-1H7a1.6 1.6 0 0 1 1.6 1.6v9.4A1.4 1.4 0 0 0 7.2 12H2.4z'/><path d='M13.6 3.2a1 1 0 0 0-1-1H9a1.6 1.6 0 0 0-1.6 1.6v9.4A1.4 1.4 0 0 1 8.8 12h4.8z'/>"),
    };

    private static readonly HtmlString fallbackIcon =
        Icon("<circle cx='8' cy='8' r='2.4'/>");

    // Фирменный логотип СУБД: датасорсы Oracle и PostgreSQL должны различаться
    // раньше, чем прочитана подпись. Единственное цветное исключение в монохромном
    // наборе — логотип узнаётся цветом, перерисовывать его штрихом нельзя.
    // Файлы 20×20, в дереве масштабируются до 14px (.tree-icon .obj-icon).
    private static HtmlString KindIcon(string file, string alt) => new(
        "<img class=\"obj-icon\" src=\"/images/" + file + "\" alt=\"\" title=\"" + alt + "\">");

    private static readonly Dictionary<DbKind, HtmlString> kindIcons = new()
    {
        [DbKind.Postgres] = KindIcon("postgres_icon.png", "PostgreSQL"),
        [DbKind.Oracle] = KindIcon("oracle_icon.png", "Oracle"),
    };

    /// <summary>Возвращает готовую SVG-иконку для типа объекта.</summary>
    public static IHtmlContent For(DbObjectType type) =>
        icons.TryGetValue(type, out var icon) ? icon : fallbackIcon;

    /// <summary>Иконка узла базы данных с меткой СУБД; для остальных типов — обычная иконка.</summary>
    public static IHtmlContent For(DbObjectType type, DbKind? kind) =>
        type == DbObjectType.Database && kind is { } k ? kindIcons[k] : For(type);

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
        DbObjectType.ForeignTable => "Внешняя таблица",
        DbObjectType.Aggregate => "Агрегатная функция",
        DbObjectType.Domain => "Домен",
        DbObjectType.Operator => "Оператор",
        DbObjectType.Collation => "Правило сортировки",
        DbObjectType.Policy => "Политика RLS",
        DbObjectType.Rule => "Правило перезаписи",
        DbObjectType.TextSearchConfig => "Конфигурация полнотекстового поиска",
        DbObjectType.TextSearchDictionary => "Словарь полнотекстового поиска",
        _ => type.ToString()
    };

    /// <summary>Название типа с уточнением СУБД для узлов-баз.</summary>
    public static string Title(DbObjectType type, DbKind? kind) =>
        type == DbObjectType.Database && kind is { } k
            ? "База данных " + (k == DbKind.Oracle ? "Oracle" : "PostgreSQL")
            : Title(type);
}
