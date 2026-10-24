using System.Globalization;
using System.Text;
using WindowsTool.Core.Search;

namespace WindowsTool.App.Search;

public sealed record StartSearchRequest(
    IReadOnlyList<string>? Roots,
    string? Pattern = null,
    string? Mode = null,
    bool? MatchFullPath = null,
    string? Kind = null,
    IReadOnlyList<string>? Extensions = null,
    long? MinSize = null,
    long? MaxSize = null,
    DateTime? ModifiedAfter = null,
    DateTime? ModifiedBefore = null,
    bool? IncludeHidden = null,
    bool? IncludeSystem = null,
    int? MaxDepth = null,
    IReadOnlyList<string>? ExcludeNames = null,
    int? MaxResults = null);

public sealed record StartSearchResponse(string SearchId);

public sealed record SearchHitDto(
    int Index,
    string Name,
    string Path,
    string Directory,
    string Kind,
    long Size,
    DateTime LastWriteUtc,
    IReadOnlyList<string> Flags);

public sealed record SearchProgressDto(long Dirs, int Matches, long Denied, string? CurrentPath);

/// <summary>搜尋狀態；SSE 的每個事件也是這個格式（<see cref="Hits"/> 只含新的結果）。</summary>
public sealed record SearchEventDto(
    string SearchId,
    string Status,
    IReadOnlyList<string> Roots,
    long ElapsedMs,
    SearchProgressDto Progress,
    bool Truncated,
    int MaxResults,
    IReadOnlyList<SearchHitDto> Hits,
    IReadOnlyList<string>? DeniedPaths,
    string? Error);

public static class SearchDtoMapper
{
    public static string ToApi(this SearchStatus status) => status switch
    {
        SearchStatus.Running => "running",
        SearchStatus.Completed => "completed",
        SearchStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    /// <summary>建立狀態事件，附上從 <paramref name="from"/> 開始的結果。</summary>
    public static SearchEventDto ToEvent(this SearchSession session, int from, IReadOnlyList<SearchHit> hits)
    {
        var status = session.Status; // 先讀狀態（volatile）
        var progress = session.Results.Progress();
        var done = status != SearchStatus.Running;

        return new SearchEventDto(
            session.Id,
            status.ToApi(),
            session.Query.Roots,
            (long)session.Elapsed.TotalMilliseconds,
            new SearchProgressDto(progress.Dirs, progress.Matches, progress.Denied, progress.CurrentPath),
            session.Results.Truncated,
            session.Results.MaxResults,
            [.. hits.Select((hit, i) => hit.ToDto(from + i))],
            done ? session.Results.DeniedPaths : null,
            session.Error);
    }

    public static SearchHitDto ToDto(this SearchHit hit, int index) => new(
        index,
        hit.Name,
        hit.Path,
        hit.Directory,
        hit.Kind switch
        {
            SearchHitKind.Dir => "dir",
            SearchHitKind.Link => "link",
            _ => "file",
        },
        hit.Size,
        hit.LastWriteUtc,
        FlagNames(hit.Flags));

    private static string[] FlagNames(SearchHitFlags flags)
    {
        if (flags == SearchHitFlags.None)
        {
            return [];
        }

        var names = new List<string>(2);
        if ((flags & SearchHitFlags.CloudPlaceholder) != 0)
        {
            names.Add("cloudPlaceholder");
        }

        if ((flags & SearchHitFlags.Hidden) != 0)
        {
            names.Add("hidden");
        }

        if ((flags & SearchHitFlags.System) != 0)
        {
            names.Add("system");
        }

        return [.. names];
    }
}

/// <summary>匯出 CSV（UTF-8 含 BOM，Excel 才能正確顯示中文）。</summary>
public static class SearchCsv
{
    public static byte[] Build(IReadOnlyList<SearchHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var builder = new StringBuilder();
        builder.Append("名稱,所在資料夾,完整路徑,類型,大小（位元組）,修改時間\r\n");
        foreach (var hit in hits)
        {
            Append(builder, hit.Name).Append(',');
            Append(builder, hit.Directory).Append(',');
            Append(builder, hit.Path).Append(',');
            builder.Append(hit.Kind switch
            {
                SearchHitKind.Dir => "資料夾",
                SearchHitKind.Link => "連結",
                _ => "檔案",
            }).Append(',');
            builder.Append(hit.Kind == SearchHitKind.File ? hit.Size.ToString(CultureInfo.InvariantCulture) : "").Append(',');
            builder.Append(hit.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            builder.Append("\r\n");
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(builder.ToString());
        return [.. preamble, .. body];
    }

    /// <summary>
    /// CSV 跳脫，並防止公式注入：以 = + - @ 開頭的值前面加上 '，避免 Excel 把檔名當成公式執行。
    /// </summary>
    private static StringBuilder Append(StringBuilder builder, string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        if (value.AsSpan().IndexOfAny(",\"\r\n") >= 0)
        {
            return builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        return builder.Append(value);
    }
}
