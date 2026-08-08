using WebDbViewer.Web.Pages;

namespace WebDbViewer.Tests.Unit.Web;

/// <summary>Тесты кодирования пути узла дерева навигатора.</summary>
public sealed class TreePathTests
{
    [Fact]
    public void SplitPath_Empty_ReturnsNoSegments()
    {
        Assert.Empty(TreeModel.SplitPath(null));
        Assert.Empty(TreeModel.SplitPath(""));
    }

    [Fact]
    public void AppendSegment_ThenSplit_RoundTrips()
    {
        var path = TreeModel.AppendSegment(null, "public");
        path = TreeModel.AppendSegment(path, "Таблицы");
        path = TreeModel.AppendSegment(path, "заказы/2024"); // имя со слэшем

        var segments = TreeModel.SplitPath(path);

        Assert.Equal(["public", "Таблицы", "заказы/2024"], segments);
    }
}
