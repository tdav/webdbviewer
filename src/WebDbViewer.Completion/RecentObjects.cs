namespace WebDbViewer.Completion;

/// <summary>
/// Таблицы, с которыми пользователь работал последними, — по одному списку на датасорс.
/// Нужны, чтобы в схеме из сотен таблиц наверх списка попадали те, что реально в работе,
/// а не первые по алфавиту. Живёт в памяти процесса: история подсказок переживать
/// перезапуск не обязана.
/// </summary>
internal sealed class RecentObjects
{
    /// <summary>Сколько таблиц помнить на датасорс. Больше — и «недавнее» перестаёт что-либо значить.</summary>
    private const int Capacity = 20;

    /// <summary>Насколько поднимается таблица из истории. Ровно на один разряд приоритета.</summary>
    public const int Boost = 2;

    private readonly Dictionary<Guid, LinkedList<string>> _byDataSource = [];
    private readonly Lock _lock = new();

    /// <summary>Отмечает таблицы как использованные: свежие уходят в начало списка.</summary>
    public void Touch(Guid dataSourceId, IEnumerable<string> tableNames)
    {
        lock (_lock)
        {
            if (!_byDataSource.TryGetValue(dataSourceId, out var list))
            {
                list = new LinkedList<string>();
                _byDataSource[dataSourceId] = list;
            }

            foreach (var raw in tableNames)
            {
                var name = Normalize(raw);
                if (name.Length == 0)
                    continue;

                var existing = Find(list, name);
                if (existing is not null)
                    list.Remove(existing);
                list.AddFirst(name);

                while (list.Count > Capacity)
                    list.RemoveLast();
            }
        }
    }

    /// <summary>Использовалась ли таблица недавно.</summary>
    public bool IsRecent(Guid dataSourceId, string tableName)
    {
        var name = Normalize(tableName);
        if (name.Length == 0)
            return false;
        lock (_lock)
        {
            return _byDataSource.TryGetValue(dataSourceId, out var list) && Find(list, name) is not null;
        }
    }

    private static LinkedListNode<string>? Find(LinkedList<string> list, string name)
    {
        for (var node = list.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value, name, StringComparison.OrdinalIgnoreCase))
                return node;
        }
        return null;
    }

    /// <summary>Схема и кавычки отбрасываются: история ведётся по короткому имени таблицы.</summary>
    private static string Normalize(string raw)
    {
        var name = raw.Trim().Trim('"');
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name[(dot + 1)..].Trim('"');
        return name;
    }
}
