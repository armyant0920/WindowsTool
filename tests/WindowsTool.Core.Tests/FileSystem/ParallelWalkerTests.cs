using System.Collections.Concurrent;
using System.IO.Enumeration;
using WindowsTool.Core.FileSystem;
using WindowsTool.Core.Tests.Fixtures;

namespace WindowsTool.Core.Tests.FileSystem;

public sealed class ParallelWalkerTests : IDisposable
{
    private readonly TempTree _tree = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tree.Dispose();

    /// <summary>記錄看到的檔案與目錄（節點 = 深度）。</summary>
    private sealed class RecordingHandler : IWalkHandler<int>
    {
        public ConcurrentBag<string> Files { get; } = [];

        public ConcurrentBag<(string Name, int Depth, bool CanDescend)> Dirs { get; } = [];

        public Func<string, bool>? ThrowOnFile { get; init; }

        public void OnDirectoryStarted(int node, string path)
        {
        }

        public void OnFile(int parent, ref FileSystemEntry entry, EntryFlags flags)
        {
            var name = entry.FileName.ToString();
            if (ThrowOnFile?.Invoke(name) == true)
            {
                throw new InvalidOperationException("handler bug");
            }

            Files.Add(name);
        }

        public bool OnDirectory(int parent, ref FileSystemEntry entry, EntryFlags flags, bool canDescend, out int child)
        {
            child = parent + 1;
            Dirs.Add((entry.FileName.ToString(), child, canDescend));
            return true;
        }

        public void OnLink(int parent, ref FileSystemEntry entry, EntryFlags flags)
        {
        }

        public void OnDirectoryError(int node, string path, Exception exception)
        {
        }

        public void OnDirectoryCompleted(int node)
        {
        }
    }

    [Fact]
    public async Task MaxDepth_limits_descent_but_still_reports_the_boundary_directory()
    {
        _tree.File("l1.txt", 1);
        _tree.File("a/l2.txt", 1);
        _tree.File("a/b/l3.txt", 1);
        _tree.File("a/b/c/l4.txt", 1);
        var handler = new RecordingHandler();

        await ParallelWalker.WalkAsync(_tree.Root, 0, handler, new WalkOptions { MaxDepth = 2 }, Ct);

        handler.Files.Order().ShouldBe(["l1.txt", "l2.txt"]);
        handler.Dirs.Single(d => d.Name == "a").CanDescend.ShouldBeTrue();
        handler.Dirs.Single(d => d.Name == "b").CanDescend.ShouldBeFalse();
        handler.Dirs.Any(d => d.Name == "c").ShouldBeFalse();
    }

    [Fact]
    public async Task Handler_exception_fails_the_walk_instead_of_hanging()
    {
        for (var i = 0; i < 20; i++)
        {
            _tree.File($"d{i}/file{i}.txt", 1);
        }

        var handler = new RecordingHandler { ThrowOnFile = name => name == "file7.txt" };

        var walk = ParallelWalker.WalkAsync(_tree.Root, 0, handler, new WalkOptions { MaxDegreeOfParallelism = 4 }, Ct);

        var completed = await Task.WhenAny(walk, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        completed.ShouldBeSameAs(walk);
        await Should.ThrowAsync<InvalidOperationException>(() => walk);
    }

    /// <summary>每開始一個目錄就計數，到指定數量時取消。</summary>
    private sealed class CancellingHandler(CancellationTokenSource cts, int cancelAfter) : IWalkHandler<int>
    {
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public void OnDirectoryStarted(int node, string path)
        {
            if (Interlocked.Increment(ref _started) == cancelAfter)
            {
                cts.Cancel();
            }
        }

        public void OnFile(int parent, ref FileSystemEntry entry, EntryFlags flags)
        {
        }

        public bool OnDirectory(int parent, ref FileSystemEntry entry, EntryFlags flags, bool canDescend, out int child)
        {
            child = parent + 1;
            return true;
        }

        public void OnLink(int parent, ref FileSystemEntry entry, EntryFlags flags)
        {
        }

        public void OnDirectoryError(int node, string path, Exception exception)
        {
        }

        public void OnDirectoryCompleted(int node)
        {
        }
    }

    [Fact]
    public async Task Cancellation_stops_promptly_even_when_many_directories_are_queued()
    {
        for (var i = 0; i < 60; i++)
        {
            _tree.Dir($"d{i:00}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var handler = new CancellingHandler(cts, cancelAfter: 2);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ParallelWalker.WalkAsync(_tree.Root, 0, handler, new WalkOptions { MaxDegreeOfParallelism = 1 }, cts.Token));

        // 根目錄 + 1 個子目錄之後就取消；不應該把佇列裡剩下的 59 個目錄都處理完
        handler.Started.ShouldBe(2);
    }

    [Fact]
    public void Network_paths_use_low_parallelism()
    {
        ParallelWalker.GetDefaultParallelism(@"\\server\share\folder").ShouldBe(OperatingSystem.IsWindows() ? 2 : ParallelWalker.GetDefaultParallelism(_tree.Root));
        ParallelWalker.GetDefaultParallelism(_tree.Root).ShouldBeInRange(2, 16);
    }
}
