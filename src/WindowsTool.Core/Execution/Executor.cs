using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;

namespace WindowsTool.Core.Execution;

public sealed record ExecutionProgress(int Done, int Total, string? CurrentPath);

/// <summary>
/// 執行計畫（PLAN 10.2）：每一項執行前都再檢查一次；單筆失敗不中斷，最後彙總；
/// 每一項（包含被擋下、略過的）都寫進操作紀錄。
/// </summary>
public static class Executor
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(15);

    public static async Task<IReadOnlyList<ItemResult>> ExecuteAsync(
        Plan plan,
        PlanEnvironment environment,
        ActionLog log,
        string? confirmation,
        IProgress<ExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(log);

        if (plan.Summary.RequiresConfirmation && confirmation != Plan.PermanentDeleteConfirmation)
        {
            throw new InvalidOperationException($"這個計畫包含永久刪除，請輸入「{Plan.PermanentDeleteConfirmation}」確認。");
        }

        var results = new List<ItemResult>(plan.Items.Count);
        var done = 0;
        foreach (var item in plan.Items)
        {
            progress?.Report(new ExecutionProgress(done, plan.Items.Count, item.Path));

            ItemResult result;
            if (cancellationToken.IsCancellationRequested)
            {
                result = Result(item, ActionOutcome.Skipped, "已停止執行。");
            }
            else if (!item.WillRun)
            {
                result = Result(item, ActionOutcome.Skipped, item.Reason);
            }
            else
            {
                result = await RunAsync(plan, item, environment, cancellationToken).ConfigureAwait(false);
            }

            results.Add(result);
            log.Append(ToLog(plan.PlanId, item, result));
            done++;
        }

        progress?.Report(new ExecutionProgress(done, plan.Items.Count, null));
        return results;
    }

    private static async Task<ItemResult> RunAsync(Plan plan, PlanItem item, PlanEnvironment environment, CancellationToken cancellationToken)
    {
        try
        {
            // 執行前再檢查一次：計畫建立後，檔案或規則可能已經改變
            var (state, reason) = PlanBuilder.Check(item.ToRequest(), environment, cancellationToken);
            if (state is PlanItemState.Blocked or PlanItemState.Skipped)
            {
                return Result(item, ActionOutcome.Skipped, reason);
            }

            switch (item.Action)
            {
                case PlanAction.Recycle:
                {
                    var trash = environment.Trash.Recycle(item.Path);
                    return trash.Success
                        ? Result(item, ActionOutcome.Ok, null, item.Size, new UndoInfo("recycleBin", trash.RecycledPath, item.Path, null))
                        : Result(item, ActionOutcome.Failed, trash.Error);
                }

                case PlanAction.Quarantine:
                {
                    var entry = environment.Quarantine.Quarantine(item.Path, item.Kind, item.Size, plan.PlanId);
                    return Result(item, ActionOutcome.Ok, null, item.Size, new UndoInfo("quarantine", entry.StoredPath, item.Path, entry.EntryId));
                }

                case PlanAction.Move:
                {
                    var target = Path.Combine(FileSystem.PathUtil.NormalizeUserPath(item.Destination!), Path.GetFileName(item.Path));
                    if (item.Kind == ScanItemKind.Dir)
                    {
                        Directory.Move(item.Path, target);
                    }
                    else
                    {
                        File.Move(item.Path, target);
                    }

                    return Result(item, ActionOutcome.Ok, null, item.Size, new UndoInfo("move", target, item.Path, null));
                }

                case PlanAction.Delete:
                    if (item.Kind == ScanItemKind.Dir)
                    {
                        FileOperations.DeleteDirectory(item.Path);
                    }
                    else
                    {
                        FileOperations.DeleteFile(item.Path);
                    }

                    return Result(item, ActionOutcome.Ok, "已永久刪除，無法還原。", item.Size);

                case PlanAction.Command:
                {
                    var command = CommandCatalog.Find(item.CommandId)!;
                    var run = await environment.Commands.RunAsync(command, CommandTimeout, cancellationToken).ConfigureAwait(false);
                    var message = run.TimedOut ? $"超過 {CommandTimeout.TotalMinutes:0} 分鐘仍未完成，已經停止。"
                        : run.ExitCode != 0 ? $"命令結束代碼 {run.ExitCode}。" : null;
                    return Result(item, run.Succeeded ? ActionOutcome.Ok : ActionOutcome.Failed, message, 0, null, run.Output);
                }

                default:
                    return Result(item, ActionOutcome.Skipped, "不支援的動作。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(item, ActionOutcome.Skipped, "已停止執行。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            var partial = item.Action == PlanAction.Delete && item.Kind == ScanItemKind.Dir && Directory.Exists(item.Path)
                ? "（資料夾裡的部分內容可能已經刪除）"
                : "";
            return Result(item, ActionOutcome.Failed, ex.Message + partial);
        }
    }

    private static ItemResult Result(PlanItem item, ActionOutcome outcome, string? message, long bytes = 0, UndoInfo? undo = null, string? output = null) =>
        new(item.Index, item.Path, item.Action, outcome, message, bytes, undo, output);

    private static ActionLogEntry ToLog(string planId, PlanItem item, ItemResult result) => new()
    {
        Ts = DateTime.UtcNow,
        PlanId = planId,
        Index = item.Index,
        Path = item.Path,
        Action = item.Action switch
        {
            PlanAction.Recycle => LogAction.Recycle,
            PlanAction.Quarantine => LogAction.Quarantine,
            PlanAction.Move => LogAction.Move,
            PlanAction.Delete => LogAction.Delete,
            _ => LogAction.Command,
        },
        Result = result.Outcome,
        Error = result.Outcome == ActionOutcome.Ok ? null : result.Message,
        Bytes = result.Bytes,
        RuleId = item.RuleId,
        CommandId = item.CommandId,
        Undo = result.Undo,
        Output = result.Output,
    };
}
