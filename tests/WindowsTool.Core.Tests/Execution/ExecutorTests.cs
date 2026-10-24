using WindowsTool.Core.Execution;
using WindowsTool.Core.Scanning;
using static WindowsTool.Core.Tests.Rules.TestRules;

namespace WindowsTool.Core.Tests.Execution;

public sealed class ExecutorTests
{
    [Fact]
    public async Task Quarantine_then_restore_inside_the_tool()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("proj/node_modules/lib/a.js", 1000);
        f.Tree.File("old.iso", 2000);

        var plan = f.Build(f.Dir("proj/node_modules", PlanAction.Quarantine, 1000), f.File("old.iso", PlanAction.Quarantine, 2000));
        var results = await f.ExecuteAsync(plan);

        results.ShouldAllBe(r => r.Outcome == ActionOutcome.Ok);
        Directory.Exists(f.Tree.PathOf("proj/node_modules")).ShouldBeFalse();
        System.IO.File.Exists(f.Tree.PathOf("old.iso")).ShouldBeFalse();

        var entries = f.Quarantine.List();
        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(e => e.PlanId == "plan-1" && System.IO.File.Exists(e.StoredPath) || Directory.Exists(e.StoredPath));

        var nm = entries.Single(e => e.Kind == ScanItemKind.Dir);
        f.Quarantine.Restore(nm.EntryId);
        System.IO.File.Exists(f.Tree.PathOf("proj/node_modules/lib/a.js")).ShouldBeTrue();
        f.Quarantine.List().Count.ShouldBe(1);
        f.Quarantine.List(includeClosed: true).Single(e => e.EntryId == nm.EntryId).Status.ShouldBe(QuarantineStatus.Restored);

