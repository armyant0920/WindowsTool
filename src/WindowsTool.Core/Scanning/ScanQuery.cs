namespace WindowsTool.Core.Scanning;

public enum ItemKindFilter
{
    Any,
    Dir,
    File,
}

public enum ItemSort
{
    Size,
    Name,
    Modified,
    Files,
}

/// <summary>結果清單的查詢條件。</summary>
public sealed record ItemQuery
{
    /// <summary>列出這個目錄的直接子項目；Flat = true 時代表搜尋範圍（null = 整個掃描結果）。</summary>
    public int? ParentId { get; init; }

    /// <summary>true = 列出範圍內所有子孫（例如「整個結果中最大的檔案」）。</summary>
    public bool Flat { get; init; }

    public ItemKindFilter Kind { get; init; } = ItemKindFilter.Any;

    public long? MinSize { get; init; }

    /// <summary>名稱包含（不分大小寫）。</summary>
    public string? NameContains { get; init; }

    /// <summary>副檔名（含或不含點都可以），只套用在檔案上。</summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>超過 N 天沒有修改（目錄以子樹中最新的檔案為準）。</summary>
    public int? OlderThanDays { get; init; }

    /// <summary>額外的條件（例如依規則標註的風險、類別篩選）。</summary>
    public Func<ScanNode, bool>? Where { get; init; }

    public ItemSort Sort { get; init; } = ItemSort.Size;

    public bool Descending { get; init; } = true;

    public int Offset { get; init; }

    public int Limit { get; init; } = 200;
}

/// <param name="Matched">所有符合條件的項目（未分頁），給「全選」與勾選統計使用。</param>
public sealed record ItemPage(IReadOnlyList<ScanNode> Items, int Total, IReadOnlyList<ScanNode> Matched);

public static class ScanQuery
{
    public const int MaxLimit = 2000;

    public static ItemPage Run(ScanResult result, ItemQuery query, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(query);

        DirNode scope;
        if (query.ParentId is { } parentId)
        {
            scope = result.Find(parentId) as DirNode
                ?? throw new KeyNotFoundException($"找不到資料夾項目：{parentId}");
        }
        else
        {
            scope = result.Root;
        }

        IEnumerable<ScanNode> source = query.Flat ? Descendants(scope) : Children(scope);
        var filter = BuildFilter(query, nowUtc);
        var matched = source.Where(filter).ToList();

        // OrderBy + Skip + Take 由 LINQ 做部分排序：只需要前幾筆時不必排序全部
        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        var offset = Math.Max(0, query.Offset);
        var items = Sort(matched, query.Sort, query.Descending).Skip(offset).Take(limit).ToList();
        return new ItemPage(items, matched.Count, matched);
    }

    private static IEnumerable<ScanNode> Children(DirNode dir) => dir.Dirs.Cast<ScanNode>().Concat(dir.Files);

    private static IEnumerable<ScanNode> Descendants(DirNode dir)
    {
        var stack = new Stack<DirNode>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in current.Dirs)
            {
                yield return child;
                stack.Push(child);
            }

            foreach (var file in current.Files)
            {
                yield return file;
            }
        }
    }

    private static Func<ScanNode, bool> BuildFilter(ItemQuery query, DateTime nowUtc)
    {
        var extensions = query.Extensions is { Count: > 0 }
            ? new HashSet<string>(
                query.Extensions
                    .Select(e => e.Trim())
                    .Where(e => e.Length > 0)
                    .Select(e => e.StartsWith('.') ? e : "." + e),
                StringComparer.OrdinalIgnoreCase)
            : null;

        var name = string.IsNullOrWhiteSpace(query.NameContains) ? null : query.NameContains.Trim();
        var olderThanTicks = query.OlderThanDays is { } days ? nowUtc.AddDays(-days).Ticks : (long?)null;

        return node =>
        {
            switch (query.Kind)
            {
                case ItemKindFilter.Dir when node.Kind != ScanItemKind.Dir:
                case ItemKindFilter.File when node.Kind == ScanItemKind.Dir:
                    return false;
            }

            if (query.MinSize is { } min && node.Size < min)
            {
                return false;
            }

            if (name is not null && !node.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (extensions is not null
                && (node.Kind == ScanItemKind.Dir || !extensions.Contains(Path.GetExtension(node.Name))))
            {
                return false;
            }

            if (olderThanTicks is { } limit && node.NewestWriteTicks > limit)
            {
                return false;
            }

            return query.Where is null || query.Where(node);
        };
    }

    private static IEnumerable<ScanNode> Sort(IEnumerable<ScanNode> items, ItemSort sort, bool descending)
    {
        IOrderedEnumerable<ScanNode> ordered = sort switch
        {
            ItemSort.Name => descending
                ? items.OrderByDescending(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
                : items.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase),
            ItemSort.Modified => descending
                ? items.OrderByDescending(n => n.NewestWriteTicks)
                : items.OrderBy(n => n.NewestWriteTicks),
            ItemSort.Files => descending
                ? items.OrderByDescending(FileCount)
                : items.OrderBy(FileCount),
            _ => descending
                ? items.OrderByDescending(n => n.Size)
                : items.OrderBy(n => n.Size),
        };

        // 次要排序固定用名稱，結果才穩定
        return ordered.ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static long FileCount(ScanNode node) => node is DirNode dir ? dir.TotalFileCount : 0;
}
