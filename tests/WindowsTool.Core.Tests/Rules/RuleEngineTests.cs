using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;
using WindowsTool.Core.Tests.Fixtures;
using static WindowsTool.Core.Tests.Rules.TestRules;

namespace WindowsTool.Core.Tests.Rules;

public sealed class RuleEngineTests
{
    // ---- 優先序（PLAN 8.3） ------------------------------------------------

    [Fact]
    public void Protect_beats_every_other_rule()
    {
        var set = Set(
            Protect("p", "C:/Windows"),
            Classify("u", "C:/Windows/**", "user-cat", source: RuleSource.User, autoSelect: true));

        var result = Eval(set, "C:/Windows/System32", Dir("C:/Windows/System32"))!;
        result.Risk.ShouldBe(RiskLevel.Protected);
        result.RuleId.ShouldBe("p");
        result.Inherited.ShouldBeTrue();
        result.AutoSelect.ShouldBeFalse();
        result.Action.ShouldBe(SuggestedAction.Keep);
    }

    [Fact]
    public void User_beats_ai_beats_builtin_regardless_of_specificity()
    {
        var builtin = Classify("b", "C:/a/b/c/node_modules", "builtin", source: RuleSource.Builtin);
        var ai = Classify("a", "**/c/node_modules", "ai", source: RuleSource.Ai);
        var user = Classify("u", "**/node_modules", "user", source: RuleSource.User);

        Eval(Set(builtin, ai, user), "C:/a/b/c/node_modules", Dir("x/node_modules"))!.RuleId.ShouldBe("u");
        Eval(Set(builtin, ai), "C:/a/b/c/node_modules", Dir("x/node_modules"))!.RuleId.ShouldBe("a");
        Eval(Set(builtin), "C:/a/b/c/node_modules", Dir("x/node_modules"))!.RuleId.ShouldBe("b");
    }

    [Fact]
    public void Within_a_tier_the_most_specific_rule_wins()
    {
        var general = Classify("general", "**/*.log", "general");
        var longer = Classify("longer", "C:/app/logs/*.log", "longer");
        var moreConditions = Classify("conditions", "**/*.log", "conditions") with
        {
            Match = new RuleMatch { PathGlob = ["**/*.log"], Kind = MatchKind.File, MinSize = 1 },
        };

        // 條件數相同時比 glob 長度
        Eval(Set(general, longer), "C:/app/logs/a.log", File("a.log", 10))!.RuleId.ShouldBe("longer");
        // 條件數多的優先（條件數 × 1000 + glob 字面長度）
        Eval(Set(longer, moreConditions), "C:/app/logs/a.log", File("a.log", 10))!.RuleId.ShouldBe("conditions");
    }

