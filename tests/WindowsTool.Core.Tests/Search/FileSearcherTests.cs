using System.Text.RegularExpressions;
using WindowsTool.Core.FileSystem;
using WindowsTool.Core.Search;
using WindowsTool.Core.Tests.Fixtures;

namespace WindowsTool.Core.Tests.Search;

public sealed class FileSearcherTests : IDisposable
{
    private static readonly DateTime Old = new(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Recent = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TempTree _tree = new();

    public FileSearcherTests()
    {
        _tree.File("proj1/src/main.cs", 100, Recent);
        _tree.File("proj1/src/report.cs", 200, Recent);
        _tree.File("proj1/docs/Report.PDF", 5000, Old);
        _tree.File("proj1/docs/readme.md", 50, Recent);
        _tree.File("proj1/build.log", 2000, Recent);
        _tree.File("proj2/src/util.cs", 300, Old);
        _tree.File("proj2/data_001.csv", 10, Recent);
        _tree.File("proj2/data_002.CSV", 20, Recent);
        _tree.File("proj2/data_1234.csv", 30, Recent);
        _tree.File("notes.txt", 5, Recent);
        _tree.File("re por.txt", 5, Recent);
        _tree.File("archive/old.log", 500, Old);
        _tree.File("archive/deep/deeper/deepest/report final.txt", 1, Old);
        _tree.Dir("empty-report-dir");
    }

    public void Dispose() => _tree.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<SearchResults> SearchAsync(SearchQuery query)
    {
        var results = new SearchResults(query.MaxResults);
        await FileSearcher.SearchAsync(query, results, Ct);
        return results;
    }

    private SearchQuery Query(string? pattern = null) => new() { Roots = [_tree.Root], Pattern = pattern };

    /// <summary>暴力列舉整個樹，再用 <paramref name="expected"/> 篩選，當作參考答案。</summary>
    private List<string> Reference(Func<FileSystemInfo, bool> expected, bool includeHidden = true, bool includeSystem = false)
    {
        var skip = (includeHidden ? 0 : FileAttributes.Hidden) | (includeSystem ? 0 : FileAttributes.System);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = skip, IgnoreInaccessible = true };
        return [.. new DirectoryInfo(_tree.Root).EnumerateFileSystemInfos("*", options).Where(expected).Select(i => i.FullName).Order()];
    }

    private static List<string> Paths(SearchResults results) => [.. results.GetRange(0).Select(h => h.Path).Order()];

    private async Task AssertMatchesReference(SearchQuery query, Func<FileSystemInfo, bool> expected)
    {
        var results = await SearchAsync(query);
        var reference = Reference(expected, query.IncludeHidden, query.IncludeSystem);

        reference.ShouldNotBeEmpty("參考答案不應該是空的，否則測試沒有意義");
        Paths(results).ShouldBe(reference);
        results.Truncated.ShouldBeFalse();
    }

