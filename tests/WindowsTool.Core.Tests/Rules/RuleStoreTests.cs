using WindowsTool.Core.Rules;
using WindowsTool.Core.Storage;
using WindowsTool.Core.Tests.Fixtures;
using static WindowsTool.Core.Tests.Rules.TestRules;

namespace WindowsTool.Core.Tests.Rules;

public sealed class RuleStoreTests
{
    private static readonly Rule[] Builtin =
    [
        Protect("protect-x", "C:/x"),
        Classify("builtin-y", "**/y", source: RuleSource.Builtin),
    ];

    private static RuleStore Create(TempTree tree) => new(tree.PathOf("data/rules.user.json"), Builtin, Tokens("C:"), "windows");

    [Fact]
    public void Starts_with_builtin_rules_when_no_file_exists()
    {
        using var tree = new TempTree();
        var store = Create(tree);
        store.Current.Builtin.Count.ShouldBe(2);
        store.Current.User.ShouldBeEmpty();
        store.Current.LoadError.ShouldBeNull();
        store.Current.Set.ActiveCount.ShouldBe(2);
    }

    [Fact]
    public void Add_update_delete_persist_and_bump_the_version()
    {
        using var tree = new TempTree();
        var store = Create(tree);
        var v0 = store.Current.Version;

        store.Add(Classify("user-0001", "**/a", "第一版"));
        store.Current.Version.ShouldBeGreaterThan(v0);
        System.IO.File.Exists(store.FilePath).ShouldBeTrue();
        System.IO.File.ReadAllText(store.FilePath).ShouldContain("第一版"); // 中文不跳脫

        store.Update("user-0001", Classify("user-0001", "**/a", "第二版"));
        System.IO.File.Exists(store.FilePath + ".bak").ShouldBeTrue();

        var reloaded = Create(tree);
        reloaded.Current.User.Single().Result.Category.ShouldBe("第二版");

        reloaded.Delete("user-0001");
        Create(tree).Current.User.ShouldBeEmpty();
    }

    [Fact]
    public void Rename_through_update_checks_for_duplicates()
    {
        using var tree = new TempTree();
        var store = Create(tree);
        store.Add(Classify("user-0001", "**/a"));
        store.Add(Classify("user-0002", "**/b"));

        Should.Throw<RuleStoreException>(() => store.Update("user-0001", Classify("user-0002", "**/a")));
        store.Update("user-0001", Classify("user-0003", "**/a"));
        store.Current.User.Select(r => r.Id).ShouldBe(["user-0003", "user-0002"]);
    }

    [Fact]
    public void Duplicate_ids_and_builtin_changes_are_rejected()
    {
        using var tree = new TempTree();
        var store = Create(tree);
        store.Add(Classify("user-0001", "**/a"));

        Should.Throw<RuleStoreException>(() => store.Add(Classify("user-0001", "**/b")));
        Should.Throw<RuleStoreException>(() => store.Add(Classify("builtin-y", "**/b")));
        Should.Throw<RuleStoreException>(() => store.Add(Classify("user-0009", "**/b", source: RuleSource.Builtin)));
        Should.Throw<RuleStoreException>(() => store.Update("builtin-y", Classify("builtin-y", "**/b")));
        Should.Throw<RuleStoreException>(() => store.Delete("builtin-y"));
        Should.Throw<KeyNotFoundException>(() => store.Delete("missing"));
        Should.Throw<RuleStoreException>(() => store.AddRange([Classify("user-0005", "**/c"), Classify("user-0005", "**/d")]));
        store.Current.User.Count.ShouldBe(1);
    }

    [Fact]
    public void Builtin_classify_rules_can_be_disabled_but_protect_rules_cannot()
    {
        using var tree = new TempTree();
        var store = Create(tree);

        store.SetEnabled("builtin-y", false);
        store.Current.Find("builtin-y")!.Enabled.ShouldBeFalse();
        store.Current.Set.IsActive("builtin-y").ShouldBeFalse();
        Create(tree).Current.Find("builtin-y")!.Enabled.ShouldBeFalse(); // 有寫入檔案

        store.SetEnabled("builtin-y", true);
        store.Current.Set.IsActive("builtin-y").ShouldBeTrue();

        Should.Throw<RuleStoreException>(() => store.SetEnabled("protect-x", false));
        store.SetEnabled("protect-x", true); // 啟用沒問題

        store.Add(Classify("user-0001", "**/a"));
        store.SetEnabled("user-0001", false);
        store.Current.Set.IsActive("user-0001").ShouldBeFalse();
    }

