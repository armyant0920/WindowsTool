using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;
using WindowsTool.Core.Tests.Fixtures;
using static WindowsTool.Core.Tests.Rules.TestRules;

namespace WindowsTool.Core.Tests.Rules;

public sealed class RuleValidatorTests
{
    private static RuleValidationResult Validate(Rule rule, RuleSet? current = null) =>
        RuleValidator.Validate(rule, new RuleValidationContext { Current = current ?? Set(), Probe = new FakeProbe() });

    private static IEnumerable<string> Errors(RuleValidationResult result) => result.Issues.Where(i => i.IsError).Select(i => i.Field);

    [Fact]
    public void A_reasonable_rule_is_valid()
    {
        var result = Validate(Classify("user-0001", "**/node_modules"));
        result.IsValid.ShouldBeTrue(string.Join("; ", result.Issues.Select(i => i.Message)));
        result.DryRun.ShouldBeNull();
    }

    [Theory]
    [InlineData("**")]
    [InlineData("**/*")]
    [InlineData("**/**")]
    [InlineData("*")]
    [InlineData("*.*")]
    [InlineData("C:/**")]
    [InlineData("C:/*")]
    [InlineData("C:/")]
    [InlineData("?:/**")]
    [InlineData("/")]
    [InlineData("/**")]
    [InlineData("**/*.*")]
    [InlineData("*/**")]
    [InlineData("{UserProfile}/**")]
    [InlineData("{UserProfile}")]
    [InlineData("C:/Users/**")]
    [InlineData("{LocalAppData}/*")]
    [InlineData("{Windows}/**")]
    public void Overly_broad_globs_are_blocked(string glob)
    {
        var result = Validate(Classify("user-0001", glob));
        result.IsValid.ShouldBeFalse();
        result.Issues.ShouldContain(i => i.IsError && i.Message.Contains("太寬"));
    }

    [Theory]
    [InlineData("**/node_modules")]
    [InlineData("**/*.log")]
    [InlineData("{LocalAppData}/Temp/**")]
    [InlineData("{UserProfile}/Downloads/**")]
    [InlineData("D:/projects/*/bin")]
    public void Specific_globs_are_allowed(string glob)
    {
        Validate(Classify("user-0001", glob)).Issues.ShouldNotContain(i => i.Message.Contains("太寬"));
    }

    [Fact]
    public void Protect_rules_may_be_broad()
    {
        Validate(Protect("user-protect", "D:/**") with { Source = RuleSource.User }).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("-dash")]
    [InlineData("has space")]
    public void Ids_must_follow_the_pattern(string id)
    {
        Errors(Validate(Classify(id, "**/x"))).ShouldContain("id");
    }

    [Fact]
    public void Structure_errors_are_reported_with_field_paths()
    {
        var rule = Classify("user-0001", "**/x") with
        {
            Name = "",
            Platforms = ["windows", "amiga"],
            Result = new RuleResult { Category = "", Risk = RiskLevel.Protected, SuggestedAction = SuggestedAction.Recycle, Method = "nuget-clear" },
        };

        var fields = Errors(Validate(rule)).ToList();
        fields.ShouldContain("name");
        fields.ShouldContain("platforms[1]");
        fields.ShouldContain("result.category");
        fields.ShouldContain("result.risk");
        fields.ShouldContain("result.method");
    }

