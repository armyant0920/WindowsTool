namespace WindowsTool.Core.Search;

public enum SearchHitKind
{
    File,
    Dir,
    Link,
}

[Flags]
public enum SearchHitFlags
{
    None = 0,
    Hidden = 1 << 0,
    System = 1 << 1,
    CloudPlaceholder = 1 << 2,
}

public sealed record SearchHit(
    string Path,
    string Name,
    SearchHitKind Kind,
    long Size,
    DateTime LastWriteUtc,
    SearchHitFlags Flags)
{
    /// <summary>所在資料夾。</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public readonly record struct SearchProgressSnapshot(long Dirs, int Matches, long Denied, string? CurrentPath);

/// <summary>
/// 搜尋結果的收集器，可以在搜尋進行中從任何執行緒讀取。結果只會往後附加，索引不會變動。
/// </summary>
public sealed class SearchResults(int maxResults)
{
    public const int MaxDeniedPathsRecorded = 200;

    private readonly List<SearchHit> _hits = [];
    private readonly List<string> _deniedPaths = [];
    private readonly Lock _lock = new();
    private long _dirs;
    private long _denied;
    private string? _currentPath;
    private volatile bool _truncated;

    public int MaxResults { get; } = Math.Clamp(maxResults, 1, SearchQuery.MaxAllowedResults);

    /// <summary>結果數達到上限，搜尋已提前停止。</summary>
    public bool Truncated => _truncated;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _hits.Count;
            }
        }
    }

    public SearchProgressSnapshot Progress() => new(
        Interlocked.Read(ref _dirs),
        Count,
        Interlocked.Read(ref _denied),
        Volatile.Read(ref _currentPath));

    public SearchHit? Get(int index)
    {
        lock (_lock)
        {
            return index >= 0 && index < _hits.Count ? _hits[index] : null;
        }
    }

    /// <summary>取得從 <paramref name="start"/> 開始、最多 <paramref name="max"/> 筆的結果（複本）。</summary>
    public IReadOnlyList<SearchHit> GetRange(int start, int max = int.MaxValue)
    {
        lock (_lock)
        {
            if (start >= _hits.Count)
            {
                return [];
            }

            var count = Math.Min(max, _hits.Count - start);
            return _hits.GetRange(start, count);
        }
    }

    public IReadOnlyList<string> DeniedPaths
    {
        get
        {
            lock (_lock)
            {
                return [.. _deniedPaths];
            }
        }
    }

    public long DeniedCount => Interlocked.Read(ref _denied);

    /// <summary>加入一筆結果；已達上限時回傳 false（並標記為截斷）。</summary>
    internal bool TryAdd(SearchHit hit)
    {
        lock (_lock)
        {
            if (_hits.Count >= MaxResults)
            {
                _truncated = true;
                return false;
            }

            _hits.Add(hit);
            return true;
        }
    }

    internal void DirectoryStarted(string path)
    {
        Interlocked.Increment(ref _dirs);
        Volatile.Write(ref _currentPath, path);
    }

    internal void AddDenied(string path)
    {
        Interlocked.Increment(ref _denied);
        lock (_lock)
        {
            if (_deniedPaths.Count < MaxDeniedPathsRecorded)
            {
                _deniedPaths.Add(path);
            }
        }
    }
}
