using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;

namespace WindowsTool.Core.Tests.Rules;

/// <summary>建立測試用規則的捷徑。</summary>
internal static class TestRules
{
    public static Rule Classify(
        string id, string glob, string category = "cat", RiskLevel risk = RiskLevel.Low,
        RuleSource source = RuleSource.User, bool autoSelect = false, SuggestedAction action = SuggestedAction.Recycle) => new()
        {
            Id = id,
            Name = id,
            Source = source,
            Kind = RuleKind.Classify,
            Match = new RuleMatch { PathGlob = [glob] },
            Result = new RuleResult { Category = category, Risk = risk, SuggestedAction = action, AutoSelect = autoSelect },
        };

    public static Rule Protect(string id, string glob, params string[] excludes) => new()
    {
        Id = id,
        Name = id,
        Source = RuleSource.Builtin,
        Kind = RuleKind.Protect,
        Match = new RuleMatch { PathGlob = [glob], ExcludeGlob = excludes.Length > 0 ? excludes : null },
        Result = new RuleResult { Category = "protected-" + id },
    };

    public static Rule Caution(string id, string glob, RiskLevel risk = RiskLevel.High) => new()
    {
        Id = id,
        Name = id,
        Source = RuleSource.Builtin,
        Kind = RuleKind.Caution,
        Match = new RuleMatch { PathGlob = [glob] },
        Result = new RuleResult { Category = "caution-" + id, Risk = risk },
    };

    public static PathTokens Tokens(string root) => new(new Dictionary<string, string>
    {
        ["UserProfile"] = Path.Combine(root, "Users", "me"),
        ["LocalAppData"] = Path.Combine(root, "Users", "me", "AppData", "Local"),
        ["RoamingAppData"] = Path.Combine(root, "Users", "me", "AppData", "Roaming"),
        ["Temp"] = Path.Combine(root, "Users", "me", "AppData", "Local", "Temp"),
        ["ProgramData"] = Path.Combine(root, "ProgramData"),
        ["Windows"] = Path.Combine(root, "Windows"),
        ["ProgramFiles"] = Path.Combine(root, "Program Files"),
        ["ProgramFiles(x86)"] = Path.Combine(root, "Program Files (x86)"),
        ["SystemDrive"] = root,
        ["Documents"] = Path.Combine(root, "Users", "me", "Documents"),
        ["Desktop"] = Path.Combine(root, "Users", "me", "Desktop"),
        ["Pictures"] = Path.Combine(root, "Users", "me", "Pictures"),
        ["Videos"] = Path.Combine(root, "Users", "me", "Videos"),
        ["Music"] = Path.Combine(root, "Users", "me", "Music"),
    }, _ => null);

    public static RuleSet Set(params Rule[] rules) => RuleSet.Create(rules, Tokens("C:"), "windows", ignoreCase: true);

    public static ItemFacts Dir(string path, long size = 0, DateTime? newest = null) =>
        new(Path.GetFileName(path.Replace('\\', '/').TrimEnd('/').Split('/')[^1]), ScanItemKind.Dir, size, (newest ?? DateTime.UtcNow).Ticks);

    public static ItemFacts File(string path, long size = 0, DateTime? newest = null) =>
        new(path.Replace('\\', '/').Split('/')[^1], ScanItemKind.File, size, (newest ?? DateTime.UtcNow).Ticks);

    public static Classification? Eval(RuleSet set, string path, ItemFacts facts, IEntryProbe? probe = null) =>
        RuleEngine.EvaluatePath(set, path, facts, probe ?? new FakeProbe(), DateTime.UtcNow).Classification;
}

/// <summary>假的檔案系統查詢：資料夾 → 裡面的名稱。</summary>
internal sealed class FakeProbe : IEntryProbe
{
    private readonly Dictionary<string, string[]> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Calls { get; private set; }

    public FakeProbe With(string directory, params string[] names)
    {
        _entries[Key(directory)] = names;
        return this;
    }

    public bool HasEntry(string directoryPath, WildcardSegment pattern)
    {
        Calls++;
        return _entries.TryGetValue(Key(directoryPath), out var names) && names.Any(n => pattern.IsMatch(n));
    }

    private static string Key(string path) => RulePath.Join(RulePath.Split(path));
}