    [Fact]
    public void A_corrupt_file_is_reported_and_never_overwritten()
    {
        using var tree = new TempTree();
        var path = tree.PathOf("data/rules.user.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "{ not json");

        var store = new RuleStore(path, Builtin, Tokens("C:"), "windows");
        store.Current.LoadError.ShouldNotBeNull();
        store.Current.Set.ActiveCount.ShouldBe(2); // 內建規則照常運作
        Should.Throw<RuleStoreException>(() => store.Add(Classify("user-0001", "**/a")));
        System.IO.File.ReadAllText(path).ShouldBe("{ not json");

        System.IO.File.WriteAllText(path, """{ "version": 1, "rules": [] }""");
        store.Reload().LoadError.ShouldBeNull();
        store.Add(Classify("user-0001", "**/a"));
    }

    [Fact]
    public void Unknown_fields_make_the_file_invalid()
    {
        using var tree = new TempTree();
        var path = tree.PathOf("rules.user.json");
        System.IO.File.WriteAllText(path, """{ "rules": [ { "id": "user-1", "name": "x", "match": { "pathGlobs": ["**/x"] } } ] }""");
        new RuleStore(path, Builtin, Tokens("C:"), "windows").Current.LoadError!.ShouldContain("pathGlobs");
    }

    [Fact]
    public void Invalid_entries_in_the_file_are_skipped_with_warnings()
    {
        using var tree = new TempTree();
        var path = tree.PathOf("rules.user.json");
        System.IO.File.WriteAllText(path, """
            {
              // 允許註解
              "version": 1,
              "rules": [
                { "id": "user-1", "name": "ok", "match": { "pathGlob": ["**/x"] }, "result": { "category": "c", "risk": "low" } },
                { "id": "user-1", "name": "dup", "match": { "pathGlob": ["**/y"] } },
                { "id": "builtin-y", "name": "clash", "match": { "pathGlob": ["**/y"] } },
                { "id": "fake", "name": "fake", "source": "builtin", "match": { "pathGlob": ["**/z"] } },
              ],
              "disabledBuiltin": ["protect-x", "builtin-y", "gone"]
            }
            """);

        var snapshot = new RuleStore(path, Builtin, Tokens("C:"), "windows").Current;
        snapshot.User.Select(r => r.Id).ShouldBe(["user-1"]);
        snapshot.LoadWarnings.Count.ShouldBe(4);
        snapshot.Find("protect-x")!.Enabled.ShouldBeTrue();
        snapshot.Find("builtin-y")!.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Export_round_trips_and_ids_are_generated()
    {
        using var tree = new TempTree();
        var store = Create(tree);
        store.NextUserId().ShouldBe("user-0001");
        store.AddRange([Classify("user-0001", "**/a"), Classify("user-0002", "**/b")]);
        store.SetEnabled("builtin-y", false);
        store.NextUserId().ShouldBe("user-0003");

        var file = RuleJson.ParseFile(store.Export());
        file.Rules.Select(r => r.Id).ShouldBe(["user-0001", "user-0002"]);
        file.DisabledBuiltin.ShouldBe(["builtin-y"]);
        RuleJson.Serialize(file.Rules[0]).ShouldContain("\"pathGlob\"");
    }

    [Fact]
    public void App_paths_support_portable_mode_and_overrides()
    {
        using var tree = new TempTree();
        var exeDir = tree.Dir("app");

        AppPaths.Resolve(tree.PathOf("custom"), exeDir).DataDirectory.ShouldBe(tree.PathOf("custom"));

        var normal = AppPaths.Resolve(null, exeDir);
        normal.IsPortable.ShouldBeFalse();
        normal.DataDirectory.ShouldNotStartWith(exeDir);

        tree.File("app/portable", 0);
        var portable = AppPaths.Resolve(null, exeDir);
        portable.IsPortable.ShouldBeTrue();
        portable.DataDirectory.ShouldBe(Path.Combine(exeDir, AppPaths.PortableFolderName));
        portable.UserRulesFile.ShouldBe(Path.Combine(exeDir, AppPaths.PortableFolderName, "rules.user.json"));
    }
}
