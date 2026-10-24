using System.Text.RegularExpressions;
using WindowsTool.Core.Scanning;

namespace WindowsTool.Core.Rules;

/// <summary>驗證或編譯規則時發現的問題。<see cref="Field"/> 是 JSON 路徑，例如 <c>match.pathGlob[0]</c>。</summary>
public sealed record RuleIssue(string Field, string Message, bool IsError)
{
    public static RuleIssue Error(string field, string message) => new(field, message, true);

    public static RuleIssue Warning(string field, string message) => new(field, message, false);
}

/// <summary>規則比對時需要的項目資訊。</summary>
public readonly record struct ItemFacts(string Name, ScanItemKind Kind, long Size, long NewestWriteTicks);

/// <summary>一次比對的環境：現在時間、檔案系統查詢，以及目前的掃描節點（用來先查掃描結果，省下系統呼叫）。</summary>
internal sealed class MatchContext(IEntryProbe probe, long nowTicks)
{
    public IEntryProbe Probe { get; } = probe;

    public long NowTicks { get; } = nowTicks;

    public ScanNode? Node { get; set; }
}

/// <summary>預先處理好的規則：token 已展開、glob 與正規表示式已編譯。</summary>
internal sealed class CompiledRule
{
    public const int Specificity = 1000;

    public required Rule Rule { get; init; }

    public required GlobPattern[] Globs { get; init; }

    public required GlobPattern[] Excludes { get; init; }

    public Regex? NameRegex { get; init; }

    public HashSet<string>? Extensions { get; init; }

    public required WildcardSegment[] Siblings { get; init; }

    public required WildcardSegment[] Children { get; init; }

    public string[]? ScopeRoot { get; init; }

    public bool IgnoreCase { get; init; }

    public int ConditionCount { get; init; }

    /// <summary>優先序：使用者 1、AI 2、內建 3（數字小的優先，PLAN 8.3）。</summary>
    public int Tier => Rule.Source switch
    {
        RuleSource.User => 1,
        RuleSource.Ai => 2,
        _ => 3,
    };

    public long CreatedTicks => Rule.Meta?.CreatedUtc?.Ticks ?? 0;

    /// <summary>提醒規則的風險下限。</summary>
    public RiskLevel CautionRisk => Rule.Result.Risk is RiskLevel.Medium or RiskLevel.High ? Rule.Result.Risk : RiskLevel.High;

    /// <summary>
    /// 是否符合。<paramref name="score"/> = 條件數 × 1000 + 符合的 glob 的字面長度，分數越高越具體（PLAN 8.3 第 5 點）。
    /// </summary>
    public bool Matches(ReadOnlySpan<string> path, in ItemFacts item, MatchContext context, out int score)
    {
        score = 0;
        var match = Rule.Match;

        switch (match.Kind)
        {
            case MatchKind.Dir when item.Kind != ScanItemKind.Dir:
            case MatchKind.File when item.Kind != ScanItemKind.File:
                return false;
        }

        if (ScopeRoot is not null && !StartsWith(path, ScopeRoot))
        {
            return false;
        }

        if (Extensions is not null
            && (item.Kind != ScanItemKind.File || !Extensions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(Path.GetExtension(item.Name.AsSpan()))))
        {
            return false;
        }

        if (match.MinSize is { } minSize && item.Size < minSize)
        {
            return false;
        }

        if (match.MinAgeDays is { } days && item.NewestWriteTicks > context.NowTicks - (days * TimeSpan.TicksPerDay))
        {
            return false;
        }

        var literal = -1;
        foreach (var glob in Globs)
        {
            if (glob.LiteralLength > literal && glob.IsMatch(path))
            {
                literal = glob.LiteralLength;
            }
        }

        if (literal < 0)
        {
            return false;
        }

        if (NameRegex is not null && !NameRegex.IsMatch(item.Name))
        {
            return false;
        }

        if (IsExcluded(path))
        {
            return false;
        }

        if (Siblings.Length > 0 && (path.Length < 2 || !AnyEntry(path[..^1], context.Node?.Parent, Siblings, context, exclude: item.Name)))
        {
            return false;
        }

        if (Children.Length > 0 && (item.Kind != ScanItemKind.Dir || !AnyEntry(path, context.Node as DirNode, Children, context, exclude: null)))
        {
            return false;
        }

        score = (ConditionCount * Specificity) + Math.Min(literal, Specificity - 1);
        return true;
    }