    [Fact]
    public Task Substring_matches_reference() =>
        AssertMatchesReference(Query("report"), i => i.Name.Contains("report", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public Task Multiple_terms_match_reference() =>
        AssertMatchesReference(Query("re por"), i =>
            i.Name.Contains("re", StringComparison.OrdinalIgnoreCase) && i.Name.Contains("por", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public Task Wildcard_matches_reference() =>
        AssertMatchesReference(Query("*.PDF;*.md"), i =>
            i is FileInfo && (i.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || i.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public Task Regex_matches_reference() =>
        AssertMatchesReference(
            Query(@"^data_\d{3}\.csv$") with { Mode = NameMatchMode.Regex },
            i => Regex.IsMatch(i.Name, @"^data_\d{3}\.csv$", RegexOptions.IgnoreCase));

    [Fact]
    public Task Extension_and_size_match_reference() =>
        AssertMatchesReference(
            Query() with { Extensions = ["log", ".PDF"], MinSize = 1000 },
            i => i is FileInfo f && (f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) && f.Length >= 1000);

    [Fact]
    public Task Size_range_matches_reference() =>
        AssertMatchesReference(
            Query() with { MinSize = 100, MaxSize = 500 },
            i => i is FileInfo f && f.Length is >= 100 and <= 500);

    [Fact]
    public Task Directories_only_match_reference() =>
        AssertMatchesReference(
            Query("src") with { Kind = SearchKind.Dir },
            i => i is DirectoryInfo && i.Name.Contains("src", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public Task Files_only_match_reference() =>
        AssertMatchesReference(
            Query("report") with { Kind = SearchKind.File },
            i => i is FileInfo && i.Name.Contains("report", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public Task Date_range_matches_reference() =>
        AssertMatchesReference(
            Query() with { Kind = SearchKind.File, ModifiedBeforeUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            i => i is FileInfo && i.LastWriteTimeUtc < new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public Task Full_path_matching_matches_reference() =>
        AssertMatchesReference(
            Query("proj1 src") with { MatchFullPath = true },
            i => i.FullName.Contains("proj1", StringComparison.OrdinalIgnoreCase) && i.FullName.Contains("src", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Max_depth_limits_results()
    {
        var results = await SearchAsync(Query("report") with { MaxDepth = 2 });

        // 深度 1：empty-report-dir；深度 2 以內沒有其他名稱含 report 的項目（report.cs 在深度 3）
        results.GetRange(0).Select(h => h.Name).ShouldBe(["empty-report-dir"]);
    }

    [Fact]
    public async Task Results_contain_metadata()
    {
        var results = await SearchAsync(Query("Report.PDF"));

        var hit = results.GetRange(0).Single();
        hit.Name.ShouldBe("Report.PDF");
        hit.Kind.ShouldBe(SearchHitKind.File);
        hit.Size.ShouldBe(5000);
        hit.LastWriteUtc.ShouldBe(Old);
        hit.Directory.ShouldBe(_tree.PathOf("proj1/docs"));
    }

    [Fact]
    public async Task Stops_at_max_results_and_marks_truncated()
    {
        var results = await SearchAsync(Query() with { MaxResults = 3 });

        results.Count.ShouldBe(3);
        results.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task Hidden_and_system_files_follow_options()
    {
        if (OperatingSystem.IsWindows())
        {
            _tree.File("secret-hidden.txt", 1, attributes: FileAttributes.Hidden);
            _tree.File("secret-system.txt", 1, attributes: FileAttributes.System | FileAttributes.Hidden);
        }
        else
        {
            _tree.File(".secret-hidden.txt", 1);
        }

        var defaults = await SearchAsync(Query("secret"));
        var noHidden = await SearchAsync(Query("secret") with { IncludeHidden = false });

        defaults.GetRange(0).Select(h => h.Name.TrimStart('.')).ShouldBe(["secret-hidden.txt"]); // 預設不含系統檔
        noHidden.Count.ShouldBe(0);

        if (OperatingSystem.IsWindows())
        {
            var withSystem = await SearchAsync(Query("secret") with { IncludeSystem = true });
            withSystem.Count.ShouldBe(2);
        }
    }

    [Fact]
    public async Task Links_are_listed_but_not_followed()
    {
        if (!_tree.TryCreateDirectoryLink("link-to-proj1", "proj1"))
        {
            Assert.Skip("無法建立目錄連結");
        }

        var results = await SearchAsync(Query("main.cs"));
        var link = await SearchAsync(Query("link-to"));

        results.Count.ShouldBe(1); // 沒有透過連結重複找到
        link.GetRange(0).Single().Kind.ShouldBe(SearchHitKind.Link);
    }

    [Fact]
    public async Task Unreadable_directories_are_counted()
    {
        _tree.File("locked/report-inside.txt", 1);
        if (!_tree.TryDenyListing("locked"))
        {
            Assert.Skip("無法設定拒絕存取");
        }

        var results = await SearchAsync(Query("report"));

        results.DeniedCount.ShouldBe(1);
        results.DeniedPaths.ShouldBe([_tree.PathOf("locked")]);
        results.GetRange(0).ShouldNotContain(h => h.Name == "report-inside.txt");
    }

    [Fact]
    public async Task Overlapping_roots_do_not_produce_duplicates()
    {
        var query = new SearchQuery { Roots = [_tree.PathOf("proj1"), _tree.Root, _tree.Root], Pattern = "main.cs" };

        var results = await SearchAsync(query);

        results.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Multiple_separate_roots_are_all_searched()
    {
        var query = new SearchQuery { Roots = [_tree.PathOf("proj1"), _tree.PathOf("proj2")], Pattern = "*.cs" };

        var results = await SearchAsync(query);

        results.GetRange(0).Select(h => h.Name).Order().ShouldBe(["main.cs", "report.cs", "util.cs"]);
    }

    [Fact]
    public async Task Missing_root_throws()
    {
        var ex = await Should.ThrowAsync<DirectoryNotFoundException>(() =>
            SearchAsync(new SearchQuery { Roots = [_tree.PathOf("nope")] }));
        ex.Message.ShouldContain("nope");
    }

    [Fact]
    public async Task Can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            FileSearcher.SearchAsync(Query(), new SearchResults(100), cts.Token));
    }

    [Fact]
    public void PrepareRoots_rejects_empty_list()
    {
        Should.Throw<ArgumentException>(() => FileSearcher.PrepareRoots(["", "  "]));
        FileSearcher.PrepareRoots([_tree.Root + Path.DirectorySeparatorChar]).ShouldBe([_tree.Root]);
        PathUtil.IsSameOrUnder(_tree.PathOf("proj1"), _tree.Root).ShouldBeTrue();
    }
}
