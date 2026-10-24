using WindowsTool.Core.Execution;
using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;
using WindowsTool.Core.Tests.Rules;
using static WindowsTool.Core.Tests.Rules.TestRules;

namespace WindowsTool.Core.Tests.Execution;

public sealed class PlanBuilderTests
{
    [Fact]
    public void Ready_items_are_summarized()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("a/x.tmp", 100);
        f.Tree.File("b.tmp", 50);

        var plan = f.Build(f.Dir("a", PlanAction.Recycle, 100), f.File("b.tmp", PlanAction.Quarantine, 50));

        plan.Items.ShouldAllBe(i => i.State == PlanItemState.Ready);
        plan.Summary.Ready.ShouldBe(2);
        plan.Summary.Bytes.ShouldBe(150);
        plan.Summary.RequiresConfirmation.ShouldBeFalse();
    }

    [Fact]
    public void Protected_items_are_always_blocked()
    {
        using var f = new ExecutionFixture(Protect("keep", "{UserProfile}/keep"), Protect("keep-files", "**/keep.txt"));
        f.Tree.File("Users/me/keep/inner/a.bin", 10);
        f.Tree.File("work/deep/keep.txt", 1); // 小檔案：掃描時不一定會保留，執行前要逐一檢查
        f.Tree.File("work/other.bin", 1);
        f.Tree.File("clean/x.bin", 1);

        var plan = f.Build(
            f.Dir("Users/me/keep", PlanAction.Delete),
            f.File("Users/me/keep/inner/a.bin", PlanAction.Recycle),
            f.Dir("work", PlanAction.Quarantine),
            f.Dir("clean", PlanAction.Quarantine));

        Item(plan, "Users/me/keep", f).State.ShouldBe(PlanItemState.Blocked);
        Item(plan, "Users/me/keep", f).Reason!.ShouldContain("受保護");
        Item(plan, "Users/me/keep/inner/a.bin", f).State.ShouldBe(PlanItemState.Blocked); // 從上層延伸
        Item(plan, "work", f).Reason!.ShouldContain("keep.txt");
        Item(plan, "clean", f).State.ShouldBe(PlanItemState.Ready);
        plan.Summary.RequiresConfirmation.ShouldBeFalse(); // 被擋下的永久刪除不需要確認
    }

    [Fact]
    public void Critical_folders_missing_items_and_kind_changes_are_blocked()
    {
        using var f = new ExecutionFixture();
        f.Tree.Dir("Users/me/AppData/Local/Temp/x");
        f.Tree.File("file.txt", 1);

        var plan = f.Build(
            new PlanItemRequest(f.Tree.Root, ScanItemKind.Dir, 1, PlanAction.Recycle),          // SystemDrive token
            f.Dir("Users/me", PlanAction.Recycle),                                             // UserProfile
            f.Dir("Users", PlanAction.Recycle),                                                // UserProfile 的上一層
            f.Dir("Users/me/AppData/Local/Temp", PlanAction.Recycle),                          // Temp 本身
            f.Dir("Users/me/AppData/Local/Temp/x", PlanAction.Recycle),                        // 裡面的項目可以
            f.File("missing.txt", PlanAction.Recycle),
            f.Dir("file.txt", PlanAction.Recycle));

        plan.Items.Single(i => i.Path == f.Tree.Root).Reason!.ShouldContain("關鍵資料夾");
        Item(plan, "Users/me", f).Reason!.ShouldContain("關鍵資料夾");
        Item(plan, "Users", f).Reason!.ShouldContain("關鍵資料夾");
        Item(plan, "Users/me/AppData/Local/Temp", f).State.ShouldBe(PlanItemState.Blocked);
        Item(plan, "Users/me/AppData/Local/Temp/x", f).State.ShouldBe(PlanItemState.Ready);
        Item(plan, "missing.txt", f).Reason!.ShouldContain("不存在");
        Item(plan, "file.txt", f).Reason!.ShouldContain("類型");
    }

    [Fact]
    public void Links_are_blocked()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("target/a.bin", 1);
        if (!f.Tree.TryCreateDirectoryLink("link", "target"))
        {
            Assert.Skip("無法建立目錄連結");
        }

        var plan = f.Build(f.Dir("link", PlanAction.Delete));
        plan.Items[0].State.ShouldBe(PlanItemState.Blocked);
        plan.Items[0].Reason!.ShouldContain("連結");
    }

    [Fact]
    public void Children_of_selected_folders_are_skipped_and_duplicates_removed()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("cache/a/b.bin", 1);
        f.Tree.File("cache/c.bin", 1);

        var plan = f.Build(
            f.File("cache/a/b.bin", PlanAction.Recycle),
            f.Dir("cache", PlanAction.Recycle),
            f.File("cache/c.bin", PlanAction.Recycle),
            f.Dir("cache", PlanAction.Recycle));

        plan.Items.Count.ShouldBe(3);
        plan.Items[0].Path.ShouldBe(f.Tree.PathOf("cache"));
        plan.Items[0].State.ShouldBe(PlanItemState.Ready);
        plan.Items.Skip(1).ShouldAllBe(i => i.State == PlanItemState.Skipped && i.Reason!.Contains("上層"));
    }

    [Fact]
    public void Blocked_parents_do_not_hide_their_children()
    {
        using var f = new ExecutionFixture(Protect("keep", "**/keep.txt"));
        f.Tree.File("mixed/keep.txt", 1);
        f.Tree.File("mixed/junk.bin", 1);

        var plan = f.Build(f.Dir("mixed", PlanAction.Recycle), f.File("mixed/junk.bin", PlanAction.Recycle));
        Item(plan, "mixed", f).State.ShouldBe(PlanItemState.Blocked);
        Item(plan, "mixed/junk.bin", f).State.ShouldBe(PlanItemState.Ready);
    }

    [Fact]
    public void Commands_are_whitelisted_and_run_once()
    {
        using var f = new ExecutionFixture();
        f.Tree.Dir("a");
        f.Tree.Dir("b");

        PlanItemRequest Command(string relative, string? id) =>
            new(f.Tree.PathOf(relative), ScanItemKind.Dir, 1, PlanAction.Command, CommandId: id);

        var plan = f.Build(Command("a", "npm-cache-clean"), Command("b", "npm-cache-clean"), Command("a/x", "rm-rf"), Command("b/y", "wsl-compact"));

        plan.Items.Count(i => i.CommandId == "npm-cache-clean" && i.State == PlanItemState.Ready).ShouldBe(1);
        plan.Items.Count(i => i.CommandId == "npm-cache-clean" && i.State == PlanItemState.Skipped).ShouldBe(1);
        plan.Items.Single(i => i.CommandId == "rm-rf").Reason!.ShouldContain("白名單");
        plan.Items.Single(i => i.CommandId == "wsl-compact").State.ShouldBe(PlanItemState.Blocked);
        plan.Summary.Commands.ShouldBe(["npm-cache-clean"]);
        plan.Summary.Bytes.ShouldBe(0);
    }

    [Fact]
    public void Commands_need_the_tool_and_sometimes_elevation()
    {
        using var f = new ExecutionFixture();
        var request = new PlanItemRequest(f.Tree.Root, ScanItemKind.Dir, 1, PlanAction.Command, CommandId: "dism-component-cleanup");
        var dism = PlanBuilder.Check(request, f.Environment, TestContext.Current.CancellationToken);
        if (RuleSet.CurrentPlatform == "windows")
        {
            dism.Reason!.ShouldContain("系統管理員");
            f.IsElevated = true;
            PlanBuilder.Check(request, f.Environment, TestContext.Current.CancellationToken).State.ShouldBe(PlanItemState.Ready);
        }

        f.Commands = new FakeCommands(available: false);
        PlanBuilder.Check(request with { CommandId = "pip-cache-purge" }, f.Environment, TestContext.Current.CancellationToken).Reason!.ShouldContain("找不到 pip");
    }

    [Fact]
    public void Move_checks_destination()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("src/a.bin", 1);
        f.Tree.File("src/b.bin", 1);
        f.Tree.File("dest/b.bin", 1);
        var dest = f.Tree.PathOf("dest");

        PlanBuilder.Check(f.File("src/a.bin", PlanAction.Move, destination: dest), f.Environment, TestContext.Current.CancellationToken).State.ShouldBe(PlanItemState.Ready);
        PlanBuilder.Check(f.File("src/b.bin", PlanAction.Move, destination: dest), f.Environment, TestContext.Current.CancellationToken).Reason!.ShouldContain("同名");
        PlanBuilder.Check(f.File("src/a.bin", PlanAction.Move), f.Environment, TestContext.Current.CancellationToken).Reason!.ShouldContain("指定");
        PlanBuilder.Check(f.File("src/a.bin", PlanAction.Move, destination: f.Tree.PathOf("nope")), f.Environment, TestContext.Current.CancellationToken).Reason!.ShouldContain("找不到");
        PlanBuilder.Check(f.Dir("src", PlanAction.Move, destination: f.Tree.PathOf("src")), f.Environment, TestContext.Current.CancellationToken).Reason!.ShouldContain("裡面");
    }

    [Fact]
    public void Recycle_and_quarantine_availability_is_checked()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("a.bin", 1);
        f.Trash = new FakeTrash(f.Tree.PathOf("_trash"), supported: false);
        PlanBuilder.Check(f.File("a.bin", PlanAction.Recycle), f.Environment, TestContext.Current.CancellationToken).State.ShouldBe(PlanItemState.Blocked);

        // 已經在隔離區裡的項目不能再隔離
        var entry = f.Quarantine.Quarantine(f.Tree.PathOf("a.bin"), ScanItemKind.File, 1, null);
        PlanBuilder.Check(new PlanItemRequest(entry.StoredPath, ScanItemKind.File, 1, PlanAction.Quarantine), f.Environment, TestContext.Current.CancellationToken)
            .Reason!.ShouldContain("隔離區");
    }

    [Fact]
    public void Locked_files_get_a_warning()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "只有 Windows 會強制檔案鎖定");
        using var f = new ExecutionFixture();
        var path = f.Tree.File("locked.bin", 10);
        using var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var (state, reason) = PlanBuilder.Check(f.File("locked.bin", PlanAction.Recycle), f.Environment, TestContext.Current.CancellationToken);
        state.ShouldBe(PlanItemState.Warning);
        reason!.ShouldContain("使用");
    }

    [Fact]
    public void Delete_requires_confirmation()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("a.bin", 1);
        f.Build(f.File("a.bin", PlanAction.Delete)).Summary.RequiresConfirmation.ShouldBeTrue();
    }

    [Fact]
    public void Protected_check_only_walks_when_a_rule_could_match_inside()
    {
        using var f = new ExecutionFixture(Protect("win", "{Windows}"));
        f.Tree.File("big/a/b/c.bin", 1);
        PlanBuilder.FindProtectedInside(f.Tree.PathOf("big"), f.Environment, TestContext.Current.CancellationToken).ShouldBeNull();

        f.Rules = [Protect("win", "{Windows}"), Protect("any-key", "**/*.key")];
        f.Tree.File("big/a/secret.key", 1);
        PlanBuilder.FindProtectedInside(f.Tree.PathOf("big"), f.Environment, TestContext.Current.CancellationToken).ShouldBe(f.Tree.PathOf("big/a/secret.key"));

        var limited = new PlanEnvironment
        {
            Rules = f.Environment.Rules,
            Probe = new FakeProbe(),
            Trash = f.Trash,
            Quarantine = f.Quarantine,
            Commands = f.Commands,
            MaxEntriesToInspect = 1,
        };
        PlanBuilder.FindProtectedInside(f.Tree.PathOf("big"), limited, TestContext.Current.CancellationToken)!.ShouldContain("項目太多");
    }

    private static PlanItem Item(Plan plan, string relative, ExecutionFixture f) =>
        plan.Items.Single(i => i.Path == f.Tree.PathOf(relative));
}