    [Fact]
    public void Ties_go_to_the_newest_rule_then_the_id()
    {
        var older = Classify("older", "**/x", "older") with { Meta = new RuleMeta { CreatedUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) } };
        var newer = Classify("newer", "**/x", "newer") with { Meta = new RuleMeta { CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) } };
        Eval(Set(older, newer), "C:/a/x", Dir("x"))!.RuleId.ShouldBe("newer");

        Eval(Set(Classify("b-rule", "**/x"), Classify("a-rule", "**/x")), "C:/a/x", Dir("x"))!.RuleId.ShouldBe("a-rule");
    }

    // ---- 保護與繼承 --------------------------------------------------------

    [Fact]
    public void Protection_extends_to_descendants_except_excluded_paths()
    {
        var set = Set(
            Protect("win", "C:/Windows", "C:/Windows/Temp/**"),
            Classify("temp", "C:/Windows/Temp/**", "系統暫存"));

        Eval(set, "C:/Windows", Dir("Windows"))!.Risk.ShouldBe(RiskLevel.Protected);
        Eval(set, "C:/Windows/Temp", Dir("Temp"))!.Risk.ShouldBe(RiskLevel.Protected); // Temp 本身仍受保護

        var inside = Eval(set, "C:/Windows/Temp/abc", Dir("abc"))!;
        inside.Risk.ShouldBe(RiskLevel.Low);
        inside.Category.ShouldBe("系統暫存");

        Eval(set, "C:/Windows/Temp/abc/deep/file.tmp", File("file.tmp"))!.RuleId.ShouldBe("temp");
        Eval(set, "C:/Windows/System32/drivers", Dir("drivers"))!.Risk.ShouldBe(RiskLevel.Protected);
    }

    [Fact]
    public void Direct_protect_match_is_reported_over_inherited_one()
    {
        var set = Set(Protect("win", "C:/Windows"), Protect("winsxs", "C:/Windows/WinSxS"));
        var result = Eval(set, "C:/Windows/WinSxS", Dir("WinSxS"))!;
        result.RuleId.ShouldBe("winsxs");
        result.Inherited.ShouldBeFalse();
        Eval(set, "C:/Windows/WinSxS/x", Dir("x"))!.RuleId.ShouldBe("winsxs");
    }

    [Fact]
    public void Caution_raises_risk_and_disables_auto_select()
    {
        var set = Set(
            Caution("docs", "C:/Users/me/Documents"),
            Classify("nm", "**/node_modules", "deps", RiskLevel.Low, autoSelect: true));

        var inDocs = Eval(set, "C:/Users/me/Documents/proj/node_modules", Dir("node_modules"))!;
        inDocs.Category.ShouldBe("deps");
        inDocs.Risk.ShouldBe(RiskLevel.High);
        inDocs.AutoSelect.ShouldBeFalse();
        inDocs.CautionRuleId.ShouldBe("docs");

        var elsewhere = Eval(set, "C:/src/proj/node_modules", Dir("node_modules"))!;
        elsewhere.Risk.ShouldBe(RiskLevel.Low);
        elsewhere.AutoSelect.ShouldBeTrue();

        // 沒有分類規則時，提醒規則本身就是標註結果
        var plain = Eval(set, "C:/Users/me/Documents/report.docx", File("report.docx"))!;
        plain.RuleId.ShouldBe("docs");
        plain.Risk.ShouldBe(RiskLevel.High);
        plain.Inherited.ShouldBeTrue();
    }

    [Fact]
    public void Highest_caution_wins()
    {
        var set = Set(Caution("medium", "C:/a", RiskLevel.Medium), Caution("high", "C:/a/b", RiskLevel.High));
        Eval(set, "C:/a/b/c", Dir("c"))!.Risk.ShouldBe(RiskLevel.High);
        Eval(set, "C:/a/x", Dir("x"))!.Risk.ShouldBe(RiskLevel.Medium);
    }

    [Fact]
    public void Unmatched_items_have_no_classification()
    {
        Eval(Set(Classify("x", "**/node_modules")), "C:/a/b", Dir("b")).ShouldBeNull();
    }

    // ---- 各種條件 ----------------------------------------------------------

    [Fact]
    public void Kind_size_age_extension_and_name_conditions()
    {
        var now = DateTime.UtcNow;
        var rule = Classify("r", "**/*") with
        {
            Match = new RuleMatch
            {
                PathGlob = ["C:/logs/**"],
                Kind = MatchKind.File,
                Ext = ["log", ".TXT"],
                MinSize = 100,
                MinAgeDays = 30,
                NameRegex = "^app",
            },
        };
        var set = Set(rule);
        var old = now.AddDays(-31);

        Eval(set, "C:/logs/app1.log", File("app1.log", 100, old)).ShouldNotBeNull();
        Eval(set, "C:/logs/sub/APP2.txt", File("APP2.txt", 500, old)).ShouldNotBeNull();
        Eval(set, "C:/logs/app1.log", File("app1.log", 99, old)).ShouldBeNull();      // 太小
        Eval(set, "C:/logs/app1.log", File("app1.log", 100, now.AddDays(-29))).ShouldBeNull(); // 太新
        Eval(set, "C:/logs/app1.csv", File("app1.csv", 100, old)).ShouldBeNull();     // 副檔名
        Eval(set, "C:/logs/web.log", File("web.log", 100, old)).ShouldBeNull();       // 名稱
        Eval(set, "C:/logs/app.log", Dir("app.log", 100, old)).ShouldBeNull();        // 類型
        Eval(set, "C:/other/app1.log", File("app1.log", 100, old)).ShouldBeNull();    // 路徑
    }

    [Fact]
    public void Exclude_glob_removes_matches()
    {
        var rule = Classify("r", "**/bin") with
        {
            Match = new RuleMatch { PathGlob = ["**/bin"], ExcludeGlob = ["**/.git/**", "C:/keep/**"] },
        };
        var set = Set(rule);
        Eval(set, "C:/src/a/bin", Dir("bin")).ShouldNotBeNull();
        Eval(set, "C:/src/.git/x/bin", Dir("bin")).ShouldBeNull();
        Eval(set, "C:/keep/a/bin", Dir("bin")).ShouldBeNull();
    }

    [Fact]
    public void Sibling_and_child_conditions_use_the_probe()
    {
        var rule = Classify("nm", "**/node_modules") with
        {
            Match = new RuleMatch { PathGlob = ["**/node_modules"], Kind = MatchKind.Dir, SiblingExists = ["package.json"] },
        };
        var bins = Classify("bin", "**/bin") with
        {
            Match = new RuleMatch { PathGlob = ["**/bin"], SiblingExists = ["*.csproj", "*.fsproj"] },
        };
        var git = Classify("repo", "**/*") with
        {
            Match = new RuleMatch { PathGlob = ["C:/src/*"], Kind = MatchKind.Dir, ChildExists = [".git"] },
        };
        var set = Set(rule, bins, git);
        var probe = new FakeProbe()
            .With("C:/src/web", "package.json", "node_modules")
            .With("C:/src/api", "Api.csproj", "bin")
            .With("C:/src/repo", ".git", "README.md");

        Eval(set, "C:/src/web/node_modules", Dir("node_modules"), probe)!.RuleId.ShouldBe("nm");
        Eval(set, "C:/src/other/node_modules", Dir("node_modules"), probe).ShouldBeNull();
        Eval(set, "C:/src/api/bin", Dir("bin"), probe)!.RuleId.ShouldBe("bin");
        Eval(set, "C:/src/web/bin", Dir("bin"), probe).ShouldBeNull();
        Eval(set, "C:/src/repo", Dir("repo"), probe)!.RuleId.ShouldBe("repo");
        Eval(set, "C:/src/web", Dir("web"), probe).ShouldBeNull();
    }

    [Fact]
    public void Location_scope_limits_the_rule_and_anchors_relative_globs()
    {
        var rule = Classify("loc", "build/*.zip") with
        {
            Scope = new RuleScope { Type = ScopeType.Location, Root = "D:/proj" },
            Match = new RuleMatch { PathGlob = ["build/*.zip", "**/*.tmp"] },
        };
        var set = Set(rule);
        Eval(set, "D:/proj/build/a.zip", File("a.zip")).ShouldNotBeNull();
        Eval(set, "D:/proj/x/build/a.zip", File("a.zip")).ShouldBeNull();
        Eval(set, "D:/proj/x/y.tmp", File("y.tmp")).ShouldNotBeNull();
        Eval(set, "D:/other/y.tmp", File("y.tmp")).ShouldBeNull();
    }

    [Fact]
    public void Relative_globs_in_global_rules_match_anywhere()
    {
        var set = Set(Classify("g", "*.bak"));
        Eval(set, "C:/a/b/c.bak", File("c.bak")).ShouldNotBeNull();
        Eval(set, "C:/c.bak", File("c.bak")).ShouldNotBeNull();
    }

    [Fact]
    public void Tokens_are_expanded_in_globs_excludes_and_scope()
    {
        var rule = Classify("t", "{LocalAppData}/Temp/**") with
        {
            Match = new RuleMatch { PathGlob = ["{LocalAppData}/Temp/**"], ExcludeGlob = ["{Temp}/keep/**"] },
        };
        var set = Set(rule);
        Eval(set, "C:/Users/me/AppData/Local/Temp/x", Dir("x")).ShouldNotBeNull();
        Eval(set, "C:/Users/me/AppData/Local/Temp/keep/x", Dir("x")).ShouldBeNull();
    }

    [Fact]
    public void Disabled_rules_and_other_platforms_are_ignored()
    {
        var disabled = Classify("off", "**/x") with { Enabled = false };
        var linuxOnly = Classify("linux", "**/x") with { Platforms = ["linux"] };
        var set = Set(disabled, linuxOnly);
        set.ActiveCount.ShouldBe(0);
        Eval(set, "C:/a/x", Dir("x")).ShouldBeNull();
        set.IsActive("off").ShouldBeFalse();
    }

    [Fact]
    public void Rules_with_unavailable_tokens_are_skipped_without_errors()
    {
        var tokens = new PathTokens(new Dictionary<string, string> { ["Windows"] = "" }, _ => null);
        var set = RuleSet.Create([Protect("win", "{Windows}"), Classify("ok", "**/x")], tokens, "linux", ignoreCase: false);
        set.ActiveCount.ShouldBe(1);
        set.Issues["win"].ShouldAllBe(i => !i.IsError);
    }

    [Fact]
    public void Broken_rules_are_reported_and_skipped()
    {
        var broken = Classify("broken", "**/x") with { Match = new RuleMatch { PathGlob = ["**/x"], NameRegex = "(" } };
        var set = Set(broken, Classify("ok", "**/y"));
        set.ActiveCount.ShouldBe(1);
        set.Issues["broken"].ShouldContain(i => i.IsError && i.Field == "match.nameRegex");
    }

    // ---- 對掃描結果評估 ----------------------------------------------------

    [Fact]
    public async Task Evaluates_a_scan_with_protection_inheritance_and_contains_protected()
    {
        using var tree = new TempTree();
        var old = DateTime.UtcNow.AddDays(-10);
        tree.File("Windows/System32/kernel.dll", 2 * 1024 * 1024, old);
        tree.File("Windows/Temp/job/big.tmp", 3 * 1024 * 1024, old);
        tree.File("src/web/package.json", 10, old);
        tree.File("src/web/node_modules/lib/index.js", 1024 * 1024, old);
        tree.File("src/plain/node_modules/x.js", 1024 * 1024, old); // 沒有 package.json
        tree.File("Users/me/Documents/proj/package.json", 10, old);
        tree.File("Users/me/Documents/proj/node_modules/a.js", 1024 * 1024, old);

        var root = tree.Root;
        var set = RuleSet.Create(
            [
                Protect("win", "{Windows}", "{Windows}/Temp/**"),
                Caution("docs", "{Documents}"),
                Classify("temp", "{Windows}/Temp/**", "系統暫存", autoSelect: true),
                Classify("nm", "**/node_modules", "deps", autoSelect: true) with
                {
                    Match = new RuleMatch { PathGlob = ["**/node_modules"], Kind = MatchKind.Dir, SiblingExists = ["package.json"] },
                },
            ],
            Tokens(root));

        var scan = await FileSystemScanner.ScanAsync(root, new ScanOptions(), new ScanProgress(), TestContext.Current.CancellationToken);
        var evaluation = RuleEngine.Evaluate(scan, set, new FileSystemEntryProbe(), DateTime.UtcNow, TestContext.Current.CancellationToken);

        int Id(string relative) => scan.Items.Single(i => i.GetFullPath() == tree.PathOf(relative)).Id;

        evaluation.IsProtected(Id("Windows")).ShouldBeTrue();
        evaluation.IsProtected(Id("Windows/System32/kernel.dll")).ShouldBeTrue();
        evaluation.IsSelectable(Id("Windows/System32")).ShouldBeFalse();

        var job = evaluation.Get(Id("Windows/Temp/job"))!;
        job.Category.ShouldBe("系統暫存");
        evaluation.IsSelectable(Id("Windows/Temp/job")).ShouldBeTrue();
        evaluation.IsAutoSelected(Id("Windows/Temp/job")).ShouldBeTrue();

        // 根目錄與 Windows/Temp 裡面有受保護的項目：不可勾選
        evaluation.ContainsProtected(scan.Root.Id).ShouldBeTrue();
        evaluation.IsSelectable(scan.Root.Id).ShouldBeFalse();
        evaluation.IsSelectable(Id("src")).ShouldBeTrue();

        evaluation.Get(Id("src/web/node_modules"))!.RuleId.ShouldBe("nm");
        evaluation.Get(Id("src/plain/node_modules")).ShouldBeNull();

        var inDocs = evaluation.Get(Id("Users/me/Documents/proj/node_modules"))!;
        inDocs.Risk.ShouldBe(RiskLevel.High);
        evaluation.IsAutoSelected(Id("Users/me/Documents/proj/node_modules")).ShouldBeFalse();

        // 類別統計：同類別只算最上層
        var temp = evaluation.Categories.Single(c => c.Category == "系統暫存");
        temp.Count.ShouldBe(1); // job（底下的 big.tmp 不重複計算）
        temp.Size.ShouldBe(3 * 1024 * 1024);
        temp.AutoSelectCount.ShouldBe(1);
        evaluation.Categories.Single(c => c.Category == "deps" && c.Risk == RiskLevel.Low).Size.ShouldBe(1024 * 1024);
    }

    [Fact]
    public async Task Root_inside_a_protected_folder_inherits_protection()
    {
        using var tree = new TempTree();
        tree.File("Windows/System32/a.dll", 2 * 1024 * 1024);
        var set = RuleSet.Create([Protect("win", "{Windows}")], Tokens(tree.Root));

        var scan = await FileSystemScanner.ScanAsync(tree.PathOf("Windows/System32"), new ScanOptions(), new ScanProgress(), TestContext.Current.CancellationToken);
        var evaluation = RuleEngine.Evaluate(scan, set, new FakeProbe(), DateTime.UtcNow, TestContext.Current.CancellationToken);

        evaluation.IsProtected(scan.Root.Id).ShouldBeTrue();
        scan.Items.ShouldAllBe(i => evaluation.IsProtected(i.Id));
    }

    [Fact]
    public async Task Explain_lists_all_hits_and_marks_the_effective_one()
    {
        using var tree = new TempTree();
        tree.File("Users/me/Documents/proj/node_modules/a.js", 2 * 1024 * 1024);
        tree.File("Users/me/Documents/proj/package.json", 10);
        var set = RuleSet.Create(
            [
                Caution("docs", "{Documents}"),
                Classify("builtin-nm", "**/node_modules", "deps", source: RuleSource.Builtin),
                Classify("user-nm", "**/proj/node_modules", "mine"),
            ],
            Tokens(tree.Root));

        var scan = await FileSystemScanner.ScanAsync(tree.Root, new ScanOptions(), new ScanProgress(), TestContext.Current.CancellationToken);
        var id = scan.Items.Single(i => i.Name == "node_modules").Id;
        var hits = RuleEngine.Explain(scan, set, new FakeProbe(), DateTime.UtcNow, id);

        hits.Single(h => h.Rule.Id == "user-nm").Role.ShouldBe(RuleHitRole.Effective);
        hits.Single(h => h.Rule.Id == "builtin-nm").Role.ShouldBe(RuleHitRole.Overridden);
        var docs = hits.Single(h => h.Rule.Id == "docs");
        docs.Role.ShouldBe(RuleHitRole.Caution);
        docs.Inherited.ShouldBeTrue();

        Should.Throw<KeyNotFoundException>(() => RuleEngine.Explain(scan, set, new FakeProbe(), DateTime.UtcNow, 99999));
    }

    [Fact]
    public async Task Evaluation_can_be_cancelled()
    {
        using var tree = new TempTree();
        for (var i = 0; i < 300; i++)
        {
            tree.Dir($"d{i}");
        }

        var scan = await FileSystemScanner.ScanAsync(tree.Root, new ScanOptions(), new ScanProgress(), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Should.Throw<OperationCanceledException>(() =>
            RuleEngine.Evaluate(scan, Set(Classify("x", "**/x")), new FakeProbe(), DateTime.UtcNow, cts.Token));
    }

    [Fact]
    public void File_system_probe_finds_entries_by_name_or_wildcard()
    {
        using var tree = new TempTree();
        tree.File("p/App.csproj", 1);
        tree.Dir("p/.git");
        var probe = new FileSystemEntryProbe();
        var dir = tree.PathOf("p");

        probe.HasEntry(dir, new WildcardSegment("*.csproj", true)).ShouldBeTrue();
        probe.HasEntry(dir, new WildcardSegment(".git", true)).ShouldBeTrue();
        probe.HasEntry(dir, new WildcardSegment("*.sln", true)).ShouldBeFalse();
        probe.HasEntry(tree.PathOf("missing"), new WildcardSegment("*", true)).ShouldBeFalse();
    }
}
