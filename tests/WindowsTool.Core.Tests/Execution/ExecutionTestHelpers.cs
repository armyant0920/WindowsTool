using WindowsTool.Core.Execution;
using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;
using WindowsTool.Core.Tests.Fixtures;
using WindowsTool.Core.Tests.Rules;

namespace WindowsTool.Core.Tests.Execution;

/// <summary>測試用的資源回收筒：搬到 fixture 裡的 trash 資料夾。</summary>
internal sealed class FakeTrash(string folder, bool supported = true) : ITrash
{
    public bool IsSupported => supported;

    public List<string> Recycled { get; } = [];

    public string? CannotRecycleReason(string path) => supported ? null : "不支援";

    public TrashResult Recycle(string path)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(path));
        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }

        Recycled.Add(path);
        return new TrashResult(true, target, null);
    }
}

/// <summary>測試用的命令執行器：不執行任何真正的清理命令。</summary>
internal sealed class FakeCommands(bool available = true, int exitCode = 0) : ICommandRunner
{
    public List<string> Ran { get; } = [];

    public bool IsAvailable(CommandCatalog.CommandInfo command) => available;

    public Task<CommandResult> RunAsync(CommandCatalog.CommandInfo command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Ran.Add(command.Id);
        return Task.FromResult(new CommandResult(exitCode, $"ran {command.Display}", false));
    }
}

internal sealed class ExecutionFixture : IDisposable
{
    public ExecutionFixture(params Rule[] rules)
    {
        Tree = new TempTree();
        Trash = new FakeTrash(Tree.PathOf("_trash"));
        Commands = new FakeCommands();
        Quarantine = new QuarantineStore(Tree.PathOf("_data"));
        Log = new ActionLog(Tree.PathOf("_data/logs"));
        Rules = rules;
    }

    public TempTree Tree { get; }

    public FakeTrash Trash { get; set; }

    public FakeCommands Commands { get; set; }

    public QuarantineStore Quarantine { get; }

    public ActionLog Log { get; }

    public Rule[] Rules { get; set; }

    public bool IsElevated { get; set; }

    public PlanEnvironment Environment => new()
    {
        Rules = RuleSet.Create(Rules, TestRules.Tokens(Tree.Root)),
        Probe = new FileSystemEntryProbe(),
        Trash = Trash,
        Quarantine = Quarantine,
        Commands = Commands,
        IsElevated = IsElevated,
    };

    public PlanItemRequest File(string relative, PlanAction action, long size = 10, string? destination = null) =>
        new(Tree.PathOf(relative), ScanItemKind.File, size, action, Destination: destination);

    public PlanItemRequest Dir(string relative, PlanAction action, long size = 10, string? destination = null) =>
        new(Tree.PathOf(relative), ScanItemKind.Dir, size, action, Destination: destination);

    public Plan Build(params PlanItemRequest[] requests) => PlanBuilder.Build("plan-1", null, requests, Environment);

    public Task<IReadOnlyList<ItemResult>> ExecuteAsync(Plan plan, string? confirmation = null) =>
        Executor.ExecuteAsync(plan, Environment, Log, confirmation, null, TestContext.Current.CancellationToken);

    public void Dispose() => Tree.Dispose();
}
