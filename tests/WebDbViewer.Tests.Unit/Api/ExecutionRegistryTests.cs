using WebDbViewer.Core;
using WebDbViewer.Web.Api;

namespace WebDbViewer.Tests.Unit.Api;

/// <summary>Тесты реестра выполняющихся запросов.</summary>
public class ExecutionRegistryTests
{
    private static RunningQuery MakeQuery(FakeDbSession? session = null) => new()
    {
        DataSourceId = Guid.NewGuid(),
        UserName = "tester",
        SqlText = "SELECT 1",
        Statements = [new SqlStatement { Text = "SELECT 1", Offset = 0, Length = 8 }],
        Session = session ?? new FakeDbSession(),
    };

    [Fact]
    public void Register_ЗатемTryGet_ВозвращаетТуЖеЗапись()
    {
        var registry = new ExecutionRegistry();
        var query = MakeQuery();

        registry.Register(query);

        Assert.True(registry.TryGet(query.ExecutionId, out var found));
        Assert.Same(query, found);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void TryGet_НеизвестныйId_False()
    {
        var registry = new ExecutionRegistry();

        Assert.False(registry.TryGet(Guid.NewGuid(), out _));
    }

    [Fact]
    public void TryRemove_УдаляетЗапись()
    {
        var registry = new ExecutionRegistry();
        var query = MakeQuery();
        registry.Register(query);

        Assert.True(registry.TryRemove(query.ExecutionId));
        Assert.False(registry.TryGet(query.ExecutionId, out _));
        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryRemove(query.ExecutionId)); // повторное удаление — false
    }

    [Fact]
    public void TryBeginStreaming_ТолькоПервыйВызовУспешен()
    {
        var query = MakeQuery();

        Assert.True(query.TryBeginStreaming());
        Assert.False(query.TryBeginStreaming()); // защита от двойного подключения SSE
    }

    [Fact]
    public void Cancel_ВзводитТокенИОтменяетКомандуСессии()
    {
        var session = new FakeDbSession();
        var query = MakeQuery(session);

        query.Cancel();

        Assert.True(query.Cancellation.IsCancellationRequested);
        Assert.Equal(1, session.CancelRunningCalls);
    }

    [Fact]
    public void MarkFinished_ПереводитВЗавершённое()
    {
        var query = MakeQuery();

        Assert.False(query.IsFinished);
        query.MarkFinished();
        Assert.True(query.IsFinished);
    }

    [Fact]
    public void CleanupExpired_УдаляетЗавершённые_ОставляетСвежиеАктивные()
    {
        var registry = new ExecutionRegistry();
        var active = MakeQuery();
        var finished = MakeQuery();
        registry.Register(active);
        registry.Register(finished);
        finished.MarkFinished();

        var removed = registry.CleanupExpired(TimeSpan.FromMinutes(10));

        Assert.Equal(1, removed);
        Assert.True(registry.TryGet(active.ExecutionId, out _));
        Assert.False(registry.TryGet(finished.ExecutionId, out _));
    }

    [Fact]
    public void CleanupExpired_НулевойВозраст_УдаляетВсёСОтменой()
    {
        var registry = new ExecutionRegistry();
        var session = new FakeDbSession();
        var query = MakeQuery(session);
        registry.Register(query);

        var removed = registry.CleanupExpired(TimeSpan.Zero);

        Assert.Equal(1, removed);
        Assert.Equal(0, registry.Count);
        Assert.Equal(1, session.CancelRunningCalls); // брошенное выполнение отменено
    }

    [Fact]
    public void ExecutionId_УникаленДляКаждогоВыполнения()
    {
        var a = MakeQuery();
        var b = MakeQuery();

        Assert.NotEqual(a.ExecutionId, b.ExecutionId);
    }
}
