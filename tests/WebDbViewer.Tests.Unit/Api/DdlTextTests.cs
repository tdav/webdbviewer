using System.Data.Common;
using WebDbViewer.Core;
using WebDbViewer.Core.Ddl;
using WebDbViewer.Web.Api;
using WebDbViewer.Web.Pages.Shared;

namespace WebDbViewer.Tests.Unit.Api;

/// <summary>
/// Диспетчеризация типов объектов при получении DDL: значение параметра type,
/// которое кладёт в ссылку дерево, должно распознаваться серверной стороной.
/// </summary>
public sealed class DdlTextTests
{
    /// <summary>Генератор запоминает, каким методом его вызвали.</summary>
    private sealed class RecordingGenerator : IDdlGenerator
    {
        public string? Called { get; private set; }
        public DbObjectType? ObjectType { get; private set; }
        public string? Owner { get; private set; }

        public DbKind Kind => DbKind.Postgres;

        public Task<string> GetTableDdlAsync(DbConnection c, string s, string t, CancellationToken ct)
            => Record("table");

        public Task<string> GetViewDdlAsync(DbConnection c, string s, string v, CancellationToken ct)
            => Record("view");

        public Task<string> GetIndexDdlAsync(DbConnection c, string s, string i, CancellationToken ct)
            => Record("index");

        public Task<string> GetRoutineDdlAsync(DbConnection c, string s, string r, DbObjectType type, CancellationToken ct)
        {
            ObjectType = type;
            return Record("routine");
        }

        public Task<string> GetObjectDdlAsync(
            DbConnection c, string s, string n, DbObjectType type, string? owner, CancellationToken ct)
        {
            ObjectType = type;
            Owner = owner;
            return Record("object");
        }

        public Task<string> GetDropScriptAsync(
            DbConnection c, string s, string n, DbObjectType type, string? qualifier, CancellationToken ct)
        {
            ObjectType = type;
            Owner = qualifier;
            return Record("drop");
        }

        private Task<string> Record(string method)
        {
            Called = method;
            return Task.FromResult($"-- {method}");
        }
    }

    private static readonly string[] KnownTypes =
    [
        "table", "view", "matview", "materializedview", "index", "function", "procedure", "package",
        "sequence", "type", "domain", "foreigntable", "aggregate", "operator", "collation",
        "tsconfig", "tsdictionary", "trigger", "rule", "policy",
    ];

    [Fact]
    public async Task Все_известные_типы_распознаются()
    {
        foreach (var type in KnownTypes)
        {
            var generator = new RecordingGenerator();
            var ddl = await DdlText.GetAsync(generator, null!, "s", "n", type, owner: null, CancellationToken.None);

            Assert.NotNull(ddl);
            Assert.NotNull(generator.Called);
        }
    }

    [Theory]
    [InlineData("TABLE")]
    [InlineData("Trigger")]
    [InlineData("TsConfig")]
    public async Task Тип_нечувствителен_к_регистру(string type)
    {
        var generator = new RecordingGenerator();
        Assert.NotNull(await DdlText.GetAsync(generator, null!, "s", "n", type, null, CancellationToken.None));
    }

    [Fact]
    public async Task Неизвестный_тип_даёт_null()
    {
        var generator = new RecordingGenerator();
        Assert.Null(await DdlText.GetAsync(generator, null!, "s", "n", "tablespace", null, CancellationToken.None));
        Assert.Null(generator.Called);
    }

    [Fact]
    public async Task Владелец_передаётся_объектам_принадлежащим_таблице()
    {
        var generator = new RecordingGenerator();
        await DdlText.GetAsync(generator, null!, "demo_core", "tg_x", "trigger", "products", CancellationToken.None);

        Assert.Equal("object", generator.Called);
        Assert.Equal(DbObjectType.Trigger, generator.ObjectType);
        Assert.Equal("products", generator.Owner);
    }

    /// <summary>
    /// Ссылки «показать DDL» и «скрипт удаления» в дереве строятся по DdlObjectTypes.ForApi —
    /// значения должны совпадать с тем, что понимает DdlText, иначе кнопка вернёт ошибку.
    /// </summary>
    [Fact]
    public async Task Типы_из_дерева_понимаются_сервером()
    {
        var treeTypes = Enum.GetValues<DbObjectType>()
            .Select(DdlObjectTypes.ForApi)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();

        Assert.NotEmpty(treeTypes);

        foreach (var type in treeTypes)
        {
            var ddl = await DdlText.GetAsync(new RecordingGenerator(), null!, "s", "n", type, null, CancellationToken.None);
            Assert.True(ddl is not null, $"Тип «{type}» из дерева не распознан при получении DDL.");

            // Пакетов в PostgreSQL нет — скрипт удаления для них не предусмотрен.
            if (type == "package")
                continue;

            var drop = await DdlText.GetDropAsync(new RecordingGenerator(), null!, "s", "n", type, null, CancellationToken.None);
            Assert.True(drop is not null, $"Тип «{type}» из дерева не распознан при удалении.");
        }
    }

    [Fact]
    public async Task Скрипт_удаления_получает_уточнение_объекта()
    {
        var generator = new RecordingGenerator();
        await DdlText.GetDropAsync(generator, null!, "demo_core", "fn_lookup", "function", "p_code text", CancellationToken.None);

        Assert.Equal("drop", generator.Called);
        Assert.Equal(DbObjectType.Function, generator.ObjectType);
        Assert.Equal("p_code text", generator.Owner);
    }

    [Fact]
    public async Task Неизвестный_тип_не_даёт_скрипта_удаления()
    {
        var generator = new RecordingGenerator();
        Assert.Null(await DdlText.GetDropAsync(generator, null!, "s", "n", "tablespace", null, CancellationToken.None));
        Assert.Null(generator.Called);
    }
}
