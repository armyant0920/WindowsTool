using WindowsTool.Core.Scanning;
using WindowsTool.Core.Tests.Fixtures;

namespace WindowsTool.Core.Tests.Scanning;

public sealed class FileSystemScannerTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private readonly TempTree _tree = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _tree.Dispose();

    private Task<ScanResult> ScanAsync(ScanOptions? options = null) =>
        FileSystemScanner.ScanAsync(_tree.Root, options ?? new ScanOptions(), new ScanProgress(), Ct);

    private static DirNode Dir(DirNode parent, string name) => parent.Dirs.Single(d => d.Name == name);

    [Fact]
    public async Task Aggregates_sizes_and_counts_bottom_up()
    {
        _tree.File("a.txt", 100);
        _tree.File("big.bin", 2 * MB);
        _tree.File("sub1/x.log", 300);
        _tree.File("sub1/y.log", 400);
        _tree.File("sub1/deep/z.dat", 5000);
        _tree.Dir("sub2");

        var result = await ScanAsync();
        var root = result.Root;

        root.Size.ShouldBe(100 + (2 * MB) + 300 + 400 + 5000);
        root.TotalFileCount.ShouldBe(5);
        root.TotalDirCount.ShouldBe(3);
        root.OwnFileCount.ShouldBe(2);
        root.OwnSize.ShouldBe(100 + (2 * MB));

        var sub1 = Dir(root, "sub1");
        sub1.Size.ShouldBe(300 + 400 + 5000);
        sub1.TotalFileCount.ShouldBe(3);
        sub1.TotalDirCount.ShouldBe(1);
        Dir(sub1, "deep").Size.ShouldBe(5000);
        Dir(root, "sub2").Size.ShouldBe(0);
        Dir(root, "sub2").HasChildren.ShouldBeFalse();

        result.ErrorCount.ShouldBe(0);
    }

    [Fact]
    public async Task Assigns_unique_ids_and_builds_full_paths()
    {
        _tree.File("sub/deep/file.txt", 10);

        var result = await ScanAsync();

        var ids = result.Items.Select(i => i.Id).ToList();
        ids.ShouldBeUnique();
        result.Root.Id.ShouldBe(1);
        result.ItemCount.ShouldBe(4); // root、sub、deep 三個目錄 + 1 個檔案

        var file = result.Items.OfType<FileNode>().Single();
        result.Find(file.Id).ShouldBeSameAs(file);
        file.GetFullPath().ShouldBe(_tree.PathOf("sub/deep/file.txt"));
        result.Find(0).ShouldBeNull();
        result.Find(9999).ShouldBeNull();
    }

    [Fact]
    public async Task Keeps_large_files_and_top_small_files_only()
    {
        for (var i = 1; i <= 50; i++)
        {
            _tree.File($"many/f{i:00}.txt", i);
        }

        _tree.File("many/large.iso", 3 * MB);

        var result = await ScanAsync(new ScanOptions { TopFilesPerDirectory = 5 });
        var many = Dir(result.Root, "many");

        many.Files.Select(f => f.Name).Order().ShouldBe(
            ["f46.txt", "f47.txt", "f48.txt", "f49.txt", "f50.txt", "large.iso"]);
        many.OtherFileCount.ShouldBe(45);
        many.OtherFileSize.ShouldBe(Enumerable.Range(1, 45).Sum());

        // 沒有個別保留的檔案仍然計入彙總
        many.OwnFileCount.ShouldBe(51);
        many.Size.ShouldBe(Enumerable.Range(1, 50).Sum() + (3 * MB));
    }

    [Fact]
    public async Task Directory_links_are_not_followed()
    {
        _tree.File("target/payload.bin", 1000);
        if (!_tree.TryCreateDirectoryLink("link", "target"))
        {
            Assert.Skip("無法建立目錄連結");
        }

        var result = await ScanAsync();

        result.Root.Size.ShouldBe(1000); // 沒有因為連結而重複計算
        var link = result.Root.Files.Single(f => f.Name == "link");
        link.Kind.ShouldBe(ScanItemKind.Link);
        link.Flags.HasFlag(ScanItemFlags.ReparsePoint).ShouldBeTrue();
        link.Size.ShouldBe(0);
        result.Root.Dirs.Select(d => d.Name).ShouldBe(["target"]);
    }

    [Fact]
    public async Task Link_pointing_to_ancestor_does_not_loop()
    {
        _tree.File("a/b/file.txt", 10);
        if (!_tree.TryCreateDirectoryLink("a/b/back-to-root", ""))
        {
            Assert.Skip("無法建立目錄連結");
        }

        var result = await ScanAsync();

        result.Root.Size.ShouldBe(10);
        result.Root.TotalDirCount.ShouldBe(2);
    }

    [Fact]
    public async Task Unreadable_directories_are_flagged_and_counted()
    {
        _tree.File("ok/visible.txt", 10);
        _tree.File("locked/secret.txt", 500);
        if (!_tree.TryDenyListing("locked"))
        {
            Assert.Skip("無法設定拒絕存取");
        }

        var result = await ScanAsync();

        result.ErrorCount.ShouldBe(1);
        result.Errors.Single().Path.ShouldBe(_tree.PathOf("locked"));
        result.Errors.Single().Reason.ShouldBe("權限不足");

        var locked = Dir(result.Root, "locked");
        locked.Flags.HasFlag(ScanItemFlags.AccessDenied).ShouldBeTrue();
        locked.DeniedCount.ShouldBe(1);
        result.Root.DeniedCount.ShouldBe(1);
        result.Root.Size.ShouldBe(10); // 讀不到的部分不計入，UI 會提示「可能被低估」
    }

    [Fact]
    public async Task Excluded_directory_names_are_skipped_entirely()
    {
        _tree.File("keep/a.txt", 10);
        _tree.File("System Volume Information/b.txt", 20);
        _tree.File("nested/skip-me/c.txt", 30);

        var result = await ScanAsync(new ScanOptions
        {
            ExcludeDirectoryNames = ["System Volume Information", "SKIP-ME"],
        });

        result.Root.Size.ShouldBe(10);
        result.Root.Dirs.Select(d => d.Name).Order().ShouldBe(["keep", "nested"]);
    }

    [Fact]
    public async Task Hidden_files_can_be_excluded()
    {
        _tree.File("visible.txt", 10);
        if (OperatingSystem.IsWindows())
        {
            _tree.File("hidden.txt", 20, attributes: FileAttributes.Hidden);
        }
        else
        {
            _tree.File(".hidden.txt", 20); // Unix 上以 . 開頭的檔案視為隱藏
        }

        var included = await ScanAsync();
        var excluded = await ScanAsync(new ScanOptions { IncludeHidden = false });

        included.Root.Size.ShouldBe(30);
        included.Root.Files.Single(f => f.Size == 20).Flags.HasFlag(ScanItemFlags.Hidden).ShouldBeTrue();
        excluded.Root.Size.ShouldBe(10);
    }

    [Fact]
    public async Task Newest_write_time_comes_from_the_whole_subtree()
    {
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        _tree.File("proj/old.txt", 1, old);
        _tree.File("proj/src/recent.txt", 1, recent);

        var result = await ScanAsync();
        var proj = Dir(result.Root, "proj");

        proj.NewestWriteUtc.ShouldBeGreaterThanOrEqualTo(recent);
        proj.Files.Single().LastWriteUtc.ShouldBe(old);
    }

    [Fact]
    public async Task Collects_extension_stats()
    {
        _tree.File("a.log", 100);
        _tree.File("b.LOG", 200);
        _tree.File("c.tmp", 50);
        _tree.File("noext", 5);
        _tree.File("sub/d.log", 1000);

        var result = await ScanAsync();

        result.Root.OwnExtStats.ShouldBe(
        [
            new ExtStat(".log", 2, 300),
            new ExtStat(".tmp", 1, 50),
            new ExtStat("", 1, 5),
        ]);

        var subtree = ScanResult.GetSubtreeExtStats(result.Root);
        subtree[0].ShouldBe(new ExtStat(".log", 3, 1300));
    }

    [Fact]
    public async Task Many_extensions_are_folded_into_other()
    {
        for (var i = 0; i < 12; i++)
        {
            _tree.File($"f.e{i:00}", 100 + i);
        }

        var result = await ScanAsync();

        result.Root.OwnExtStats.Count.ShouldBe(8);
        result.Root.OwnExtStats[^1].Ext.ShouldBe(ExtStat.OtherExtension);
        result.Root.OwnExtStats.Sum(e => e.Count).ShouldBe(12);
        result.Root.OwnExtStats.Sum(e => e.Size).ShouldBe(result.Root.OwnSize);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task Totals_match_brute_force_enumeration(int parallelism)
    {
        var random = new Random(42);
        for (var d = 0; d < 40; d++)
        {
            var depth = random.Next(1, 5);
            var dir = string.Join('/', Enumerable.Range(0, depth).Select(level => $"d{(d + level) % 7}_{level}"));
            for (var f = 0; f < random.Next(0, 30); f++)
            {
                var path = $"{dir}/f{d}_{f}.bin";
                if (!System.IO.File.Exists(_tree.PathOf(path)))
                {
                    _tree.File(path, random.Next(0, 5000));
                }
            }
        }

        var result = await ScanAsync(new ScanOptions { MaxDegreeOfParallelism = parallelism, TopFilesPerDirectory = 2 });

        var files = new DirectoryInfo(_tree.Root).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var dirs = Directory.EnumerateDirectories(_tree.Root, "*", SearchOption.AllDirectories).Count();
        result.Root.Size.ShouldBe(files.Sum(f => f.Length));
        result.Root.TotalFileCount.ShouldBe(files.Count);
        result.Root.TotalDirCount.ShouldBe(dirs);
    }

    [Fact]
    public async Task Reports_progress()
    {
        _tree.File("a/1.txt", 10);
        _tree.File("b/2.txt", 20);
        var progress = new ScanProgress();

        await FileSystemScanner.ScanAsync(_tree.Root, new ScanOptions(), progress, Ct);

        var snapshot = progress.Snapshot();
        snapshot.Dirs.ShouldBe(3);
        snapshot.Files.ShouldBe(2);
        snapshot.Bytes.ShouldBe(30);
        snapshot.CurrentPath.ShouldNotBeNull();
    }

    [Fact]
    public async Task Can_be_cancelled()
    {
        _tree.File("a/1.txt", 10);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => FileSystemScanner.ScanAsync(_tree.Root, new ScanOptions(), new ScanProgress(), cts.Token));
    }

    [Fact]
    public async Task Missing_root_throws()
    {
        await Should.ThrowAsync<DirectoryNotFoundException>(
            () => FileSystemScanner.ScanAsync(_tree.PathOf("nope"), new ScanOptions(), new ScanProgress(), Ct));
    }
}