    [Fact]
    public void Commands_must_be_whitelisted()
    {
        var rule = Classify("user-0001", "**/cache", action: SuggestedAction.Command);
        Errors(Validate(rule with { Result = rule.Result with { Method = "rm -rf /" } })).ShouldContain("result.method");
        Errors(Validate(rule with { Result = rule.Result with { Method = null } })).ShouldContain("result.method");
        Validate(rule with { Result = rule.Result with { Method = "npm-cache-clean" } }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Risky_auto_select_is_blocked()
    {
        Errors(Validate(Classify("user-0001", "**/x", risk: RiskLevel.High, autoSelect: true))).ShouldContain("result.autoSelect");
        Errors(Validate(Classify("user-0001", "**/x", action: SuggestedAction.Delete, autoSelect: true))).ShouldContain("result.autoSelect");
        Errors(Validate(Protect("user-p", "D:/x") with { Source = RuleSource.User, Result = new RuleResult { AutoSelect = true } })).ShouldContain("result.autoSelect");
        Errors(Validate(Caution("user-c", "D:/x") with { Source = RuleSource.User, Result = new RuleResult { Risk = RiskLevel.Low } })).ShouldContain("result.risk");
    }

    [Fact]
    public void Invalid_patterns_tokens_and_conditions_are_errors()
    {
        var rule = Classify("user-0001", "**/x") with
        {
            Match = new RuleMatch
            {
                PathGlob = ["{Nope}/x", "a**b/c", " "],
                NameRegex = "[",
                Ext = ["*.log"],
                MinSize = -1,
                MinAgeDays = -5,
                SiblingExists = ["a/b"],
                Kind = MatchKind.File,
                ChildExists = ["x"],
            },
        };

        var fields = Errors(Validate(rule)).ToList();
        fields.ShouldContain("match.pathGlob[0]");
        fields.ShouldContain("match.pathGlob[1]");
        fields.ShouldContain("match.pathGlob[2]");
        fields.ShouldContain("match.nameRegex");
        fields.ShouldContain("match.ext[0]");
        fields.ShouldContain("match.minSize");
        fields.ShouldContain("match.minAgeDays");
        fields.ShouldContain("match.siblingExists[0]");
        fields.ShouldContain("match.childExists");
    }

    [Fact]
    public void Missing_path_glob_and_location_root_are_errors()
    {
        var noGlob = Classify("user-0001", "**/x") with { Match = new RuleMatch() };
        Errors(Validate(noGlob)).ShouldContain("match.pathGlob");

        var noRoot = Classify("user-0001", "x") with { Scope = new RuleScope { Type = ScopeType.Location } };
        Errors(Validate(noRoot)).ShouldContain("scope.root");

        var relativeRoot = Classify("user-0001", "x") with { Scope = new RuleScope { Type = ScopeType.Location, Root = "relative" } };
        Errors(Validate(relativeRoot)).ShouldContain("scope.root");
    }

    [Fact]
    public void Ai_rules_must_be_location_scoped_unless_the_user_allows_global()
    {
        var rule = Classify("ai-0001", "**/x", source: RuleSource.Ai);
        Errors(Validate(rule)).ShouldContain("scope.type");
        RuleValidator.Validate(rule, new RuleValidationContext { Current = Set(), Probe = new FakeProbe(), AllowGlobalScopeForAi = true })
            .IsValid.ShouldBeTrue();
        Validate(rule with { Scope = new RuleScope { Type = ScopeType.Location, Root = "D:/proj" } }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Rules_inside_protected_paths_are_blocked_without_a_scan()
    {
        var current = Set(Protect("win", "C:/Windows", "C:/Windows/Temp/**"));

        Validate(Classify("user-0001", "C:/Windows/System32/*.log"), current).IsValid.ShouldBeFalse();
        Validate(Classify("user-0002", "C:/Windows/System32/drivers"), current).IsValid.ShouldBeFalse();
        Validate(Classify("user-0003", "C:/Windows/Temp/**"), current).IsValid.ShouldBeTrue(); // 例外路徑
    }

    [Fact]
    public void Platform_mismatch_is_a_warning()
    {
        var result = Validate(Classify("user-0001", "**/x") with { Platforms = ["linux"] });
        result.IsValid.ShouldBeTrue();
        result.Issues.ShouldContain(i => !i.IsError && i.Field == "platforms");
    }

    // ---- dry-run ----------------------------------------------------------

    [Fact]
    public async Task Dry_run_counts_hits_and_matches_brute_force()
    {
        using var tree = new TempTree();
        var old = DateTime.UtcNow.AddDays(-100);
        for (var i = 0; i < 5; i++)
        {
            tree.File($"proj{i}/package.json", 10, old);
            tree.File($"proj{i}/node_modules/lib/a.js", (i + 1) * 1024 * 1024, old);
            tree.File($"proj{i}/node_modules/lib/node_modules/b.js", 1024 * 1024, old); // 巢狀：只算一次大小
        }

        tree.File("loose/node_modules/c.js", 1024 * 1024, old); // 沒有 package.json

        var scan = await FileSystemScanner.ScanAsync(tree.Root, new ScanOptions(), new ScanProgress(), TestContext.Current.CancellationToken);
        var current = RuleSet.Create([], Tokens(tree.Root));
        var probe = new FileSystemEntryProbe();
        var rule = Classify("user-0001", "**/node_modules") with
        {
            Match = new RuleMatch { PathGlob = ["**/node_modules"], Kind = MatchKind.Dir, SiblingExists = ["package.json"] },
        };

        var result = RuleValidator.Validate(rule, new RuleValidationContext
        {
            Current = current,
            Scan = scan,
            Evaluation = RuleEngine.Evaluate(scan, current, probe, DateTime.UtcNow, TestContext.Current.CancellationToken),
            Probe = probe,
        });

        result.IsValid.ShouldBeTrue(string.Join("; ", result.Issues.Select(i => i.Message)));
        var dryRun = result.DryRun!;

        // 參考答案：逐一用 EvaluatePath 判斷
        var single = RuleSet.Create([rule], Tokens(tree.Root));
        var expected = scan.Items.Count(item =>
            RuleEngine.EvaluatePath(single, item.GetFullPath(), new ItemFacts(item.Name, item.Kind, item.Size, item.NewestWriteTicks), probe, DateTime.UtcNow)
                .Classification is not null);

        expected.ShouldBe(5); // lib/node_modules 旁邊沒有 package.json
        dryRun.Count.ShouldBe(expected);
        dryRun.Size.ShouldBe((1 + 2 + 3 + 4 + 5 + 5) * 1024L * 1024);
        dryRun.Samples.Count.ShouldBe(5);
        dryRun.Samples[0].Size.ShouldBe(6 * 1024L * 1024); // 依大小排序
        dryRun.ConflictCount.ShouldBe(0);
    }

    [Fact]
    public async Task Dry_run_blocks_rules_that_hit_protected_items_and_suggests_excludes()
    {
        using var tree = new TempTree();
        tree.File("Windows/Logs/old.log", 2 * 1024 * 1024);
        tree.File("app/old.log", 2 * 1024 * 1024);

        var scan = await FileSystemScanner.ScanAsync(tree.Root, new ScanOptions(), new ScanProgress(), TestContext.Current.CancellationToken);
        var current = RuleSet.Create([Protect("win", "{Windows}")], Tokens(tree.Root));
        var probe = new FakeProbe();
        var context = new RuleValidationContext
        {
            Current = current,
            Scan = scan,
            Evaluation = RuleEngine.Evaluate(scan, current, probe, DateTime.UtcNow, TestContext.Current.CancellationToken),
            Probe = probe,
        };

        var result = RuleValidator.Validate(Classify("user-0001", "**/*.log"), context);
        result.IsValid.ShouldBeFalse();
        result.DryRun!.ConflictCount.ShouldBe(1);
        result.DryRun.Conflicts[0].Path.ShouldBe(tree.PathOf("Windows/Logs/old.log"));
        result.DryRun.SuggestedExcludes.ShouldBe(["{Windows}/**"]);

        // 加上建議的排除條件之後就通過
        var fixedRule = Classify("user-0001", "**/*.log") with
        {
            Match = new RuleMatch { PathGlob = ["**/*.log"], ExcludeGlob = ["{Windows}/**"] },
        };
        var ok = RuleValidator.Validate(fixedRule, context);
        ok.IsValid.ShouldBeTrue();
        ok.DryRun!.Count.ShouldBe(1);

        // 資料夾裡面有受保護的項目也算衝突
        var parent = RuleValidator.Validate(Classify("user-0002", tree.Root.Replace('\\', '/') + "/*"), context);
        parent.DryRun!.Conflicts.ShouldContain(c => c.Path == tree.PathOf("Windows"));
    }

    [Fact]
    public async Task Dry_run_warns_when_nothing_matches()
    {
        using var tree = new TempTree();
        tree.File("a.txt", 1);
        var scan = await FileSystemScanner.ScanAsync(tree.Root, new ScanOptions(), new ScanProgress(), TestContext.Current.CancellationToken);
        var current = Set();
        var result = RuleValidator.Validate(Classify("user-0001", "**/nothing-here"), new RuleValidationContext
        {
            Current = current,
            Scan = scan,
            Evaluation = RuleEngine.Evaluate(scan, current, new FakeProbe(), DateTime.UtcNow, TestContext.Current.CancellationToken),
            Probe = new FakeProbe(),
        });

        result.IsValid.ShouldBeTrue();
        result.DryRun!.Count.ShouldBe(0);
        result.Issues.ShouldContain(i => !i.IsError && i.Field == "match");
    }
}
