using WindowsTool.Core.Execution;

namespace WindowsTool.App.Plans;

/// <summary>
/// 從掃描結果建立執行計畫。勾選 = （<c>useAutoSelect</c> 時所有預設勾選的項目）＋ <c>include</c> − <c>exclude</c>。
/// <c>action</c>：<c>suggested</c>（依規則建議，預設）/ <c>recycle</c> / <c>quarantine</c> / <c>move</c> / <c>delete</c>。
/// </summary>
public sealed record CreatePlanRequest(
    string? ScanId,
    bool? UseAutoSelect = null,
    IReadOnlyList<int>? Include = null,
    IReadOnlyList<int>? Exclude = null,
    string? Action = null,
    string? Destination = null);

public sealed record ExecuteRequest(string? Confirmation = null);

public sealed record ExecutionTotals(int Ok, int Failed, int Skipped, long Bytes);

public sealed record ExecutionDto(
    string Status,
    int Done,
    int Total,
    string? CurrentPath,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    ExecutionTotals Totals,
    IReadOnlyList<ItemResult> Results,
    string? Error);

public sealed record PlanResponse(Plan Plan, ExecutionDto? Execution);

public sealed record LogsResponse(string Directory, IReadOnlyList<ActionLogEntry> Entries);

public sealed record QuarantineResponse(string Root, long TotalBytes, IReadOnlyList<QuarantineEntry> Entries);

public sealed record PurgeRequest(int? OlderThanDays, string? Confirmation);
