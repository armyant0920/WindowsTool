using WindowsTool.Core.FileSystem;

namespace WindowsTool.Core.Rules;

/// <summary>
/// 一組已編譯、可以直接拿來評估的規則（不可變）。
/// 停用、不適用目前平台、或有錯誤的規則不會參與比對；錯誤記錄在 <see cref="Issues"/>。
/// </summary>
public sealed class RuleSet
{
    private RuleSet(
        IReadOnlyList<Rule> rules,
        PathTokens tokens,
        string platform,
        bool ignoreCase,
        CompiledRule[] protect,
        CompiledRule[] caution,
        CompiledRule[] classify,
        IReadOnlyDictionary<string, IReadOnlyList<RuleIssue>> issues)
    {
        Rules = rules;
        Tokens = tokens;
        Platform = platform;
        IgnoreCase = ignoreCase;
        Protect = protect;
        Caution = caution;
        Classify = classify;
        Issues = issues;
    }

    public static string CurrentPlatform { get; } =
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    public static IReadOnlyList<string> KnownPlatforms { get; } = ["windows", "linux", "macos"];

    /// <summary>建立時傳入的所有規則（包含停用的）。</summary>
    public IReadOnlyList<Rule> Rules { get; }

    public PathTokens Tokens { get; }

    public string Platform { get; }

    public bool IgnoreCase { get; }

    /// <summary>每條規則編譯時的問題（只列出有問題的規則）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RuleIssue>> Issues { get; }

    internal CompiledRule[] Protect { get; }

    internal CompiledRule[] Caution { get; }

    internal CompiledRule[] Classify { get; }

    /// <summary>實際參與比對的規則數。</summary>
    public int ActiveCount => Protect.Length + Caution.Length + Classify.Length;

    public bool IsActive(string ruleId) =>
        Protect.Any(r => r.Rule.Id == ruleId) || Caution.Any(r => r.Rule.Id == ruleId) || Classify.Any(r => r.Rule.Id == ruleId);

    public static RuleSet Create(IEnumerable<Rule> rules, PathTokens tokens, string? platform = null, bool? ignoreCase = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(tokens);

        var list = rules.ToList();
        var targetPlatform = platform ?? CurrentPlatform;
        var caseInsensitive = ignoreCase ?? PathUtil.PathComparison == StringComparison.OrdinalIgnoreCase;

        var protect = new List<CompiledRule>();
        var caution = new List<CompiledRule>();
        var classify = new List<CompiledRule>();
        var issues = new Dictionary<string, IReadOnlyList<RuleIssue>>(StringComparer.Ordinal);

        foreach (var rule in list)
        {
            if (!rule.Enabled || !AppliesTo(rule, targetPlatform))
            {
                continue;
            }

            var ruleIssues = new List<RuleIssue>();
            var compiled = RuleCompiler.Compile(rule, tokens, caseInsensitive, ruleIssues);
            if (ruleIssues.Count > 0)
            {
                issues[rule.Id] = ruleIssues;
            }

            if (compiled is null || compiled.Globs.Length == 0)
            {
                continue;
            }

            (rule.Kind switch
            {
                RuleKind.Protect => protect,
                RuleKind.Caution => caution,
                _ => classify,
            }).Add(compiled);
        }

        return new RuleSet(list, tokens, targetPlatform, caseInsensitive, [.. protect], [.. caution], [.. classify], issues);
    }

    public static bool AppliesTo(Rule rule, string platform)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Platforms is not { Count: > 0 } || rule.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);
    }
}
