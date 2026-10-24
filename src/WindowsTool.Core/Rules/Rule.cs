using System.Text.Json.Serialization;

namespace WindowsTool.Core.Rules;

// 規則的資料模型（PLAN 8.1）。JSON 欄位名稱為 camelCase，列舉值為小寫字串；
// 未知的欄位一律視為錯誤（避免打錯欄位名稱卻默默沒有作用）。

[JsonConverter(typeof(JsonStringEnumConverter<RuleSource>))]
public enum RuleSource
{
    [JsonStringEnumMemberName("builtin")]
    Builtin,

    [JsonStringEnumMemberName("user")]
    User,

    [JsonStringEnumMemberName("ai")]
    Ai,
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleKind>))]
public enum RuleKind
{
    /// <summary>分類：標註類別、風險與建議動作。</summary>
    [JsonStringEnumMemberName("classify")]
    Classify,

    /// <summary>保護：命中的項目（含所有子孫）不可勾選、不可執行。</summary>
    [JsonStringEnumMemberName("protect")]
    Protect,

    /// <summary>提醒：不保護，但把風險至少提高到指定等級，並取消預設勾選（含所有子孫）。</summary>
    [JsonStringEnumMemberName("caution")]
    Caution,
}

/// <summary>風險等級，數值越大越危險。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    [JsonStringEnumMemberName("unknown")]
    Unknown = 0,

    [JsonStringEnumMemberName("low")]
    Low = 1,

    [JsonStringEnumMemberName("medium")]
    Medium = 2,

    [JsonStringEnumMemberName("high")]
    High = 3,

    [JsonStringEnumMemberName("protected")]
    Protected = 4,
}

[JsonConverter(typeof(JsonStringEnumConverter<SuggestedAction>))]
public enum SuggestedAction
{
    [JsonStringEnumMemberName("keep")]
    Keep,

    [JsonStringEnumMemberName("review")]
    Review,

    [JsonStringEnumMemberName("recycle")]
    Recycle,

    [JsonStringEnumMemberName("quarantine")]
    Quarantine,

    [JsonStringEnumMemberName("delete")]
    Delete,

    [JsonStringEnumMemberName("command")]
    Command,
}

[JsonConverter(typeof(JsonStringEnumConverter<MatchKind>))]
public enum MatchKind
{
    [JsonStringEnumMemberName("any")]
    Any,

    [JsonStringEnumMemberName("dir")]
    Dir,

    [JsonStringEnumMemberName("file")]
    File,
}

[JsonConverter(typeof(JsonStringEnumConverter<ScopeType>))]
public enum ScopeType
{
    [JsonStringEnumMemberName("global")]
    Global,

    /// <summary>只套用在 <see cref="RuleScope.Root"/> 底下；相對的 glob 以它為起點。</summary>
    [JsonStringEnumMemberName("location")]
    Location,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Rule
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public RuleSource Source { get; set; } = RuleSource.User;

    public bool Enabled { get; set; } = true;

    public RuleKind Kind { get; set; } = RuleKind.Classify;

    /// <summary><c>windows</c> / <c>linux</c> / <c>macos</c>；null 或空白代表所有平台。</summary>
    public IReadOnlyList<string>? Platforms { get; set; }

    public RuleScope Scope { get; set; } = new();

    public RuleMatch Match { get; set; } = new();

    public RuleResult Result { get; set; } = new();

    public RuleMeta? Meta { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleScope
{
    public ScopeType Type { get; set; } = ScopeType.Global;

    public string? Root { get; set; }
}

/// <summary>比對條件：各條件之間是 AND，陣列內是 OR（PLAN 8.2）。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleMatch
{
    public MatchKind Kind { get; set; } = MatchKind.Any;

    public IReadOnlyList<string>? PathGlob { get; set; }

    /// <summary>比對名稱（不含路徑）的正規表示式。</summary>
    public string? NameRegex { get; set; }

    /// <summary>副檔名（只套用在檔案上）。</summary>
    public IReadOnlyList<string>? Ext { get; set; }

    public long? MinSize { get; set; }

    /// <summary>至少 N 天沒有修改（資料夾以整個子樹中最新的檔案為準）。</summary>
    public int? MinAgeDays { get; set; }

    /// <summary>同一層要有符合的項目（名稱，可用萬用字元），例如 <c>package.json</c>、<c>*.csproj</c>。</summary>
    public IReadOnlyList<string>? SiblingExists { get; set; }

    /// <summary>資料夾內要有符合的項目（名稱，可用萬用字元）。</summary>
    public IReadOnlyList<string>? ChildExists { get; set; }

    public IReadOnlyList<string>? ExcludeGlob { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleResult
{
    public string? Category { get; set; }

    public RiskLevel Risk { get; set; } = RiskLevel.Unknown;

    public SuggestedAction SuggestedAction { get; set; } = SuggestedAction.Review;

    /// <summary><see cref="SuggestedAction.Command"/> 時引用的白名單 commandId。</summary>
    public string? Method { get; set; }

    public string? Reason { get; set; }

    public bool AutoSelect { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleMeta
{
    public DateTime? CreatedUtc { get; set; }

    public DateTime? UpdatedUtc { get; set; }

    public string? ApprovedBy { get; set; }

    public RuleOrigin? Origin { get; set; }

    public int HitCount { get; set; }

    public DateTime? LastHitUtc { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleOrigin
{
    public string? FromItemPath { get; set; }

    public string? Provider { get; set; }
}

/// <summary>一個項目最後生效的標註結果（PLAN 7.2）。</summary>
public sealed record Classification(
    string Category,
    RiskLevel Risk,
    SuggestedAction Action,
    string? Method,
    string? Reason,
    RuleSource Source,
    string RuleId,
    string RuleName,
    RuleKind RuleKind,
    bool AutoSelect,
    bool Inherited,
    string? CautionRuleId,
    string? CautionReason);
