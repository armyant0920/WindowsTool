using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using WindowsTool.App.Common;
using WindowsTool.App.Scans;
using WindowsTool.Core.Rules;
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
    int? Limit,
    string? Risk,
    string? Category,
    string? Source);

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
        scans.MapGet("/{id}/categories", Categories);
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
        string id, [AsParameters] ItemsQueryParameters p, ScanManager manager, RuleStore rules)
    {
        if (!TryGetResult(id, manager, out var session, out var result, out var notFound, out var conflict))
        {
            return notFound is not null ? notFound : conflict!;
        }

        var evaluation = session.GetEvaluation(rules.Current);
        if (!TryBuildQuery(p, evaluation, out var query, out var error))
        {
            return TypedResults.BadRequest(new ErrorResponse(error));
        }

        try
        {
            var page = ScanQuery.Run(result, query, DateTime.UtcNow);
            var parent = query.ParentId is { } parentId ? result.Find(parentId) as DirNode : result.Root;
            return TypedResults.Ok(new ItemPageDto(
                parent?.ToDto(evaluation),
                [.. page.Items.Select(i => i.ToDto(evaluation))],
                page.Total,
                query.Flat ? null : parent?.OtherFiles()));
        }
        catch (KeyNotFoundException ex)
        {
            return TypedResults.NotFound(new ErrorResponse(ex.Message));
        }
    }

    private static Results<Ok<ItemDetailDto>, NotFound<ErrorResponse>, Conflict<ErrorResponse>> Item(
        string id, int itemId, ScanManager manager, RuleStore rules)
    {
        if (!TryGetResult(id, manager, out var session, out var result, out var notFound, out var conflict))
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

        var snapshot = rules.Current;
        var evaluation = session.GetEvaluation(snapshot);
        var hits = RuleEngine.Explain(result, snapshot.Set, new FileSystemEntryProbe(), DateTime.UtcNow, itemId)
            .Select(h => new RuleHitDto(h.Rule.Id, h.Rule.Name, h.Rule.Kind, h.Rule.Source, h.Inherited, h.Role))
            .OrderBy(h => h.Role)
            .ToList();

        return TypedResults.Ok(new ItemDetailDto(node.ToDto(evaluation), ancestors, extStats, dir?.OtherFiles(), hits));
    }

    private static Results<Ok<CategoriesDto>, NotFound<ErrorResponse>, Conflict<ErrorResponse>> Categories(
        string id, ScanManager manager, RuleStore rules)
    {
        if (!TryGetResult(id, manager, out var session, out _, out var notFound, out var conflict))
        {
            return notFound is not null ? notFound : conflict!;
        }

        var snapshot = rules.Current;
        var evaluation = session.GetEvaluation(snapshot)!;
        return TypedResults.Ok(new CategoriesDto(snapshot.Version, (long)evaluation.Duration.TotalMilliseconds, evaluation.Categories));
    }

    private static bool TryGetResult(
        string id,
        ScanManager manager,
        out ScanSession session,
        out ScanResult result,
        out NotFound<ErrorResponse>? notFound,
        out Conflict<ErrorResponse>? conflict)
    {
        session = null!;
        result = null!;
        notFound = null;
        conflict = null;

        if (manager.Get(id) is not { } found)
        {
            notFound = TypedResults.NotFound(new ErrorResponse("找不到這次掃描，可能已經被清除。"));
            return false;
        }

        if (found.Status != ScanStatus.Completed || found.Result is null)
        {
            conflict = TypedResults.Conflict(new ErrorResponse("掃描尚未完成。"));
            return false;
        }

        session = found;
        result = found.Result;
        return true;
    }

    private static bool TryBuildQuery(ItemsQueryParameters p, RuleEvaluation? evaluation, out ItemQuery query, out string error)
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

        if (!TryBuildRuleFilter(p, evaluation, out var where, out error))
        {
            return false;
        }

        query = new ItemQuery
        {
            Where = where,
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

    /// <summary>依規則標註篩選：risk（可多選，unknown = 未分類）、category、source（none = 未分類）。</summary>
    private static bool TryBuildRuleFilter(
        ItemsQueryParameters p, RuleEvaluation? evaluation, out Func<ScanNode, bool>? where, out string error)
    {
        where = null;
        error = "";

        HashSet<RiskLevel>? risks = null;
        if (!string.IsNullOrWhiteSpace(p.Risk))
        {
            risks = [];
            foreach (var value in p.Risk.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                RiskLevel? risk = value.ToLowerInvariant() switch
                {
                    "unknown" => RiskLevel.Unknown,
                    "low" => RiskLevel.Low,
                    "medium" => RiskLevel.Medium,
                    "high" => RiskLevel.High,
                    "protected" => RiskLevel.Protected,
                    _ => null,
                };
                if (risk is null)
                {
                    error = $"不支援的 risk：{value}";
                    return false;
                }

                risks.Add(risk.Value);
            }
        }

        RuleSource? source = null;
        var unclassifiedOnly = false;
        switch (p.Source?.ToLowerInvariant())
        {
            case null or "": break;
            case "builtin": source = RuleSource.Builtin; break;
            case "user": source = RuleSource.User; break;
            case "ai": source = RuleSource.Ai; break;
            case "none": unclassifiedOnly = true; break;
            default: error = $"不支援的 source：{p.Source}"; return false;
        }

        var category = string.IsNullOrWhiteSpace(p.Category) ? null : p.Category.Trim();
        if (risks is null && source is null && !unclassifiedOnly && category is null)
        {
            return true;
        }

        where = node =>
        {
            var c = evaluation?.Get(node.Id);
            if (risks is not null && !risks.Contains(c?.Risk ?? RiskLevel.Unknown))
            {
                return false;
            }

            if (unclassifiedOnly && c is not null)
            {
                return false;
            }

            if (source is not null && c?.Source != source)
            {
                return false;
            }

            return category is null || c?.Category == category;
        };
        return true;
    }
}
