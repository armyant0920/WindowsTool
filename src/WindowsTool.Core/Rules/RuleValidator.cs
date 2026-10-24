using System.Text.RegularExpressions;
using WindowsTool.Core.Scanning;

namespace WindowsTool.Core.Rules;

public sealed record DryRunSample(int ItemId, string Path, ScanItemKind Kind, long Size, string? Note);

/// <summary>規則對目前掃描結果的試套用結果（PLAN 8.4 第 3、4 點）。</summary>
public sealed record DryRunResult(
    int Count,
    long Size,
    IReadOnlyList<DryRunSample> Samples,
    int ConflictCount,
    IReadOnlyList<DryRunSample> Conflicts,
    IReadOnlyList<string> SuggestedExcludes);

public sealed record RuleValidationResult(IReadOnlyList<RuleIssue> Issues, DryRunResult? DryRun)
{
    public bool IsValid => !Issues.Any(i => i.IsError);
}

public sealed record RuleValidationContext
{
    /// <summary>目前生效的規則（用來判斷保護路徑，以及展開 token）。</summary>
    public required RuleSet Current { get; init; }

    /// <summary>要試套用的掃描結果；null 時只做靜態檢查。</summary>
    public ScanResult? Scan { get; init; }

    /// <summary><see cref="Scan"/> 以目前規則評估的結果（判斷哪些項目受保護）。</summary>
    public RuleEvaluation? Evaluation { get; init; }

    public required IEntryProbe Probe { get; init; }

    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    /// <summary>AI 提案的規則預設只能是 location；使用者在 UI 明確選擇後才允許 global。</summary>
    public bool AllowGlobalScopeForAi { get; init; }
}

/// <summary>
/// 規則寫入前的檢查（PLAN 8.4）：格式、過寬的 glob、白名單命令、保護路徑衝突，以及對掃描結果 dry-run。
/// </summary>
public static partial class RuleValidator
{
    public const int MaxSamples = 20;
    public const int MaxNameLength = 100;
    public const int MaxCategoryLength = 50;
    public const int MaxReasonLength = 500;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    public static RuleValidationResult Validate(Rule rule, RuleValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);

        var issues = new List<RuleIssue>();
        CheckStructure(rule, context, issues);

        var compileIssues = new List<RuleIssue>();
        var compiled = RuleCompiler.Compile(rule, context.Current.Tokens, context.Current.IgnoreCase, compileIssues);
        issues.AddRange(compileIssues);

        if (compiled is not null && rule.Kind == RuleKind.Classify)
        {
            CheckBreadth(compiled, context.Current, issues);
            CheckStaticProtection(compiled, context, issues);
        }

        DryRunResult? dryRun = null;
        if (compiled is not null && !issues.Any(i => i.IsError) && context.Scan is not null)
        {
            dryRun = DryRun(compiled, context);
            if (dryRun.ConflictCount > 0)
            {
                var hint = dryRun.SuggestedExcludes.Count > 0
                    ? $"可以在 excludeGlob 加上：{string.Join("、", dryRun.SuggestedExcludes)}"
                    : "請縮小路徑樣式或加上 excludeGlob。";
                issues.Add(RuleIssue.Error("match", $"這條規則會命中 {dryRun.ConflictCount} 個受保護（或包含受保護項目）的項目。{hint}"));
            }
            else if (dryRun.Count == 0)
            {
                issues.Add(RuleIssue.Warning("match", "在目前的掃描結果中沒有命中任何項目。"));
            }
        }

