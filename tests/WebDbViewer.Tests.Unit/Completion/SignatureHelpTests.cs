using WebDbViewer.Completion;
using WebDbViewer.Completion.Semantics;
using WebDbViewer.Core;

namespace WebDbViewer.Tests.Unit.Completion;

/// <summary>Разбор вызова под кареткой и подсказка его сигнатуры.</summary>
public class SignatureHelpTests
{
    private readonly CompletionEngine _engine = new(new FakeMetadataCache());

    private async Task<SignatureInfo?> DescribeAsync(string sqlWithCaret, DbKind dialect = DbKind.Postgres)
    {
        var caret = sqlWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "В тестовом SQL нет маркера каретки «|»");
        var sql = sqlWithCaret.Remove(caret, 1);

        return await _engine.DescribeSignatureAsync(new CompletionRequest
        {
            DataSourceId = FakeMetadataCache.DsId,
            SqlText = sql,
            CaretOffset = caret,
            DefaultSchema = "public",
        }, dialect, CancellationToken.None);
    }

    // ================================================================== Разбор вызова

    [Fact]
    public void РазборВызова_ИмяИНомерАргумента()
    {
        Assert.Equal(("round", 0), CaretText.EnclosingCall("SELECT round("));
        Assert.Equal(("round", 1), CaretText.EnclosingCall("SELECT round(x, "));
        Assert.Equal(("round", 2), CaretText.EnclosingCall("SELECT round(x, y, "));
    }

    [Fact]
    public void РазборВызова_ВложенныеСкобкиНеСбиваютНомерАргумента()
    {
        Assert.Equal(("coalesce", 1), CaretText.EnclosingCall("SELECT coalesce(min(a, b), "));
    }

    [Fact]
    public void РазборВызова_ЗакрытыйВызов_НеСчитается()
    {
        Assert.Null(CaretText.EnclosingCall("SELECT round(x) "));
    }

    [Fact]
    public void РазборВызова_СкобкаБезИмени_ЭтоГруппировкаАНеВызов()
    {
        Assert.Null(CaretText.EnclosingCall("SELECT (a + "));
    }

    [Fact]
    public void РазборВызова_ВнеСкобок_НичегоНеВозвращает()
    {
        Assert.Null(CaretText.EnclosingCall("SELECT id FROM users"));
    }

    [Fact]
    public void ОператорПриведенияТипа_РаспознаётсяТолькоДляДвухДвоеточий()
    {
        Assert.True(CaretText.EndsWithCastOperator("id::"));
        Assert.True(CaretText.EndsWithCastOperator("id:: "));
        Assert.False(CaretText.EndsWithCastOperator("id:"));
        Assert.False(CaretText.EndsWithCastOperator("id"));
    }

    // ================================================================== Сигнатуры

    [Fact]
    public async Task ФункцияСхемы_ОтдаётСигнатуруИзКэшаМетаданных()
    {
        var signature = await DescribeAsync("SELECT calc_total(|) FROM orders");

        Assert.NotNull(signature);
        Assert.Equal("public.calc_total(order_id bigint)", signature.Label);
        Assert.Contains("Сумма заказа", signature.Documentation);
        Assert.Contains("numeric", signature.Documentation);
        Assert.Equal(0, signature.ActiveParameter);
    }

    [Fact]
    public async Task ВстроеннаяФункция_ОтдаётСигнатуруИзСправочникаДиалекта()
    {
        var signature = await DescribeAsync("SELECT NVL(a, |) FROM DUAL", DbKind.Oracle);

        Assert.NotNull(signature);
        Assert.Equal("NVL(expr, replacement)", signature.Label);
        Assert.Equal(1, signature.ActiveParameter);
    }

    [Fact]
    public async Task ПакетнаяПодпрограмма_НаходитсяПоКороткомуИмени()
    {
        var signature = await DescribeAsync("BEGIN DBMS_OUTPUT.PUT_LINE(|); END;", DbKind.Oracle);

        Assert.NotNull(signature);
        Assert.Equal("DBMS_OUTPUT.PUT_LINE(text)", signature.Label);
    }

    [Fact]
    public async Task ФункцияЧужогоДиалекта_НеНаходится()
    {
        var signature = await DescribeAsync("SELECT NVL(a, |) FROM users");

        Assert.Null(signature);
    }

    [Fact]
    public async Task КареткаВнеВызова_ПодсказкиНет()
    {
        Assert.Null(await DescribeAsync("SELECT | FROM users"));
    }

    [Fact]
    public async Task НеизвестнаяФункция_ПодсказкиНет()
    {
        Assert.Null(await DescribeAsync("SELECT no_such_function(|) FROM users"));
    }
}
