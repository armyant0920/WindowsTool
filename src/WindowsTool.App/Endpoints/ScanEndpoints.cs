using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using WindowsTool.App.Common;
using WindowsTool.App.Scans;
using WindowsTool.Core.Scanning;

namespace WindowsTool.App.Endpoints;

/// <summary>清單查詢參數（PLAN 11.3：GET /api/scans/{id}/items）。</summary>
public readonly record struct ItemsQueryParameters(
    int? ParentId,
    bool? Flat,
    string? Kind,
    long? MinSize,
    string? Q,
    string? Ext,
    int? OlderThanDays,
    string? Sort,
    string? Order,
    int? Offset,
    int? Limit);

public static class ScanEndpoints
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public static IEndpointRouteBuilder MapScanEndpoints(this IEndpointRouteBuilder app)
    {
        var scans = app.MapGroup("/api/scans");
        scans.MapPost("/", Start);
        scans.MapGet("/{id}", GetStatus);
        scans.MapGet("/{id}/events", Events);
        scans.MapDelete("/{id}", Delete);
        scans.MapGet("/{id}/items", Items);
        scans.MapGet("/{id}/items/{itemId:int}", Item);
        return app;
    }

    private static Results<Accepted<StartScanResponse>, BadRequest<ErrorResponse>> Start(
        StartScanRequest request, ScanManager manager)
    {
        if (string.IsNullOrWhiteSpace(request.Root))
        {
            return TypedResults.BadRequest(new ErrorResponse("請輸入要掃描的位置。"));
        }

        var defaults = new ScanOptions();
        var options = new ScanOptions
        {
            IncludeHidden = request.IncludeHidden ?? defaults.IncludeHidden,
            IncludeSystem = request.IncludeSystem ?? defaults.IncludeSystem,
            ExcludeDirectoryNames = request.ExcludeNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray()
                ?? defaults.ExcludeDirectoryNames,
            KeepFileThreshold = request.KeepFileThreshold is > 0 ? request.KeepFileThreshold.Value : defaults.KeepFileThreshold,
        };

        try
        {
            var session = manager.Start(request.Root, options);
            return TypedResults.Accepted($"/api/scans/{session.Id}", new StartScanResponse(session.Id));
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException
                                       or NotSupportedException or UnauthorizedAccessException)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex is DirectoryNotFoundException
                ? ex.Message
                : $"無法使用這個位置：{ex.Message}"));
        }
    }

    private static Results<Ok<ScanStatusDto>, NotFound> GetStatus(string id, ScanManager manager) =>
        manager.Get(id) is { } session ? TypedResults.Ok(session.ToDto()) : TypedResults.NotFound();

    /// <summary>SSE：執行中每 250ms 送一次 progress，結束時送 done 後關閉。</summary>
    private static Results<ServerSentEventsResult<ScanStatusDto>, NotFound> Events(
        string id, ScanManager manager, CancellationToken cancellationToken) =>
        manager.Get(id) is { } session
            ? TypedResults.ServerSentEvents(Stream(session, cancellationToken))
            : TypedResults.NotFound();

    private static async IAsyncEnumerable<SseItem<ScanStatusDto>> Stream(
        ScanSession session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var done = session.Completion.IsCompleted;
            yield return new SseItem<ScanStatusDto>(session.ToDto(), done ? "done" : "progress");
            if (done)
            {
                yield break;
            }

            await Task.WhenAny(session.Completion, Task.Delay(ProgressInterval, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static Results<NoContent, NotFound> Delete(string id, ScanManager manager) =>
        manager.Remove(id) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static Results<Ok<ItemPageDto>, NotFound<ErrorResponse>, Conflict<ErrorResponse>, BadRequest<ErrorResponse>> Items(
        string id, [AsParameters] ItemsQueryParameters p, ScanManager manager)
    {
        if (!TryGetResult(id, manager, out var result, out var notFound, out var conflict))
        {
            return notFound is not null ? notFound : conflict!;
        }

        if (!TryBuildQuery(p, out var query, out var error))
        {
            return TypedResults.BadRequest(new ErrorResponse(error));
        }

        try
        {
            var page = ScanQuery.Run(result, query, DateTime.UtcNow);
            var parent = query.ParentId is { } parentId ? result.Find(parentId) as DirNode : result.Root;
            return TypedResults.Ok(new ItemPageDto(
                parent?.ToDto(),
                [.. page.Items.Select(i => i.ToDto())],
                page.Total,
                query.Flat ? null : parent?.OtherFiles()));
        }
        catch (KeyNotFoundException ex)
        {
            return TypedResults.NotFound(new ErrorResponse(ex.Message));
        }
    }

    private static Results<Ok<ItemDetailDto>, NotFound<ErrorResponse>, Conflict<ErrorResponse>> Item(
        string id, int itemId, ScanManager manager)
    {
        if (!TryGetResult(id, manager, out var result, out var notFound, out var conflict))
        {
            return notFound is not null ? notFound : conflict!;
        }

        if (result.Find(itemId) is not { } node)
        {
            return TypedResults.NotFound(new ErrorResponse($"找不到項目：{itemId}"));
        }

        var ancestors = new List<AncestorDto>();
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            ancestors.Insert(0, new AncestorDto(current.Id, current.Name));
        }

        var dir = node as DirNode;
        var extStats = dir is null
            ? []
            : ScanResult.GetSubtreeExtStats(dir).Select(e => new ExtStatDto(e.Ext, e.Count, e.Size)).ToList();

        return TypedResults.Ok(new ItemDetailDto(node.ToDto(), ancestors, extStats, dir?.OtherFiles()));
    }

    private static bool TryGetResult(
        string id,
        ScanManager manager,
        out ScanResult result,
        out NotFound<ErrorResponse>? notFound,
        out Conflict<ErrorResponse>? conflict)
    {
        result = null!;
        notFound = null;
        conflict = null;

        if (manager.Get(id) is not { } session)
        {
            notFound = TypedResults.NotFound(new ErrorResponse("找不到這次掃描，可能已經被清除。"));
            return false;
        }

        if (session.Status != ScanStatus.Completed || session.Result is null)
        {
            conflict = TypedResults.Conflict(new ErrorResponse("掃描尚未完成。"));
            return false;
        }

        result = session.Result;
        return true;
    }

    private static bool TryBuildQuery(ItemsQueryParameters p, out ItemQuery query, out string error)
    {
        query = null!;
        error = "";

        ItemKindFilter kind;
        switch (p.Kind?.ToLowerInvariant())
        {
            case null or "" or "any": kind = ItemKindFilter.Any; break;
            case "dir": kind = ItemKindFilter.Dir; break;
            case "file": kind = ItemKindFilter.File; break;
            default: error = $"不支援的 kind：{p.Kind}"; return false;
        }

        ItemSort sort;
        switch (p.Sort?.ToLowerInvariant())
        {
            case null or "" or "size": sort = ItemSort.Size; break;
            case "name": sort = ItemSort.Name; break;
            case "modified": sort = ItemSort.Modified; break;
            case "files": sort = ItemSort.Files; break;
            default: error = $"不支援的 sort：{p.Sort}"; return false;
        }

        bool descending;
        switch (p.Order?.ToLowerInvariant())
        {
            case null or "":
                descending = sort != ItemSort.Name;
                break;
            case "desc": descending = true; break;
            case "asc": descending = false; break;
            default: error = $"不支援的 order：{p.Order}"; return false;
        }

        query = new ItemQuery
        {
            ParentId = p.ParentId,
            Flat = p.Flat ?? false,
            Kind = kind,
            MinSize = p.MinSize,
            NameContains = p.Q,
            Extensions = string.IsNullOrWhiteSpace(p.Ext)
                ? null
                : p.Ext.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            OlderThanDays = p.OlderThanDays,
            Sort = sort,
            Descending = descending,
            Offset = p.Offset ?? 0,
            Limit = p.Limit ?? 200,
        };

        return true;
    }
}
