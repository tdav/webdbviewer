namespace WebDbViewer.Metadata.Search;

/// <summary>
/// Префиксное дерево (trie) для быстрого поиска объектов по началу имени.
/// Ключи нормализуются к нижнему регистру (учёт регистра — OrdinalIgnoreCase),
/// оригинальные элементы сохраняются как есть.
/// Построение — однопоточное; после построения структура неизменяема и безопасна для чтения из многих потоков.
/// </summary>
public sealed class PrefixTrie<T>
{
    private sealed class Node
    {
        public Dictionary<char, Node>? Children;
        public List<T>? Items;
    }

    private readonly Node _root = new();

    /// <summary>Добавляет элемент по ключу (ключ приводится к нижнему регистру).</summary>
    public void Add(string key, T item)
    {
        ArgumentNullException.ThrowIfNull(key);
        var node = _root;
        foreach (var raw in key)
        {
            var ch = char.ToLowerInvariant(raw);
            node.Children ??= new Dictionary<char, Node>();
            if (!node.Children.TryGetValue(ch, out var next))
            {
                next = new Node();
                node.Children[ch] = next;
            }
            node = next;
        }
        node.Items ??= [];
        node.Items.Add(item);
    }

    /// <summary>Возвращает все элементы, ключ которых начинается с указанного префикса (без учёта регистра).</summary>
    public IEnumerable<T> GetByPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var node = _root;
        foreach (var raw in prefix)
        {
            var ch = char.ToLowerInvariant(raw);
            if (node.Children is null || !node.Children.TryGetValue(ch, out var next))
                yield break;
            node = next;
        }
        // Обход поддерева: все элементы под узлом префикса
        var stack = new Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Items is not null)
            {
                foreach (var item in current.Items)
                    yield return item;
            }
            if (current.Children is not null)
            {
                foreach (var child in current.Children.Values)
                    stack.Push(child);
            }
        }
    }
}
