using System.Collections.Concurrent;
using WindowsTool.App.Scans;
using WindowsTool.Core.Execution;
using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;
using WindowsTool.Core.Storage;

namespace WindowsTool.App.Plans;

/// <summary>計畫建立或執行失敗（訊息可以直接顯示給使用者）。</summary>
public sealed class PlanException : Exception
{
    public PlanException()
        : base("無法建立計畫。")
    {
    }

    public PlanException(string message)
        : base(message)
    {
    }

    public PlanException(string message, bool notFound)
        : base(message)
    {
        NotFound = notFound;
    }

    public PlanException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public bool NotFound { get; }
}

/// <summary>一個計畫的執行狀態。</summary>
public sealed class PlanExecution : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ItemResult> _results = [];
    private volatile string _status = "running";
    private ExecutionProgress _progress;

    public PlanExecution(Plan plan)
    {
        Plan = plan;
        _progress = new ExecutionProgress(0, plan.Items.Count, null);
        StartedUtc = DateTime.UtcNow;
    }

    public Plan Plan { get; }

    public DateTime StartedUtc { get; }

    public DateTime? FinishedUtc { get; private set; }

    public string? Error { get; private set; }

    public Task Completion { get; private set; } = Task.CompletedTask;

    public bool IsRunning => _status == "running";

    public void Start(PlanEnvironment environment, ActionLog log, string? confirmation, Action<IReadOnlyList<ItemResult>>? onCompleted = null)
    {
        Completion = Task.Run(async () =>
        {
            var final = "failed";
            try
            {
                var results = await Executor.ExecuteAsync(Plan, environment, log, confirmation, new SyncProgress(p => _progress = p), _cts.Token)
                    .ConfigureAwait(false);
                lock (_results)
                {
                    _results.AddRange(results);
                }

                onCompleted?.Invoke(results);

                final = _cts.IsCancellationRequested ? "cancelled" : "completed";
            }
#pragma warning disable CA1031 // 執行失敗只回報錯誤
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Error = ex.Message;
            }
            finally
            {
                FinishedUtc = DateTime.UtcNow;
                _status = final;
            }
        });
    }

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

    public void Dispose() => _cts.Dispose();

    public ExecutionDto ToDto()
    {
        var status = _status;
        List<ItemResult> results;
        lock (_results)
        {
            results = [.. _results];
        }

        var progress = _progress;
        return new ExecutionDto(
            status,
            progress.Done,
            progress.Total,
            progress.CurrentPath,
            StartedUtc,
            status == "running" ? null : FinishedUtc,
            new ExecutionTotals(
                results.Count(r => r.Outcome == ActionOutcome.Ok),
                results.Count(r => r.Outcome == ActionOutcome.Failed),
                results.Count(r => r.Outcome == ActionOutcome.Skipped),
                results.Sum(r => r.Bytes)),
            results,
            Error);
    }

    /// <summary>同步回報進度（<see cref="Progress{T}"/> 會切到 thread pool，順序不保證）。</summary>
    private sealed class SyncProgress(Action<ExecutionProgress> report) : IProgress<ExecutionProgress>
    {
        public void Report(ExecutionProgress value) => report(value);
    }
}