        // 原位置已經有同名項目時不覆蓋
        var iso = f.Quarantine.List().Single();
        f.Tree.File("old.iso", 1);
        Should.Throw<IOException>(() => f.Quarantine.Restore(iso.EntryId)).Message.ShouldContain("同名");
        Should.Throw<KeyNotFoundException>(() => f.Quarantine.Restore("nope"));
    }

    [Fact]
    public async Task Recycle_move_and_delete()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("r.bin", 1);
        f.Tree.File("m.bin", 1);
        f.Tree.File("d/readonly.bin", 1, attributes: FileAttributes.ReadOnly);
        var dest = f.Tree.Dir("dest");

        var plan = f.Build(
            f.File("r.bin", PlanAction.Recycle),
            f.File("m.bin", PlanAction.Move, destination: dest),
            f.Dir("d", PlanAction.Delete));

        Should.Throw<InvalidOperationException>(() => f.ExecuteAsync(plan, "nope")).Message.ShouldContain("永久刪除");
        System.IO.File.Exists(f.Tree.PathOf("r.bin")).ShouldBeTrue(); // 沒有確認就完全不執行

        var results = await f.ExecuteAsync(plan, Plan.PermanentDeleteConfirmation);
        results.ShouldAllBe(r => r.Outcome == ActionOutcome.Ok, string.Join("; ", results.Select(r => r.Message)));
        f.Trash.Recycled.ShouldBe([f.Tree.PathOf("r.bin")]);
        System.IO.File.Exists(Path.Combine(dest, "m.bin")).ShouldBeTrue();
        Directory.Exists(f.Tree.PathOf("d")).ShouldBeFalse(); // 唯讀檔案也刪得掉
        results.Single(r => r.Action == PlanAction.Move).Undo!.To.ShouldBe(f.Tree.PathOf("m.bin"));
    }

    [Fact]
    public async Task A_locked_file_fails_without_stopping_the_rest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "只有 Windows 會強制檔案鎖定");
        using var f = new ExecutionFixture();
        f.Tree.File("a.bin", 1);
        var locked = f.Tree.File("b.bin", 1);
        f.Tree.File("c.bin", 1);
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var plan = f.Build(f.File("a.bin", PlanAction.Quarantine), f.File("b.bin", PlanAction.Quarantine), f.File("c.bin", PlanAction.Quarantine));
        plan.Items.Single(i => i.Path == locked).State.ShouldBe(PlanItemState.Warning);

        var results = await f.ExecuteAsync(plan);
        results.Single(r => r.Path == locked).Outcome.ShouldBe(ActionOutcome.Failed);
        results.Count(r => r.Outcome == ActionOutcome.Ok).ShouldBe(2);
        System.IO.File.Exists(locked).ShouldBeTrue();
        f.Quarantine.List().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Every_item_is_logged_including_skipped_and_blocked()
    {
        using var f = new ExecutionFixture(Protect("keep", "**/keep.bin"));
        f.Tree.File("ok.bin", 5);
        f.Tree.File("keep.bin", 1);
        f.Tree.File("dir/child.bin", 1);

        var plan = f.Build(f.File("ok.bin", PlanAction.Quarantine, 5), f.File("keep.bin", PlanAction.Recycle),
            f.Dir("dir", PlanAction.Recycle), f.File("dir/child.bin", PlanAction.Recycle));
        await f.ExecuteAsync(plan);

        var log = f.Log.Read(planId: "plan-1");
        log.Count.ShouldBe(4);
        var ok = log.Single(e => e.Path == f.Tree.PathOf("ok.bin"));
        ok.Result.ShouldBe(ActionOutcome.Ok);
        ok.Action.ShouldBe(LogAction.Quarantine);
        ok.Bytes.ShouldBe(5);
        ok.Undo!.Type.ShouldBe("quarantine");
        ok.Undo.EntryId.ShouldNotBeNull();
        log.Single(e => e.Path == f.Tree.PathOf("keep.bin")).Error!.ShouldContain("受保護");
        log.Single(e => e.Path == f.Tree.PathOf("dir/child.bin")).Result.ShouldBe(ActionOutcome.Skipped);
        f.Log.Read(planId: "other").ShouldBeEmpty();
        f.Log.Read(fromUtc: DateTime.UtcNow.AddMinutes(1)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Items_are_rechecked_right_before_execution()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("gone.bin", 1);
        f.Tree.File("later-protected.bin", 1);
        var plan = f.Build(f.File("gone.bin", PlanAction.Quarantine), f.File("later-protected.bin", PlanAction.Quarantine));
        plan.Summary.Ready.ShouldBe(2);

        System.IO.File.Delete(f.Tree.PathOf("gone.bin"));
        f.Rules = [Protect("late", "**/later-protected.bin")]; // 計畫建立後才新增的保護規則

        var results = await f.ExecuteAsync(plan);
        results.ShouldAllBe(r => r.Outcome == ActionOutcome.Skipped);
        results[0].Message!.ShouldContain("不存在");
        results[1].Message!.ShouldContain("受保護");
        System.IO.File.Exists(f.Tree.PathOf("later-protected.bin")).ShouldBeTrue();
    }

    [Fact]
    public async Task Commands_run_once_and_output_is_logged()
    {
        using var f = new ExecutionFixture();
        f.Tree.Dir("cache1");
        f.Tree.Dir("cache2");
        var plan = f.Build(
            new PlanItemRequest(f.Tree.PathOf("cache1"), ScanItemKind.Dir, 1, PlanAction.Command, CommandId: "nuget-clear"),
            new PlanItemRequest(f.Tree.PathOf("cache2"), ScanItemKind.Dir, 1, PlanAction.Command, CommandId: "nuget-clear"));

        var results = await f.ExecuteAsync(plan);
        f.Commands.Ran.ShouldBe(["nuget-clear"]);
        results.Count(r => r.Outcome == ActionOutcome.Ok).ShouldBe(1);
        f.Log.Read().Single(e => e.Result == ActionOutcome.Ok).Output!.ShouldContain("dotnet nuget locals all --clear");

        f.Commands = new FakeCommands(exitCode: 3);
        var failed = await f.ExecuteAsync(f.Build(new PlanItemRequest(f.Tree.PathOf("cache1"), ScanItemKind.Dir, 1, PlanAction.Command, CommandId: "nuget-clear")));
        failed[0].Outcome.ShouldBe(ActionOutcome.Failed);
        failed[0].Message!.ShouldContain("3");
    }

    [Fact]
    public async Task Cancellation_skips_the_remaining_items()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("a.bin", 1);
        f.Tree.File("b.bin", 1);
        var plan = f.Build(f.File("a.bin", PlanAction.Quarantine), f.File("b.bin", PlanAction.Quarantine));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var results = await Executor.ExecuteAsync(plan, f.Environment, f.Log, null, null, cts.Token);
        results.ShouldAllBe(r => r.Outcome == ActionOutcome.Skipped && r.Message!.Contains("停止"));
        System.IO.File.Exists(f.Tree.PathOf("a.bin")).ShouldBeTrue();
    }

    [Fact]
    public void Purge_permanently_removes_old_entries()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("a/x.bin", 3, attributes: FileAttributes.ReadOnly);
        f.Tree.File("b.bin", 4);
        f.Quarantine.Quarantine(f.Tree.PathOf("a"), ScanItemKind.Dir, 3, null);
        var b = f.Quarantine.Quarantine(f.Tree.PathOf("b.bin"), ScanItemKind.File, 4, null);

        f.Quarantine.Purge(DateTime.UtcNow.AddDays(-1)).Purged.ShouldBe(0); // 都還沒超過一天

        var result = f.Quarantine.Purge(DateTime.UtcNow.AddMinutes(1));
        result.Purged.ShouldBe(2);
        result.Bytes.ShouldBe(7);
        result.Failures.ShouldBeEmpty();
        System.IO.File.Exists(b.StoredPath).ShouldBeFalse();
        f.Quarantine.List().ShouldBeEmpty();
        f.Quarantine.List(includeClosed: true).ShouldAllBe(e => e.Status == QuarantineStatus.Purged);
    }

    [Fact]
    public void Quarantine_uses_the_same_volume_and_survives_a_damaged_manifest()
    {
        using var f = new ExecutionFixture();
        f.Tree.File("a.bin", 1);
        f.Quarantine.RootFor(f.Tree.PathOf("a.bin")).ShouldBe(f.Quarantine.DefaultRoot);
        if (OperatingSystem.IsWindows())
        {
            var otherDrive = Path.GetPathRoot(f.Tree.Root)!.StartsWith('C') ? @"Z:\x" : @"C:\x";
            f.Quarantine.RootFor(otherDrive).ShouldEndWith(QuarantineStore.VolumeFolderName);
        }

        var entry = f.Quarantine.Quarantine(f.Tree.PathOf("a.bin"), ScanItemKind.File, 1, null);
        System.IO.File.AppendAllText(f.Quarantine.ManifestPath, "{ broken line" + Environment.NewLine);
        f.Quarantine.List().Single().EntryId.ShouldBe(entry.EntryId);
    }

    [Fact]
    public void Action_log_skips_damaged_lines()
    {
        using var f = new ExecutionFixture();
        f.Log.Append(new ActionLogEntry { Path = "x", Action = LogAction.Restore, Result = ActionOutcome.Ok });
        System.IO.File.AppendAllText(Directory.GetFiles(f.Log.Directory).Single(), "not json" + Environment.NewLine);
        f.Log.Append(new ActionLogEntry { Path = "y", Action = LogAction.Purge, Result = ActionOutcome.Ok });

        var entries = f.Log.Read();
        entries.Select(e => e.Path).ShouldBe(["y", "x"]); // 新的在前
        f.Log.Read(limit: 1).Count.ShouldBe(1);
    }
}
