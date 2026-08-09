namespace WebDbViewer.Completion;

/// <summary>
/// Настройки автодополнения v1. Передаются per-request (например, из query-параметра API
/// «autoAlias»); при null используются значения по умолчанию (обратная совместимость с v0).
/// </summary>
public sealed record CompletionOptions
{
    public static readonly CompletionOptions Default = new();

    /// <summary>Автоалиас при вставке имени таблицы после FROM/JOIN: «users» → «users u».</summary>
    public bool AutoAliasTables { get; init; }
}

/// <summary>
/// Расширенный контракт движка автодополнения (v1): то же, что <see cref="Core.ICompletionEngine"/>,
/// плюс перегрузка с настройками. Регистрируется в DI тем же синглтоном.
/// </summary>
public interface ISemanticCompletionEngine : Core.ICompletionEngine
{
    Task<IReadOnlyList<Core.CompletionItem>> CompleteAsync(
        Core.CompletionRequest request, Core.DbKind dialect, CompletionOptions? options, CancellationToken ct);

    /// <summary>Сигнатура функции, внутри скобок которой стоит каретка; null — вызова там нет.</summary>
    Task<SignatureInfo?> DescribeSignatureAsync(
        Core.CompletionRequest request, Core.DbKind dialect, CancellationToken ct);
}