    public bool IsExcluded(ReadOnlySpan<string> path)
    {
        foreach (var exclude in Excludes)
        {
            if (exclude.IsMatch(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>比較兩條同類規則：a 是否比 b 更優先（層級 → 具體程度 → 建立時間較新 → id）。</summary>
    public static bool IsPreferred(CompiledRule a, int scoreA, CompiledRule b, int scoreB)
    {
        if (a.Tier != b.Tier)
        {
            return a.Tier < b.Tier;
        }

        if (scoreA != scoreB)
        {
            return scoreA > scoreB;
        }

        if (a.CreatedTicks != b.CreatedTicks)
        {
            return a.CreatedTicks > b.CreatedTicks;
        }

        return string.CompareOrdinal(a.Rule.Id, b.Rule.Id) < 0;
    }

    private bool StartsWith(ReadOnlySpan<string> path, string[] root)
    {
        if (path.Length < root.Length)
        {
            return false;
        }

        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i < root.Length; i++)
        {
            if (!string.Equals(path[i], root[i], comparison))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyEntry(
        ReadOnlySpan<string> directory, DirNode? snapshot, WildcardSegment[] patterns, MatchContext context, string? exclude)
    {
        // 先查掃描結果中保留的項目（不需要系統呼叫）
        if (snapshot is not null)
        {
            foreach (var pattern in patterns)
            {
                foreach (var dir in snapshot.Dirs)
                {
                    if (!ReferenceEquals(dir.Name, exclude) && pattern.IsMatch(dir.Name))
                    {
                        return true;
                    }
                }

                foreach (var file in snapshot.Files)
                {
                    if (!ReferenceEquals(file.Name, exclude) && pattern.IsMatch(file.Name))
                    {
                        return true;
                    }
                }
            }
        }

        var path = RulePath.Join(directory);
        foreach (var pattern in patterns)
        {
            if (context.Probe.HasEntry(path, pattern))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>把 <see cref="Rule"/> 編譯成 <see cref="CompiledRule"/>，同時收集問題。</summary>
internal static class RuleCompiler
{
    public static CompiledRule? Compile(Rule rule, PathTokens tokens, bool ignoreCase, List<RuleIssue> issues)
    {
        var match = rule.Match ?? new RuleMatch();
        var hadError = false;

        string[]? scopeRoot = null;
        if (rule.Scope?.Type == ScopeType.Location)
        {
            if (string.IsNullOrWhiteSpace(rule.Scope.Root))
            {
                issues.Add(RuleIssue.Error("scope.root", "scope 為 location 時必須指定 root（套用的資料夾）。"));
                hadError = true;
            }
            else
            {
                var expanded = tokens.Expand(rule.Scope.Root.Trim());
                if (!expanded.IsOk)
                {
                    issues.Add(new RuleIssue("scope.root", expanded.Error!, !expanded.Unavailable));
                    hadError |= !expanded.Unavailable;
                    return null; // 這個平台上不存在：規則不會生效
                }

                scopeRoot = RulePath.Split(expanded.Value!);
                if (!RulePath.IsAbsolute(scopeRoot))
                {
                    issues.Add(RuleIssue.Error("scope.root", $"root 必須是完整路徑：{rule.Scope.Root}"));
                    hadError = true;
                }
            }
        }

        var globs = CompileGlobs(match.PathGlob, "match.pathGlob", scopeRoot, tokens, ignoreCase, issues, ref hadError);
        var excludes = CompileGlobs(match.ExcludeGlob, "match.excludeGlob", scopeRoot, tokens, ignoreCase, issues, ref hadError);

        if (match.PathGlob is not { Count: > 0 })
        {
            issues.Add(RuleIssue.Error("match.pathGlob", "至少要有一個路徑樣式（pathGlob）。"));
            hadError = true;
        }
        else if (globs.Length == 0 && !hadError)
        {
            issues.Add(RuleIssue.Warning("match.pathGlob", "所有路徑樣式在這個作業系統上都不存在，這條規則不會比對任何項目。"));
        }

        Regex? regex = null;
        if (!string.IsNullOrWhiteSpace(match.NameRegex))
        {
            try
            {
                var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
                if (ignoreCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                regex = new Regex(match.NameRegex, options);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                issues.Add(RuleIssue.Error("match.nameRegex", $"正規表示式有誤：{ex.Message}"));
                hadError = true;
            }
        }

        HashSet<string>? extensions = null;
        if (match.Ext is { Count: > 0 })
        {
            extensions = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            for (var i = 0; i < match.Ext.Count; i++)
            {
                var ext = match.Ext[i]?.Trim();
                if (string.IsNullOrEmpty(ext) || ext == "." || ext.AsSpan().IndexOfAny("/\\*?") >= 0)
                {
                    issues.Add(RuleIssue.Error($"match.ext[{i}]", $"副檔名格式不正確：「{match.Ext[i]}」"));
                    hadError = true;
                    continue;
                }

                extensions.Add(ext.StartsWith('.') ? ext : "." + ext);
            }
        }

        var siblings = CompileNames(match.SiblingExists, "match.siblingExists", ignoreCase, issues, ref hadError);
        var children = CompileNames(match.ChildExists, "match.childExists", ignoreCase, issues, ref hadError);

        if (match.MinSize is < 0)
        {
            issues.Add(RuleIssue.Error("match.minSize", "minSize 不能是負數。"));
            hadError = true;
        }

        if (match.MinAgeDays is < 0 or > 36500)
        {
            issues.Add(RuleIssue.Error("match.minAgeDays", "minAgeDays 必須介於 0 到 36500 之間。"));
            hadError = true;
        }

        if (hadError)
        {
            return null;
        }

        var conditions = 1 // pathGlob
            + (match.Kind != MatchKind.Any ? 1 : 0)
            + (regex is not null ? 1 : 0)
            + (extensions is not null ? 1 : 0)
            + (match.MinSize is not null ? 1 : 0)
            + (match.MinAgeDays is not null ? 1 : 0)
            + (siblings.Length > 0 ? 1 : 0)
            + (children.Length > 0 ? 1 : 0)
            + (excludes.Length > 0 ? 1 : 0)
            + (scopeRoot is not null ? 1 : 0);

        return new CompiledRule
        {
            Rule = rule,
            Globs = globs,
            Excludes = excludes,
            NameRegex = regex,
            Extensions = extensions,
            Siblings = siblings,
            Children = children,
            ScopeRoot = scopeRoot,
            IgnoreCase = ignoreCase,
            ConditionCount = conditions,
        };
    }

    /// <summary>展開 token 並轉成 glob。相對樣式：location 規則以 root 為起點，global 規則可以出現在任何位置（前面加 <c>**/</c>）。</summary>
    public static GlobPattern? CompileGlob(
        string pattern, string[]? scopeRoot, PathTokens tokens, bool ignoreCase, out RuleIssue? issue, string field)
    {
        issue = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            issue = RuleIssue.Error(field, "路徑樣式不能是空白。");
            return null;
        }

        var expanded = tokens.Expand(pattern.Trim());
        if (!expanded.IsOk)
        {
            issue = new RuleIssue(field, expanded.Error!, !expanded.Unavailable);
            return null;
        }

        var text = expanded.Value!;
        var segments = RulePath.Split(text);
        if (segments.Length == 0)
        {
            issue = RuleIssue.Error(field, "路徑樣式不能是空白。");
            return null;
        }

        if (!RulePath.IsAbsolute(segments) && segments[0] != "**")
        {
            text = scopeRoot is not null
                ? string.Join('/', scopeRoot) + "/" + string.Join('/', segments)
                : "**/" + string.Join('/', segments);
        }

        try
        {
            return GlobPattern.Parse(text, ignoreCase);
        }
        catch (ArgumentException ex)
        {
            issue = RuleIssue.Error(field, ex.Message);
            return null;
        }
    }

    private static GlobPattern[] CompileGlobs(
        IReadOnlyList<string>? patterns, string field, string[]? scopeRoot, PathTokens tokens, bool ignoreCase,
        List<RuleIssue> issues, ref bool hadError)
    {
        if (patterns is not { Count: > 0 })
        {
            return [];
        }

        var result = new List<GlobPattern>(patterns.Count);
        for (var i = 0; i < patterns.Count; i++)
        {
            var glob = CompileGlob(patterns[i], scopeRoot, tokens, ignoreCase, out var issue, $"{field}[{i}]");
            if (issue is not null)
            {
                issues.Add(issue);
                hadError |= issue.IsError;
            }

            if (glob is not null)
            {
                result.Add(glob);
            }
        }

        return [.. result];
    }

    private static WildcardSegment[] CompileNames(
        IReadOnlyList<string>? names, string field, bool ignoreCase, List<RuleIssue> issues, ref bool hadError)
    {
        if (names is not { Count: > 0 })
        {
            return [];
        }

        var result = new List<WildcardSegment>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i]?.Trim();
            if (string.IsNullOrEmpty(name) || name.AsSpan().IndexOfAny('/', '\\') >= 0 || name is "." or "..")
            {
                issues.Add(RuleIssue.Error($"{field}[{i}]", $"只能填寫名稱（可用 * ?），不能包含路徑：「{names[i]}」"));
                hadError = true;
                continue;
            }

            result.Add(new WildcardSegment(name, ignoreCase));
        }

        return [.. result];
    }
}
