namespace WindowsTool.Core.Scanning;

public sealed class ScanResult
{
    private readonly ScanNode?[] _byId;

    internal ScanResult(
        DirNode root,
        ScanNode?[] byId,
        IReadOnlyList<ScanError> errors,
        long errorCount,
        DateTime startedUtc,
        TimeSpan duration)
    {
        Root = root;
        _byId = byId;
        Errors = errors;
        ErrorCount = errorCount;
        StartedUtc = startedUtc;
        Duration = duration;
    }

    public DirNode Root { get; }

    /// <summary>無法讀取的路徑（最多記錄 <see cref="ScanOptions.MaxDeniedPathsRecorded"/> 筆）。</summary>
    public IReadOnlyList<ScanError> Errors { get; }

    /// <summary>無法讀取的目錄總數。</summary>
    public long ErrorCount { get; }

    public DateTime StartedUtc { get; }

    public TimeSpan Duration { get; }

    /// <summary>保留下來的項目數（目錄 + 個別保留的檔案 + 連結）。</summary>
    public int ItemCount => _byId.Length - 1;

    public IEnumerable<ScanNode> Items
    {
        get
        {
            for (var i = 1; i < _byId.Length; i++)
            {
                yield return _byId[i]!;
            }
        }
    }

    public ScanNode? Find(int id) => id > 0 && id < _byId.Length ? _byId[id] : null;

    /// <summary>
    /// 整個子樹的副檔名統計（由各目錄保留的前幾名加總，數字是近似值）。
    /// </summary>
    public static IReadOnlyList<ExtStat> GetSubtreeExtStats(DirNode dir, int top = 10)
    {
        ArgumentNullException.ThrowIfNull(dir);

        var totals = new Dictionary<string, (int Count, long Size)>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<DirNode>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var stat in current.OwnExtStats)
            {
                totals.TryGetValue(stat.Ext, out var t);
                totals[stat.Ext] = (t.Count + stat.Count, t.Size + stat.Size);
            }

            foreach (var child in current.Dirs)
            {
                stack.Push(child);
            }
        }

        var sorted = totals
            .Where(kv => kv.Key != ExtStat.OtherExtension)
            .OrderByDescending(kv => kv.Value.Size)
            .Select(kv => new ExtStat(kv.Key, kv.Value.Count, kv.Value.Size))
            .ToList();

        var result = sorted.Take(top).ToList();
        var restCount = sorted.Skip(top).Sum(s => s.Count);
        var restSize = sorted.Skip(top).Sum(s => s.Size);
        if (totals.TryGetValue(ExtStat.OtherExtension, out var other))
        {
            restCount += other.Count;
            restSize += other.Size;
        }

        if (restCount > 0)
        {
            result.Add(new ExtStat(ExtStat.OtherExtension, restCount, restSize));
        }

        return result;
    }
}
