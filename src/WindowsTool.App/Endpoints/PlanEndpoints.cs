using Microsoft.AspNetCore.Http.HttpResults;
using WindowsTool.App.Common;
using WindowsTool.App.Plans;
using WindowsTool.Core.Execution;

namespace WindowsTool.App.Endpoints;

/// <summary>執行計畫、操作紀錄、隔離區（PLAN 11.3）。</summary>
public static class PlanEndpoints
{
    public static IEndpointRouteBuilder MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        var plans = app.MapGroup("/api/plans");
        plans.MapPost("/", Create);
        plans.MapGet("/{id}", Get);
        plans.MapPost("/{id}/execute", Execute);
        plans.MapPost("/{id}/cancel", Cancel);

        app.MapGet("/api/logs", Logs);

        var quarantine = app.MapGroup("/api/quarantine");
        quarantine.MapGet("/", ListQuarantine);
        quarantine.MapPost("/{entryId}/restore", Restore);
        quarantine.MapPost("/purge", Purge);
        return app;
    }

    /// <summary>建立計畫並 dry-run（不會動到任何檔案）。</summary>
    private static Results<Created<PlanResponse>, BadRequest<ErrorResponse>, NotFound<ErrorResponse>> Create(
        CreatePlanRequest request, PlanManager manager, CancellationToken cancellationToken)
    {
        try
        {
            var plan = manager.Create(request, cancellationToken);
            return TypedResults.Created($"/api/plans/{plan.PlanId}", new PlanResponse(plan, null));
        }
        catch (PlanException ex) when (ex.NotFound)
        {
            return TypedResults.NotFound(new ErrorResponse(ex.Message));
        }
        catch (PlanException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static Results<Ok<PlanResponse>, NotFound<ErrorResponse>> Get(string id, PlanManager manager) =>
        manager.Get(id) is { } entry
            ? TypedResults.Ok(new PlanResponse(entry.Plan, entry.Execution?.ToDto()))
            : TypedResults.NotFound(new ErrorResponse("找不到這個計畫，可能已經被清除。"));

    /// <summary>開始執行；含有永久刪除時必須帶上確認文字。</summary>
    private static Results<Accepted<PlanResponse>, BadRequest<ErrorResponse>, NotFound<ErrorResponse>, Conflict<ErrorResponse>> Execute(
        string id, ExecuteRequest? request, PlanManager manager)
    {
        try
        {
            var execution = manager.Execute(id, request?.Confirmation);
            return TypedResults.Accepted($"/api/plans/{id}", new PlanResponse(execution.Plan, execution.ToDto()));
        }
        catch (PlanException ex) when (ex.NotFound)
        {
            return TypedResults.NotFound(new ErrorResponse(ex.Message));
        }
        catch (PlanException ex) when (ex.Message.Contains("已經執行過", StringComparison.Ordinal))
        {
            return TypedResults.Conflict(new ErrorResponse(ex.Message));
        }
        catch (PlanException ex)
        {
            return TypedResults.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    /// <summary>停止執行：正在處理的那一項完成後停止，其餘標記為略過。</summary>
    private static Results<NoContent, NotFound<ErrorResponse>> Cancel(string id, PlanManager manager)
    {
        if (manager.Get(id) is not { Execution: { } execution })
        {
            return TypedResults.NotFound(new ErrorResponse("這個計畫沒有在執行。"));
        }

        execution.Cancel();
        return TypedResults.NoContent();
    }

    private static Ok<LogsResponse> Logs(DateTime? from, DateTime? to, string? planId, int? limit, ActionLog log) =>
        TypedResults.Ok(new LogsResponse(
            log.Directory,
            log.Read(from?.ToUniversalTime(), to?.ToUniversalTime(), string.IsNullOrWhiteSpace(planId) ? null : planId, Math.Clamp(limit ?? 500, 1, 5000))));

    private static Ok<QuarantineResponse> ListQuarantine(bool? all, QuarantineStore quarantine)
    {
        var entries = quarantine.List(includeClosed: all ?? false);
        return TypedResults.Ok(new QuarantineResponse(
            quarantine.DefaultRoot,
            entries.Where(e => e.Status == QuarantineStatus.Active).Sum(e => e.Size),
            entries));
    }

    private static Results<Ok<QuarantineEntry>, BadRequest<ErrorResponse>, NotFound<ErrorResponse>> Restore(
        string entryId, QuarantineStore quarantine, ActionLog log)
    {
        try
        {
            var entry = quarantine.Restore(entryId);
            log.Append(new ActionLogEntry
            {
                Path = entry.OriginalPath,
                Action = LogAction.Restore,
                Result = ActionOutcome.Ok,
                Bytes = entry.Size,
                Undo = new UndoInfo("quarantine", entry.StoredPath, entry.OriginalPath, entry.EntryId),
                PlanId = entry.PlanId,
            });
            return TypedResults.Ok(entry);
        }
        catch (KeyNotFoundException ex)
        {
            return TypedResults.NotFound(new ErrorResponse(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Append(new ActionLogEntry { Path = entryId, Action = LogAction.Restore, Result = ActionOutcome.Failed, Error = ex.Message });
            return TypedResults.BadRequest(new ErrorResponse($"無法還原：{ex.Message}"));
        }
    }

    /// <summary>永久刪除隔離區中超過 N 天的項目，必須帶上確認文字。</summary>
    private static Results<Ok<PurgeResult>, BadRequest<ErrorResponse>> Purge(PurgeRequest request, QuarantineStore quarantine, ActionLog log)
    {
        if (request.Confirmation != Plan.PermanentDeleteConfirmation)
        {
            return TypedResults.BadRequest(new ErrorResponse($"清空隔離區會永久刪除檔案，請輸入「{Plan.PermanentDeleteConfirmation}」確認。"));
        }

        if (request.OlderThanDays is not >= 0)
        {
            return TypedResults.BadRequest(new ErrorResponse("請指定要清空幾天前的項目（0 = 全部）。"));
        }

        var result = quarantine.Purge(DateTime.UtcNow.AddDays(-request.OlderThanDays.Value), (entry, error) => log.Append(new ActionLogEntry
        {
            Path = entry.OriginalPath,
            Action = LogAction.Purge,
            Result = error is null ? ActionOutcome.Ok : ActionOutcome.Failed,
            Error = error?.Message,
            Bytes = error is null ? entry.Size : 0,
            PlanId = entry.PlanId,
        }));
        return TypedResults.Ok(result);
    }
}
