using WindowsTool.App.Common;
using WindowsTool.Core.Search;

namespace WindowsTool.App.Search;

public enum SearchStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>一次搜尋：在背景執行，結果陸續加入 <see cref="Results"/>。</summary>
public sealed class SearchSession : IBackgroundSession
{
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private volatile SearchStatus _status = SearchStatus.Running;

    internal SearchSession(string id, SearchQuery query)
    {
        Id = id;
        Query = query;
        Results = new SearchResults(query.MaxResults);
        StartedUtc = DateTime.UtcNow;
        Completion = Task.Run(RunAsync);
    }

    public string Id { get; }

    /// <summary>已正規化、去掉重疊位置的查詢。</summary>
    public SearchQuery Query { get; }

    public SearchResults Results { get; }

    public DateTime StartedUtc { get; }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public SearchStatus Status => _status;

    public bool IsRunning => _status == SearchStatus.Running;

    public string? Error { get; private set; }

    public Task Completion { get; }

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已經結束並釋放
        }
    }

    private async Task RunAsync()
    {
        var final = SearchStatus.Failed;
        try
        {
            await FileSearcher.SearchAsync(Query, Results, _cts.Token).ConfigureAwait(false);
            final = SearchStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            final = SearchStatus.Cancelled;
        }
#pragma warning disable CA1031 // 搜尋失敗只回報錯誤，不讓程式結束
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
        }
        finally
        {
            _stopwatch.Stop();
            _status = final;
        }
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>管理進行中與最近完成的搜尋。</summary>
public sealed class SearchManager : IDisposable
{
    public const int MaxSessions = 3;

    private readonly SessionStore<SearchSession> _store = new(MaxSessions);

    /// <summary>
    /// 驗證條件後開始搜尋。位置不存在拋出 <see cref="DirectoryNotFoundException"/>；
    /// 其他條件錯誤（例如正規表示式寫錯）拋出 <see cref="ArgumentException"/>。
    /// </summary>
    public SearchSession Start(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 先在這裡驗證，錯誤才能直接回給使用者，而不是變成背景工作的失敗
        var roots = FileSearcher.PrepareRoots(query.Roots);
        _ = NameMatcher.Create(query.Pattern, query.Mode);

        var normalized = query with
        {
            Roots = roots,
            MaxResults = Math.Clamp(query.MaxResults, 1, SearchQuery.MaxAllowedResults),
        };

        return _store.Add(id => new SearchSession(id, normalized));
    }

    public SearchSession? Get(string id) => _store.Get(id);

    public bool Remove(string id) => _store.Remove(id);

    public void Dispose() => _store.Dispose();
}
