using WebDbViewer.Core;

namespace WebDbViewer.Web.Pages.Shared;

/// <summary>Модель узла дерева навигатора для partial _TreeNode.</summary>
public sealed record TreeNodeVm
{
    public required Guid DsId { get; init; }
    public required DbObjectNode Node { get; init; }

    /// <summary>Путь к ДЕТЯМ узла (сегменты через «/», каждый URL-экранирован). Пустая строка — корень датасорса.</summary>
    public required string Path { get; init; }

    /// <summary>true — узел нельзя раскрывать (результат поиска и т.п.).</summary>
    public bool NoExpand { get; init; }
}

/// <summary>Иконки типов объектов БД для навигатора.</summary>
public static class DbObjectIcons
{
    /// <summary>Возвращает символ-иконку для типа объекта.</summary>
    public static string For(DbObjectType type) => type switch
    {
        DbObjectType.Database => "\U0001F5C4",         // 🗄
        DbObjectType.Schema => "\U0001F4C1",           // 📁
        DbObjectType.Table => "\U0001F4CB",            // 📋
        DbObjectType.View => "\U0001F441",             // 👁
        DbObjectType.MaterializedView => "\U0001F4E6", // 📦
        DbObjectType.Sequence => "\U0001F522",         // 🔢
        DbObjectType.Function => "ƒ",             // ƒ
        DbObjectType.Procedure => "⚙",            // ⚙
        DbObjectType.Package => "\U0001F4E6",          // 📦
        DbObjectType.Type => "\U0001F3F7",             // 🏷
        DbObjectType.Extension => "\U0001F50C",        // 🔌
        DbObjectType.Index => "\U0001F5C2",            // 🗂
        DbObjectType.Trigger => "⚡",              // ⚡
        DbObjectType.Synonym => "\U0001F517",          // 🔗
        DbObjectType.DbLink => "\U0001F310",           // 🌐
        DbObjectType.Tablespace => "\U0001F4BD",       // 💽
        DbObjectType.Column => "▫",               // ▫
        DbObjectType.Constraint => "\U0001F512",       // 🔒
        _ => "•"
    };

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
