using WindowsTool.Core.Scanning;
using WindowsTool.Core.Tests.Fixtures;

namespace WindowsTool.Core.Tests.Scanning;

public sealed class ScanQueryTests : IAsyncLifetime
{
    private const long MB = 1024 * 1024;
    private static readonly DateTime Now = new(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);

    private readonly TempTree _tree = new();
    private ScanResult _result = null!;

    public async ValueTask InitializeAsync()
    {
        _tree.File("video/movie.mp4", 50 * MB, Now.AddDays(-400));
        _tree.File("video/clip.MKV", 5 * MB, Now.AddDays(-10));
        _tree.File("docs/report.pdf", 2 * MB, Now.AddDays(-200));
        _tree.File("docs/notes.txt", 100, Now.AddDays(-1));
        _tree.File("root.log", 3 * MB, Now.AddDays(-100));
        _tree.Dir("empty");

        _result = await FileSystemScanner.ScanAsync(
            _tree.Root, new ScanOptions(), new ScanProgress(), TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _tree.Dispose();
        return ValueTask.CompletedTask;
    }

    private ItemPage Run(ItemQuery query) => ScanQuery.Run(_result, query, Now);

    private static string[] Names(ItemPage page) => [.. page.Items.Select(i => i.Name)];

    [Fact]
    public void Lists_children_sorted_by_size_descending_by_default()
    {
        var page = Run(new ItemQuery());

        Names(page).ShouldBe(["video", "root.log", "docs", "empty"]);
        page.Total.ShouldBe(4);
    }

    [Fact]
    public void Lists_children_of_a_given_directory()
    {
        var docs = _result.Root.Dirs.Single(d => d.Name == "docs");

        var page = Run(new ItemQuery { ParentId = docs.Id, Sort = ItemSort.Name, Descending = false });

        Names(page).ShouldBe(["notes.txt", "report.pdf"]);
    }

    [Fact]
    public void Flat_mode_finds_largest_files_in_the_whole_scan()
    {
        var page = Run(new ItemQuery { Flat = true, Kind = ItemKindFilter.File, MinSize = 1 * MB });

        Names(page).ShouldBe(["movie.mp4", "clip.MKV", "root.log", "report.pdf"]);
    }

    [Fact]
    public void Filters_by_extension_case_insensitively_with_or_without_dot()
    {
        var page = Run(new ItemQuery { Flat = true, Extensions = ["mp4", ".mkv"] });

        Names(page).ShouldBe(["movie.mp4", "clip.MKV"]);
    }

    [Fact]
    public void Filters_by_name()
    {
        Names(Run(new ItemQuery { Flat = true, NameContains = "RE" })).ShouldBe(["report.pdf"]);
    }

    [Fact]
    public void Filters_by_age_using_newest_write_in_subtree()
    {
        var page = Run(new ItemQuery { Flat = true, Kind = ItemKindFilter.File, OlderThanDays = 150 });
        Names(page).ShouldBe(["movie.mp4", "report.pdf"]);

        // video 資料夾裡有 10 天前修改的檔案，所以整個資料夾不算「舊」
        var dirs = Run(new ItemQuery { Kind = ItemKindFilter.Dir, OlderThanDays = 5 });
        Names(dirs).ShouldNotContain("video");
        Names(dirs).ShouldNotContain("docs");
    }

    [Fact]
    public void Pages_results()
    {
        var page = Run(new ItemQuery { Flat = true, Kind = ItemKindFilter.File, Offset = 1, Limit = 2 });

        Names(page).ShouldBe(["clip.MKV", "root.log"]);
        page.Total.ShouldBe(5);
    }

    [Fact]
    public void Unknown_parent_throws()
    {
        Should.Throw<KeyNotFoundException>(() => Run(new ItemQuery { ParentId = 9999 }));

        var file = _result.Items.OfType<FileNode>().First();
        Should.Throw<KeyNotFoundException>(() => Run(new ItemQuery { ParentId = file.Id }));
    }
}