/// <summary>建立、保存與執行計畫。保存在記憶體中（最近 20 個）。</summary>
public sealed class PlanManager(
    ScanManager scans,
    RuleStore rules,
    ITrash trash,
    QuarantineStore quarantine,
    ICommandRunner commands,
    ActionLog log)
{
    public const int MaxPlans = 20;
    public const int MaxItems = 50_000;

    private readonly ConcurrentDictionary<string, (Plan Plan, PlanExecution? Execution)> _plans = new();
    private readonly Lock _lock = new();

    public PlanEnvironment CreateEnvironment() => new()
    {
        Rules = rules.Current.Set,
        Probe = new FileSystemEntryProbe(),
        Trash = trash,
        Quarantine = quarantine,
        Commands = commands,
        IsElevated = Environment.IsPrivilegedProcess,
    };

    public Plan Create(CreatePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ScanId) || scans.Get(request.ScanId) is not { } session)
        {
            throw new PlanException("找不到這次掃描，可能已經被清除；請重新掃描。", notFound: true);
        }

        if (session.Status != ScanStatus.Completed || session.Result is not { } result)
        {
            throw new PlanException("掃描尚未完成。");
        }

        var mode = request.Action?.ToLowerInvariant() ?? "suggested";
        PlanAction? fixedAction = mode switch
        {
            "suggested" or "" => null,
            "recycle" => PlanAction.Recycle,
            "quarantine" => PlanAction.Quarantine,
            "move" => PlanAction.Move,
            "delete" => PlanAction.Delete,
            _ => throw new PlanException($"不支援的動作：{request.Action}"),
        };

        if (fixedAction == PlanAction.Move && string.IsNullOrWhiteSpace(request.Destination))
        {
            throw new PlanException("請指定要搬到哪個資料夾。");
        }

        var evaluation = session.GetEvaluation(rules.Current)!;
        var ids = new HashSet<int>();
        if (request.UseSelection ?? false)
        {
            ids.UnionWith(session.Selection.Effective(result, evaluation).Select(n => n.Id));
        }
        else if (request.UseAutoSelect ?? true)
        {
            foreach (var node in result.Items)
            {
                if (evaluation.IsAutoSelected(node.Id))
                {
                    ids.Add(node.Id);
                }
            }
        }

        if (!(request.UseSelection ?? false))
        {
            foreach (var id in request.Include ?? [])
            {
                ids.Add(id);
            }

            foreach (var id in request.Exclude ?? [])
            {
                ids.Remove(id);
            }
        }

        if (ids.Count == 0)
        {
            throw new PlanException("沒有勾選任何項目。");
        }

        if (ids.Count > MaxItems)
        {
            throw new PlanException($"一次最多處理 {MaxItems:N0} 個項目，請縮小範圍。");
        }

        var requests = new List<PlanItemRequest>(ids.Count);
        foreach (var id in ids)
        {
            if (result.Find(id) is not { } node)
            {
                throw new PlanException($"找不到項目：{id}");
            }

            var classification = evaluation.Get(id);
            var action = fixedAction ?? FromSuggestion(classification);
            string? blocked = null;
            if (!evaluation.IsSelectable(id))
            {
                blocked = evaluation.IsProtected(id)
                    ? $"受保護：{classification?.RuleName}"
                    : "這個資料夾裡有受保護的項目。";
            }

            requests.Add(new PlanItemRequest(
                node.GetFullPath(),
                node.Kind,
                node.Size,
                action,
                id,
                action == PlanAction.Move ? request.Destination : null,
                action == PlanAction.Command ? classification?.Method : null,
                classification?.RuleId,
                classification?.Category,
                blocked));
        }

        var plan = PlanBuilder.Build(Guid.NewGuid().ToString("N")[..12], request.ScanId, requests, CreateEnvironment(), cancellationToken);
        Store(plan);
        return plan;
    }

    public (Plan Plan, PlanExecution? Execution)? Get(string id) => _plans.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>開始執行（每個計畫只能執行一次）。</summary>
    public PlanExecution Execute(string id, string? confirmation)
    {
        lock (_lock)
        {
            if (!_plans.TryGetValue(id, out var entry))
            {
                throw new PlanException("找不到這個計畫，可能已經被清除；請重新建立。", notFound: true);
            }

            if (entry.Execution is not null)
            {
                throw new PlanException("這個計畫已經執行過了；要再處理請重新建立計畫。");
            }

            if (entry.Plan.Summary.RequiresConfirmation && confirmation != Plan.PermanentDeleteConfirmation)
            {
                throw new PlanException($"這個計畫包含永久刪除，請輸入「{Plan.PermanentDeleteConfirmation}」確認。");
            }

            var execution = new PlanExecution(entry.Plan);
            _plans[id] = (entry.Plan, execution);
            var plan = entry.Plan;
            execution.Start(CreateEnvironment(), log, confirmation, results =>
            {
                // 處理過的項目從勾選中移除，並在掃描結果中標示
                if (plan.ScanId is not null && scans.Get(plan.ScanId) is { } scan)
                {
                    var ok = results.Where(r => r.Outcome == ActionOutcome.Ok).Select(r => r.Index).ToHashSet();
                    scan.Selection.MarkDone(plan.Items.Where(i => ok.Contains(i.Index) && i.ItemId is not null).Select(i => i.ItemId!.Value));
                }
            });
            return execution;
        }
    }

    /// <summary>
    /// 依規則的建議決定動作：清理命令、隔離、永久刪除照規則；
    /// 其他（保留、人工確認、未分類）一律用可以還原的資源回收筒，因為是使用者自己勾選的。
    /// </summary>
    private static PlanAction FromSuggestion(Classification? classification) => classification?.Action switch
    {
        SuggestedAction.Quarantine => PlanAction.Quarantine,
        SuggestedAction.Delete => PlanAction.Delete,
        SuggestedAction.Command when classification.Method is not null => PlanAction.Command,
        _ => PlanAction.Recycle,
    };

    private void Store(Plan plan)
    {
        lock (_lock)
        {
            foreach (var old in _plans.Values
                         .Where(p => p.Execution is null || !p.Execution.IsRunning)
                         .OrderBy(p => p.Plan.CreatedUtc)
                         .Take(Math.Max(0, _plans.Count - (MaxPlans - 1)))
                         .ToList())
            {
                _plans.TryRemove(old.Plan.PlanId, out _);
                old.Execution?.Dispose();
            }

            _plans[plan.PlanId] = (plan, null);
        }
    }
}

/// <summary>依作業系統選擇資源回收筒的實作。</summary>
public static class TrashFactory
{
    public static ITrash Create() => OperatingSystem.IsWindows() ? new WindowsTrash() : new UnsupportedTrash();
}

/// <summary>集中建立執行相關的服務（資料都放在資料資料夾）。</summary>
public static class ExecutionServices
{
    public static IServiceCollection AddExecution(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(new QuarantineStore(paths.DataDirectory));
        services.AddSingleton(new ActionLog(Path.Combine(paths.DataDirectory, "logs")));
        services.AddSingleton(_ => TrashFactory.Create());
        services.AddSingleton<ICommandRunner, CommandRunner>();
        services.AddSingleton<PlanManager>();
        return services;
    }
}
