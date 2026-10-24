using WindowsTool.Core.Rules;

namespace WindowsTool.App.Rules;

/// <summary>新增 / 修改 / 驗證規則的請求。<c>scanId</c> 指定時會對該次掃描結果 dry-run。</summary>
public sealed record RuleRequest(Rule? Rule, string? ScanId = null, bool? AllowGlobalScope = null);

public sealed record EnabledRequest(bool Enabled);

/// <summary>規則加上目前的狀態。</summary>
public sealed record RuleInfoDto(
    Rule Rule,
    bool Active,
    bool Editable,
    bool CanDisable,
    IReadOnlyList<RuleIssue> Issues);

public sealed record RulesResponse(
    long Version,
    string FilePath,
    bool Portable,
    string Platform,
    string? LoadError,
    IReadOnlyList<string> LoadWarnings,
    IReadOnlyList<RuleInfoDto> Rules,
    IReadOnlyList<CommandCatalog.CommandInfo> Commands,
    IReadOnlyDictionary<string, string> Tokens);

public sealed record ValidationResponse(
    bool Valid,
    Rule Rule,
    IReadOnlyList<RuleIssue> Issues,
    DryRunResult? DryRun,
    string? ScanRoot);

public sealed record RuleSavedResponse(long Version, RuleInfoDto Rule, ValidationResponse Validation);

public sealed record ImportProblem(string? RuleId, IReadOnlyList<RuleIssue> Issues);

public sealed record ImportResponse(int Imported, int DisabledBuiltin, IReadOnlyList<ImportProblem> Problems);
