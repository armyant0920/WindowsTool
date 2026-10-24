using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using WindowsTool.App.Common;
using WindowsTool.App.Search;
using WindowsTool.Core.Search;

namespace WindowsTool.App.Endpoints;

/// <summary>快速搜尋 API（PLAN 9A.4）。</summary>
public static class SearchEndpoints
{
    private const int BatchSize = 1000;
    private const int MaxHitsPerRequest = 5000;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(200);

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var search = app.MapGroup("/api/search");
        search.MapPost("/", Start);
        search.MapGet("/{id}", Get);
        search.MapGet("/{id}/events", Events);
        search.MapGet("/{id}/export", Export);
        search.MapPost("/{id}/cancel", Cancel);
        search.MapDelete("/{id}", Delete);
        return app;
    }

    private static Results<Accepted<StartSearchResponse>, BadRequest<ErrorResponse>> Start(
        StartSearchRequest request, SearchManager manager)
    {
        if (request.Roots is not { Count: > 0 } || request.Roots.All(string.IsNullOrWhiteSpace))
        {
            return TypedResults.BadRequest(new ErrorResponse("請至少選擇一個搜尋位置。"));
        }

        if (!TryBuildQuery(request, out var query, out var error))
        {
            return TypedResults.BadRequest(new ErrorResponse(error));
        }

        try
        {
            var session = manager.Start(query);
            return TypedResults.Accepted($"/api/search/{session.Id}", new StartSearchResponse(session.Id));
        }
        catch (DirectoryNotFoundException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    /// <summary>目前狀態；<c>from</c> 指定時附上從該索引開始的結果（最多 5000 筆，例如重新連線時補資料）。</summary>
    private static Results<Ok<SearchEventDto>, NotFound<ErrorResponse>> Get(string id, int? from, SearchManager manager)
    {
        if (manager.Get(id) is not { } session)
        {
            return NotFound();
        }

        var start = Math.Max(0, from ?? 0);
        var hits = from is null ? [] : session.Results.GetRange(start, MaxHitsPerRequest);
        return TypedResults.Ok(session.ToEvent(start, hits));
    }

    /// <summary>SSE：每 200ms（或累積 1000 筆）送一批新結果（batch），結束時送 done 後關閉。</summary>
    private static Results<ServerSentEventsResult<SearchEventDto>, NotFound<ErrorResponse>> Events(
        string id, SearchManager manager, CancellationToken cancellationToken) =>
        manager.Get(id) is { } session
            ? TypedResults.ServerSentEvents(Stream(session, cancellationToken))
            : NotFound();

    private static async IAsyncEnumerable<SseItem<SearchEventDto>> Stream(
        SearchSession session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sent = 0;
        while (true)
        {
            var done = session.Completion.IsCompleted; // 先確認是否結束，再取結果，才不會漏掉最後幾筆
            var hits = session.Results.GetRange(sent, BatchSize);
            var from = sent;
            sent += hits.Count;
            var more = sent < session.Results.Count;

            if (done && !more)
            {
                yield return new SseItem<SearchEventDto>(session.ToEvent(from, hits), "done");
                yield break;
            }

            yield return new SseItem<SearchEventDto>(session.ToEvent(from, hits), "batch");

            if (!more)
            {
                await Task.WhenAny(session.Completion, Task.Delay(BatchInterval, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static Results<FileContentHttpResult, NotFound<ErrorResponse>> Export(string id, SearchManager manager)
    {
        if (manager.Get(id) is not { } session)
        {
            return NotFound();
        }

        var csv = SearchCsv.Build(session.Results.GetRange(0));
        var fileName = $"WindowsTool-search-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv";
        return TypedResults.File(csv, "text/csv; charset=utf-8", fileName);
    }

    /// <summary>停止搜尋但保留已找到的結果（之後仍可顯示、開啟、匯出）。</summary>
    private static Results<NoContent, NotFound<ErrorResponse>> Cancel(string id, SearchManager manager)
    {
        if (manager.Get(id) is not { } session)
        {
            return NotFound();
        }

        session.Cancel();
        return TypedResults.NoContent();
    }

    private static Results<NoContent, NotFound<ErrorResponse>> Delete(string id, SearchManager manager) =>
        manager.Remove(id) ? TypedResults.NoContent() : NotFound();

    private static NotFound<ErrorResponse> NotFound() =>
        TypedResults.NotFound(new ErrorResponse("找不到這次搜尋，可能已經被清除。"));

    private static bool TryBuildQuery(StartSearchRequest request, out SearchQuery query, out string error)
    {
        query = null!;
        error = "";

        NameMatchMode mode;
        switch (request.Mode?.ToLowerInvariant())
        {
            case null or "" or "auto": mode = NameMatchMode.Auto; break;
            case "regex": mode = NameMatchMode.Regex; break;
            default: error = $"不支援的比對方式：{request.Mode}"; return false;
        }

        SearchKind kind;
        switch (request.Kind?.ToLowerInvariant())
        {
            case null or "" or "any": kind = SearchKind.Any; break;
            case "file": kind = SearchKind.File; break;
            case "dir": kind = SearchKind.Dir; break;
            default: error = $"不支援的類型：{request.Kind}"; return false;
        }

        if (request.MinSize is < 0 || request.MaxSize is < 0 || request.MinSize > request.MaxSize)
        {
            error = "大小範圍不正確。";
            return false;
        }

        if (request.ModifiedAfter > request.ModifiedBefore)
        {
            error = "修改日期範圍不正確：開始日期晚於結束日期。";
            return false;
        }

        if (request.MaxDepth is < 1)
        {
            error = "深度上限至少為 1。";
            return false;
        }

        var defaults = new SearchQuery { Roots = [] };
        query = new SearchQuery
        {
            Roots = [.. request.Roots!.Where(r => !string.IsNullOrWhiteSpace(r))],
            Pattern = request.Pattern,
            Mode = mode,
            MatchFullPath = request.MatchFullPath ?? false,
            Kind = kind,
            Extensions = request.Extensions,
            MinSize = request.MinSize,
            MaxSize = request.MaxSize,
            ModifiedAfterUtc = request.ModifiedAfter?.ToUniversalTime(),
            ModifiedBeforeUtc = request.ModifiedBefore?.ToUniversalTime(),
            IncludeHidden = request.IncludeHidden ?? defaults.IncludeHidden,
            IncludeSystem = request.IncludeSystem ?? defaults.IncludeSystem,
            MaxDepth = request.MaxDepth,
            ExcludeDirectoryNames = request.ExcludeNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray()
                ?? defaults.ExcludeDirectoryNames,
            MaxResults = request.MaxResults ?? SearchQuery.DefaultMaxResults,
        };
        return true;
    }
}
