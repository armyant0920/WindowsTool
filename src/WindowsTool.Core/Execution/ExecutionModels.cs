using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsTool.Core.Scanning;

namespace WindowsTool.Core.Execution;

// 執行計畫、執行結果、操作紀錄、隔離區 manifest 的資料模型（PLAN 7.3、10）。

[JsonConverter(typeof(JsonStringEnumConverter<PlanAction>))]
public enum PlanAction
{
    /// <summary>移到資源回收筒（可以從資源回收筒還原）。</summary>
    [JsonStringEnumMemberName("recycle")]
    Recycle,

    /// <summary>搬到隔離區（工具內一鍵還原）。</summary>
    [JsonStringEnumMemberName("quarantine")]
    Quarantine,

    /// <summary>搬到使用者指定的資料夾。</summary>
    [JsonStringEnumMemberName("move")]
    Move,

    /// <summary>永久刪除（需要二次確認）。</summary>
    [JsonStringEnumMemberName("delete")]
    Delete,

    /// <summary>執行白名單清理命令。</summary>
    [JsonStringEnumMemberName("command")]
    Command,
}

[JsonConverter(typeof(JsonStringEnumConverter<PlanItemState>))]
public enum PlanItemState
{
    [JsonStringEnumMemberName("ready")]
    Ready,

    /// <summary>可以執行，但可能失敗（例如檔案正在使用中）。</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>不會執行（受保護、已經不存在、目的地無效…）。</summary>
    [JsonStringEnumMemberName("blocked")]
    Blocked,

    /// <summary>不需要執行（已經包含在上層項目、同一個命令只執行一次）。</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

[JsonConverter(typeof(JsonStringEnumConverter<ActionOutcome>))]
public enum ActionOutcome
{
    [JsonStringEnumMemberName("ok")]
    Ok,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

/// <summary>寫進操作紀錄的動作種類（執行計畫的動作，加上隔離區的還原與清空）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogAction>))]
public enum LogAction
{
    [JsonStringEnumMemberName("recycle")]
    Recycle,

    [JsonStringEnumMemberName("quarantine")]
    Quarantine,

    [JsonStringEnumMemberName("move")]
    Move,

    [JsonStringEnumMemberName("delete")]
    Delete,

    [JsonStringEnumMemberName("command")]
    Command,

    [JsonStringEnumMemberName("restore")]
    Restore,

    [JsonStringEnumMemberName("purge")]
    Purge,
}

/// <summary>要放進執行計畫的一個項目。</summary>
public sealed record PlanItemRequest(
    string Path,
    ScanItemKind Kind,
    long Size,
    PlanAction Action,
    int? ItemId = null,
    string? Destination = null,
    string? CommandId = null,
    string? RuleId = null,
    string? Category = null,
    string? BlockedReason = null);

public sealed record PlanItem(
    int Index,
    int? ItemId,
    string Path,
    ScanItemKind Kind,
    long Size,
    PlanAction Action,
    string? Destination,
    string? CommandId,
    string? RuleId,
    string? Category,
    PlanItemState State,
    string? Reason)
{
    public bool WillRun => State is PlanItemState.Ready or PlanItemState.Warning;

    public PlanItemRequest ToRequest() => new(Path, Kind, Size, Action, ItemId, Destination, CommandId, RuleId, Category);
}

public sealed record PlanSummary(
    int Ready,
    int Warning,
    int Blocked,
    int Skipped,
    long Bytes,
    bool RequiresConfirmation,
    IReadOnlyList<string> Commands);

/// <summary>執行計畫（PLAN 7.3）：建立時已經完成 dry-run。</summary>
public sealed record Plan(
    string PlanId,
    DateTime CreatedUtc,
    string? ScanId,
    IReadOnlyList<PlanItem> Items,
    PlanSummary Summary)
{
    /// <summary>含有永久刪除時，執行前必須輸入的確認文字。</summary>
    public const string PermanentDeleteConfirmation = "永久刪除";
}

/// <summary>還原資訊。</summary>
/// <param name="Type"><c>recycleBin</c>（從資源回收筒還原）、<c>quarantine</c>（工具內還原）、<c>move</c>（搬回原位）。</param>
public sealed record UndoInfo(string Type, string? From, string? To, string? EntryId);

public sealed record ItemResult(
    int Index,
    string Path,
    PlanAction Action,
    ActionOutcome Outcome,
    string? Message,
    long Bytes,
    UndoInfo? Undo,
    string? Output);

/// <summary>操作紀錄（logs/actions-YYYYMM.jsonl，一行一筆，只附加）。</summary>
public sealed record ActionLogEntry
{
    public DateTime Ts { get; set; }

    public string? PlanId { get; set; }

    public int? Index { get; set; }

    public string Path { get; set; } = "";

    public LogAction Action { get; set; }

    public ActionOutcome Result { get; set; }

    public string? Error { get; set; }

    public long Bytes { get; set; }

    public string? RuleId { get; set; }

    public string? CommandId { get; set; }

    public UndoInfo? Undo { get; set; }

    public string? Output { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<QuarantineStatus>))]
public enum QuarantineStatus
{
    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("restored")]
    Restored,

    [JsonStringEnumMemberName("purged")]
    Purged,
}

/// <summary>隔離區中的一個項目。</summary>
public sealed record QuarantineEntry
{
    public string EntryId { get; set; } = "";

    public string OriginalPath { get; set; } = "";

    public string StoredPath { get; set; } = "";

    public ScanItemKind Kind { get; set; }

    public long Size { get; set; }

    public DateTime QuarantinedUtc { get; set; }

    public string? PlanId { get; set; }

    public QuarantineStatus Status { get; set; }

    public DateTime? ClosedUtc { get; set; }
}

/// <summary>manifest.jsonl 的一行：隔離、還原或清空。</summary>
internal sealed record QuarantineRecord
{
    public string Type { get; set; } = "";

    public DateTime Ts { get; set; }

    public QuarantineEntry? Entry { get; set; }

    public string? EntryId { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActionLogEntry))]
[JsonSerializable(typeof(QuarantineRecord))]
internal sealed partial class ExecutionJsonContext : JsonSerializerContext;