        return new RuleValidationResult(issues, dryRun);
    }

    private static void CheckStructure(Rule rule, RuleValidationContext context, List<RuleIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || !IdPattern().IsMatch(rule.Id))
        {
            issues.Add(RuleIssue.Error("id", "id 只能使用小寫英文、數字、「.」「_」「-」，開頭必須是英文或數字，最長 64 字元。"));
        }

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            issues.Add(RuleIssue.Error("name", "請填寫規則名稱。"));
        }
        else if (rule.Name.Length > MaxNameLength)
        {
            issues.Add(RuleIssue.Error("name", $"名稱最長 {MaxNameLength} 字。"));
        }

        if (!Enum.IsDefined(rule.Source))
        {
            issues.Add(RuleIssue.Error("source", "不支援的 source。"));
        }

        if (!Enum.IsDefined(rule.Kind))
        {
            issues.Add(RuleIssue.Error("kind", "kind 只能是 classify、protect 或 caution。"));
        }

        if (rule.Platforms is { } platforms)
        {
            for (var i = 0; i < platforms.Count; i++)
            {
                if (!RuleSet.KnownPlatforms.Contains(platforms[i], StringComparer.OrdinalIgnoreCase))
                {
                    issues.Add(RuleIssue.Error($"platforms[{i}]", $"不支援的平台「{platforms[i]}」，只能是 windows、linux、macos。"));
                }
            }

            if (platforms.Count > 0 && !RuleSet.AppliesTo(rule, context.Current.Platform))
            {
                issues.Add(RuleIssue.Warning("platforms", "這條規則不適用於目前的作業系統，在這台電腦上不會生效。"));
            }
        }

        if (rule.Scope is null || !Enum.IsDefined(rule.Scope.Type))
        {
            issues.Add(RuleIssue.Error("scope.type", "scope.type 只能是 global 或 location。"));
        }
        else if (rule.Source == RuleSource.Ai && rule.Scope.Type == ScopeType.Global && !context.AllowGlobalScopeForAi)
        {
            issues.Add(RuleIssue.Error("scope.type", "AI 提案的規則預設只套用在指定位置（location）；要改成 global 必須由使用者明確選擇。"));
        }

        if (rule.Match is null)
        {
            issues.Add(RuleIssue.Error("match", "缺少比對條件（match）。"));
        }
        else if (!Enum.IsDefined(rule.Match.Kind))
        {
            issues.Add(RuleIssue.Error("match.kind", "match.kind 只能是 any、dir 或 file。"));
        }
        else if (rule.Match.ChildExists is { Count: > 0 } && rule.Match.Kind == MatchKind.File)
        {
            issues.Add(RuleIssue.Error("match.childExists", "childExists 只能用在資料夾上（kind 為 dir 或 any）。"));
        }

        CheckResult(rule, issues);
    }

    private static void CheckResult(Rule rule, List<RuleIssue> issues)
    {
        var result = rule.Result;
        if (result is null)
        {
            issues.Add(RuleIssue.Error("result", "缺少標註結果（result）。"));
            return;
        }

        if (!Enum.IsDefined(result.Risk))
        {
            issues.Add(RuleIssue.Error("result.risk", "不支援的風險等級。"));
            return;
        }

        if (!Enum.IsDefined(result.SuggestedAction))
        {
            issues.Add(RuleIssue.Error("result.suggestedAction", "不支援的建議動作。"));
            return;
        }

        if (result.Category is { Length: > MaxCategoryLength })
        {
            issues.Add(RuleIssue.Error("result.category", $"類別名稱最長 {MaxCategoryLength} 字。"));
        }

        if (result.Reason is { Length: > MaxReasonLength })
        {
            issues.Add(RuleIssue.Error("result.reason", $"說明最長 {MaxReasonLength} 字。"));
        }

        switch (rule.Kind)
        {
            case RuleKind.Protect:
                if (result.AutoSelect)
                {
                    issues.Add(RuleIssue.Error("result.autoSelect", "保護規則不能預設勾選。"));
                }

                return;

            case RuleKind.Caution:
                if (result.Risk is not (RiskLevel.Medium or RiskLevel.High))
                {
                    issues.Add(RuleIssue.Error("result.risk", "提醒規則的風險下限只能是 medium 或 high。"));
                }

                if (result.AutoSelect)
                {
                    issues.Add(RuleIssue.Error("result.autoSelect", "提醒規則的作用就是取消預設勾選，不能設為自動勾選。"));
                }

                return;
        }

        if (string.IsNullOrWhiteSpace(result.Category))
        {
            issues.Add(RuleIssue.Error("result.category", "分類規則必須填寫類別（category）。"));
        }

        if (result.Risk is not (RiskLevel.Low or RiskLevel.Medium or RiskLevel.High))
        {
            issues.Add(RuleIssue.Error("result.risk", "分類規則的風險只能是 low、medium 或 high（受保護請改用保護規則）。"));
        }

        if (result.SuggestedAction == SuggestedAction.Command)
        {
            if (!CommandCatalog.Contains(result.Method))
            {
                issues.Add(RuleIssue.Error("result.method",
                    $"建議動作為 command 時，method 必須是白名單命令之一：{string.Join("、", CommandCatalog.All.Select(c => c.Id))}"));
            }
        }
        else if (!string.IsNullOrEmpty(result.Method))
        {
            issues.Add(RuleIssue.Error("result.method", "只有建議動作為 command 時才能填寫 method。"));
        }

        if (result.AutoSelect && result.SuggestedAction == SuggestedAction.Delete)
        {
            issues.Add(RuleIssue.Error("result.autoSelect", "永久刪除不能預設勾選。"));
        }

        if (result.AutoSelect && result.Risk == RiskLevel.High)
        {
            issues.Add(RuleIssue.Error("result.autoSelect", "高風險的項目不能預設勾選。"));
        }

        if (result.AutoSelect && result.SuggestedAction is SuggestedAction.Keep or SuggestedAction.Review)
        {
            issues.Add(RuleIssue.Warning("result.autoSelect", "建議動作是保留或檢視時，預設勾選沒有意義。"));
        }
    }

    /// <summary>過寬的樣式（PLAN 8.4 第 2 點）：<c>**</c>、<c>**/*</c>、<c>C:/**</c>、<c>/</c>、<c>{UserProfile}/**</c> 等。</summary>
    private static void CheckBreadth(CompiledRule compiled, RuleSet current, List<RuleIssue> issues)
    {
        var wellKnown = WellKnownRoots(current);
        var comparison = current.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        for (var i = 0; i < compiled.Globs.Length; i++)
        {
            var glob = compiled.Globs[i];
            var significant = glob.GetSignificantSegments();
            var tooBroad = significant.Count == 0
                || significant.All(s => s == "**")
                || RulePath.IsRootOnly(significant.ToArray())
                || wellKnown.Any(root => root.Length == significant.Count
                    && root.Zip(significant).All(pair => string.Equals(pair.First, pair.Second, comparison)));

            if (tooBroad)
            {
                issues.Add(RuleIssue.Error(
                    $"match.pathGlob[{IndexOf(compiled.Rule.Match.PathGlob, glob, compiled, current)}]",
                    $"路徑樣式太寬：{glob.Text}。請指定更明確的資料夾或名稱，例如 **/node_modules、{{LocalAppData}}/Temp/**。"));
            }
        }
    }

    /// <summary>樣式的固定開頭已經位於受保護的路徑底下：不需要掃描結果就能判斷一定衝突。</summary>
    private static void CheckStaticProtection(CompiledRule compiled, RuleValidationContext context, List<RuleIssue> issues)
    {
        foreach (var glob in compiled.Globs)
        {
            var prefix = glob.LiteralPrefix;
            if (prefix.Count == 0 || !RulePath.IsAbsolute(prefix.ToArray()))
            {
                continue;
            }

            // 有萬用字元時，檢查「開頭底下的任意一個子項目」
            var path = RulePath.Join(prefix.ToArray());
            var probePath = glob.IsLiteral ? path : Path.Combine(path, "x");
            var facts = new ItemFacts(Path.GetFileName(probePath), ScanItemKind.Dir, long.MaxValue, 0);
            var verdict = RuleEngine.EvaluatePath(context.Current, probePath, facts, context.Probe, context.NowUtc);
            if (verdict.IsProtected)
            {
                issues.Add(RuleIssue.Error("match.pathGlob",
                    $"樣式 {glob.Text} 位於受保護的路徑（{verdict.Classification!.RuleName}），保護規則永遠優先，這條規則不會有作用。"));
            }
        }
    }

    private static DryRunResult DryRun(CompiledRule compiled, RuleValidationContext context)
    {
        var scan = context.Scan!;
        var evaluation = context.Evaluation;
        var matchContext = new MatchContext(context.Probe, context.NowUtc.Ticks);
        var checkConflicts = compiled.Rule.Kind == RuleKind.Classify && evaluation is not null;

        var hits = new HashSet<int>();
        var topLevel = new List<ScanNode>();
        var conflicts = new List<ScanNode>();
        var conflictRules = new Dictionary<string, Rule>(StringComparer.Ordinal);

        RuleEngine.Walk(scan, (node, path) =>
        {
            matchContext.Node = node;
            var facts = new ItemFacts(node.Name, node.Kind, node.Size, node.NewestWriteTicks);
            if (!compiled.Matches(path, facts, matchContext, out _))
            {
                return;
            }

            hits.Add(node.Id);
            if (!HasHitAncestor(node, hits))
            {
                topLevel.Add(node);
            }

            if (checkConflicts && !evaluation!.IsSelectable(node.Id))
            {
                conflicts.Add(node);
                if (evaluation.Get(node.Id) is { Risk: RiskLevel.Protected } protectedBy)
                {
                    var rule = context.Current.Rules.FirstOrDefault(r => r.Id == protectedBy.RuleId);
                    if (rule is not null)
                    {
                        conflictRules.TryAdd(rule.Id, rule);
                    }
                }
            }
        });

        var suggested = conflictRules.Values
            .SelectMany(r => r.Match.PathGlob ?? [])
            .Select(g => g.TrimEnd('/', '\\').EndsWith("/**", StringComparison.Ordinal) ? g : g.TrimEnd('/', '\\') + "/**")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new DryRunResult(
            hits.Count,
            topLevel.Sum(n => n.Size),
            [.. topLevel.OrderByDescending(n => n.Size).Take(MaxSamples).Select(n => Sample(n, null))],
            conflicts.Count,
            [.. conflicts.OrderByDescending(n => n.Size).Take(MaxSamples).Select(n => Sample(n, ConflictNote(n, evaluation!)))],
            suggested);
    }

    private static string ConflictNote(ScanNode node, RuleEvaluation evaluation) =>
        evaluation.Get(node.Id) is { Risk: RiskLevel.Protected } c
            ? $"受保護：{c.RuleName}"
            : "裡面有受保護的項目";

    private static DryRunSample Sample(ScanNode node, string? note) =>
        new(node.Id, node.GetFullPath(), node.Kind, node.Size, note);

    private static bool HasHitAncestor(ScanNode node, HashSet<int> hits)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (hits.Contains(parent.Id))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string[]> WellKnownRoots(RuleSet current)
    {
        string[] names = ["UserProfile", "LocalAppData", "RoamingAppData", "ProgramData", "Windows", "ProgramFiles",
            "ProgramFiles(x86)", "Documents", "Desktop", "Pictures", "Videos", "Music"];
        var roots = new List<string[]>();
        foreach (var name in names)
        {
            var expanded = current.Tokens.Expand("{" + name + "}");
            if (expanded.IsOk)
            {
                var segments = RulePath.Split(expanded.Value!);
                roots.Add(segments);
                if (name == "UserProfile" && segments.Length > 1)
                {
                    roots.Add(segments[..^1]); // C:\Users
                }
            }
        }

        return roots;
    }

    private static int IndexOf(IReadOnlyList<string>? patterns, GlobPattern glob, CompiledRule compiled, RuleSet current)
    {
        // 找出是第幾個樣式（展開後比對），找不到時回傳 0
        if (patterns is null)
        {
            return 0;
        }

        for (var i = 0; i < patterns.Count; i++)
        {
            var candidate = RuleCompiler.CompileGlob(patterns[i], compiled.ScopeRoot, current.Tokens, current.IgnoreCase, out _, "");
            if (candidate?.Text == glob.Text)
            {
                return i;
            }
        }

        return 0;
    }
}
