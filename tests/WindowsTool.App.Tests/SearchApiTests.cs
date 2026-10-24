using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WindowsTool.App.Common;
using WindowsTool.App.Endpoints;
using WindowsTool.App.Scans;
using WindowsTool.App.Search;
using WindowsTool.App.Security;
using WindowsTool.App.Shell;

namespace WindowsTool.App.Tests;

/// <summary>記錄呼叫內容，不會真的開檔案總管。</summary>
public sealed class FakeShellLauncher : IShellLauncher
{
    public ConcurrentQueue<(string Action, string Path)> Calls { get; } = new();

    public void Reveal(string path) => Calls.Enqueue(("reveal", path));

    public void Open(string path) => Calls.Enqueue(("open", path));
}

public sealed class SearchApiTests : IClassFixture<AppFactory>, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeShellLauncher _shell = new();
    private readonly string _root;

    public SearchApiTests(AppFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IShellLauncher>(_shell)));

        _root = Path.Combine(Path.GetTempPath(), "WindowsTool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "report-folder"));
        WriteFile("docs/report-2026.pdf", 1000);
        WriteFile("docs/deep/report-old.txt", 10);
        WriteFile("docs/notes.txt", 20);
        WriteFile("=cmd.csv", 5);         // 用來測試 CSV 公式注入防護
        WriteFile("setup-report.exe", 50); // 用來測試不能直接開啟執行檔
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 暫存資料夾刪不掉不影響結果
        }
    }

    private void WriteFile(string relative, long size)
    {
        using var stream = File.Create(Path.Combine(_root, relative));
        stream.SetLength(size);
    }

    private HttpClient Client(bool authorized = true)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://127.0.0.1:{AppFactory.TestPort}"),
        });
        if (authorized)
        {
            var token = _factory.Services.GetRequiredService<LocalServerState>().Token;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    private static Uri U(string relative) => new(relative, UriKind.Relative);

    private static async Task<string> StartAsync(HttpClient client, StartSearchRequest request)
    {
        using var response = await client.PostAsJsonAsync(U("/api/search"), request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<StartSearchResponse>(Ct))!.SearchId;
    }

    /// <summary>讀完整個 SSE 串流，回傳所有事件。</summary>
    private static async Task<List<(string Event, SearchEventDto Data)>> ReadEventsAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync(U($"/api/search/{id}/events"), HttpCompletionOption.ResponseHeadersRead, Ct);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        var body = await response.Content.ReadAsStringAsync(Ct);

        var events = new List<(string, SearchEventDto)>();
        foreach (var block in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.First(l => l.StartsWith("event:", StringComparison.Ordinal))[6..].Trim();
            var data = string.Join('\n', lines.Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].TrimStart()));
            events.Add((name, JsonSerializer.Deserialize<SearchEventDto>(data, Json)!));
        }

        return events;
    }

    private static async Task<(string Id, List<SearchHitDto> Hits, SearchEventDto Done)> SearchAsync(HttpClient client, StartSearchRequest request)
    {
        var id = await StartAsync(client, request);
        var events = await ReadEventsAsync(client, id);
        events.Last().Event.ShouldBe("done");
        return (id, events.SelectMany(e => e.Data.Hits).ToList(), events.Last().Data);
    }

    [Fact]
    public async Task Search_streams_all_hits_and_ends_with_done()
    {
        using var client = Client();

        var (_, hits, done) = await SearchAsync(client, new StartSearchRequest([_root], "report"));

        done.Status.ShouldBe("completed");
        done.Truncated.ShouldBeFalse();
        done.Roots.ShouldBe([_root]);
        done.Progress.Matches.ShouldBe(4);
        hits.Select(h => h.Name).Order().ShouldBe(["report-2026.pdf", "report-folder", "report-old.txt", "setup-report.exe"]);
        hits.Select(h => h.Index).Order().ShouldBe([0, 1, 2, 3]); // 索引不重複、連續

        var pdf = hits.Single(h => h.Name == "report-2026.pdf");
        pdf.Kind.ShouldBe("file");
        pdf.Size.ShouldBe(1000);
        pdf.Directory.ShouldBe(Path.Combine(_root, "docs"));
        hits.Single(h => h.Name == "report-folder").Kind.ShouldBe("dir");
    }

    [Fact]
    public async Task Filters_are_passed_through()
    {
        using var client = Client();

        var (_, hits, _) = await SearchAsync(client, new StartSearchRequest(
            [_root], "*.txt", Kind: "file", MinSize: 15, MaxDepth: 2));

        hits.Select(h => h.Name).ShouldBe(["notes.txt"]); // report-old.txt 太小，也太深
    }

    [Fact]
    public async Task Max_results_truncates()
    {
        using var client = Client();

        var (_, hits, done) = await SearchAsync(client, new StartSearchRequest([_root], MaxResults: 2));

        hits.Count.ShouldBe(2);
        done.Truncated.ShouldBeTrue();
        done.MaxResults.ShouldBe(2);
    }

    [Fact]
    public async Task Get_returns_hits_from_index()
    {
        using var client = Client();
        var (id, _, _) = await SearchAsync(client, new StartSearchRequest([_root], "report"));

        var status = await client.GetFromJsonAsync<SearchEventDto>(U($"/api/search/{id}"), Ct);
        var page = await client.GetFromJsonAsync<SearchEventDto>(U($"/api/search/{id}?from=2"), Ct);

        status!.Hits.ShouldBeEmpty();
        status.Progress.Matches.ShouldBe(4);
        page!.Hits.Select(h => h.Index).ShouldBe([2, 3]);
    }

    [Theory]
    [InlineData("[unclosed", "regex", "正規表示式有誤")]
    [InlineData("x", "fuzzy", "不支援的比對方式")]
    public async Task Invalid_patterns_return_400(string pattern, string mode, string message)
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync(U("/api/search"), new StartSearchRequest([_root], pattern, mode), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ErrorResponse>(Ct))!.Error.ShouldContain(message);
    }

    [Fact]
    public async Task Invalid_requests_return_400()
    {
        using var client = Client();

        using var noRoots = await client.PostAsJsonAsync(U("/api/search"), new StartSearchRequest([]), Ct);
        using var missing = await client.PostAsJsonAsync(U("/api/search"), new StartSearchRequest([Path.Combine(_root, "nope")]), Ct);
        using var badSize = await client.PostAsJsonAsync(U("/api/search"), new StartSearchRequest([_root], MinSize: 10, MaxSize: 5), Ct);
        using var badDepth = await client.PostAsJsonAsync(U("/api/search"), new StartSearchRequest([_root], MaxDepth: 0), Ct);

        noRoots.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missing.Content.ReadFromJsonAsync<ErrorResponse>(Ct))!.Error.ShouldContain("找不到資料夾");
        badSize.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badDepth.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Export_produces_csv_with_bom_and_formula_protection()
    {
        using var client = Client();
        var (id, _, _) = await SearchAsync(client, new StartSearchRequest([_root], "*.csv;*.pdf"));

        using var response = await client.GetAsync(U($"/api/search/{id}/export"), Ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        var text = Encoding.UTF8.GetString(bytes);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        response.Content.Headers.ContentDisposition!.FileNameStar.ShouldStartWith("WindowsTool-search-");
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        text.ShouldContain("名稱,所在資料夾,完整路徑,類型");
        text.ShouldContain("'=cmd.csv,");                 // 公式注入防護
        text.ShouldContain("report-2026.pdf");
        text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(3); // 標題 + 2 筆
    }

    [Fact]
    public async Task Cancel_keeps_results_available()
    {
        using var client = Client();
        var id = await StartAsync(client, new StartSearchRequest([_root], "report"));

        using var cancel = await client.PostAsync(U($"/api/search/{id}/cancel"), null, Ct);
        var events = await ReadEventsAsync(client, id);
        var done = events.Last().Data;

        cancel.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        done.Status.ShouldBeOneOf("cancelled", "completed"); // 搜尋很快，可能在取消前就已經結束
        using var stillThere = await client.GetAsync(U($"/api/search/{id}"), Ct);
        stillThere.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_removes_the_search()
    {
        using var client = Client();
        var (id, _, _) = await SearchAsync(client, new StartSearchRequest([_root], "report"));

        using var deleted = await client.DeleteAsync(U($"/api/search/{id}"), Ct);
        using var gone = await client.GetAsync(U($"/api/search/{id}"), Ct);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_requires_the_token()
    {
        using var client = Client(authorized: false);

        using var response = await client.PostAsJsonAsync(U("/api/search"), new StartSearchRequest([_root]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- 在檔案總管中顯示 / 開啟 --------------------------------------------

    [Fact]
    public async Task Reveal_and_open_work_for_search_hits()
    {
        using var client = Client();
        var (id, hits, _) = await SearchAsync(client, new StartSearchRequest([_root], "report-2026"));
        var hit = hits.Single();

        using var reveal = await client.PostAsJsonAsync(U("/api/shell/reveal"), new ShellTargetRequest(id, hit.Index), Ct);
        using var open = await client.PostAsJsonAsync(U("/api/shell/open"), new ShellTargetRequest(id, hit.Index), Ct);

        reveal.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        open.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _shell.Calls.ShouldBe([("reveal", hit.Path), ("open", hit.Path)]);
    }

    [Fact]
    public async Task Executables_cannot_be_opened_but_can_be_revealed()
    {
        using var client = Client();
        var (id, hits, _) = await SearchAsync(client, new StartSearchRequest([_root], "*.exe"));
        var exe = hits.Single();

        using var open = await client.PostAsJsonAsync(U("/api/shell/open"), new ShellTargetRequest(id, exe.Index), Ct);
        using var reveal = await client.PostAsJsonAsync(U("/api/shell/reveal"), new ShellTargetRequest(id, exe.Index), Ct);

        open.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await open.Content.ReadFromJsonAsync<ErrorResponse>(Ct))!.Error.ShouldContain("基於安全考量");
        reveal.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _shell.Calls.ShouldBe([("reveal", exe.Path)]);
    }

    [Fact]
    public async Task Shell_rejects_unknown_or_vanished_targets()
    {
        using var client = Client();
        var (id, hits, _) = await SearchAsync(client, new StartSearchRequest([_root], "notes.txt"));
        File.Delete(hits.Single().Path);

        using var noTarget = await client.PostAsJsonAsync(U("/api/shell/reveal"), new ShellTargetRequest(), Ct);
        using var unknownSearch = await client.PostAsJsonAsync(U("/api/shell/reveal"), new ShellTargetRequest("nope", 0), Ct);
        using var badIndex = await client.PostAsJsonAsync(U("/api/shell/reveal"), new ShellTargetRequest(id, 99), Ct);
        using var vanished = await client.PostAsJsonAsync(U("/api/shell/reveal"), new ShellTargetRequest(id, 0), Ct);

        noTarget.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknownSearch.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        badIndex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        vanished.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await vanished.Content.ReadFromJsonAsync<ErrorResponse>(Ct))!.Error.ShouldContain("已經不存在");
        _shell.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reveal_works_for_scan_items()
    {
        using var client = Client();
        using var start = await client.PostAsJsonAsync(U("/api/scans"), new StartScanRequest(_root), Ct);
        var scanId = (await start.Content.ReadFromJsonAsync<StartScanResponse>(Ct))!.ScanId;
        ScanStatusDto? status;
        do
        {
            await Task.Delay(30, Ct);
            status = await client.GetFromJsonAsync<ScanStatusDto>(U($"/api/scans/{scanId}"), Ct);
        }
        while (status!.Status == "running");

        using var reveal = await client.PostAsJsonAsync(
            U("/api/shell/reveal"), new ShellTargetRequest(ScanId: scanId, ItemId: status.Summary!.RootId), Ct);

        reveal.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _shell.Calls.ShouldBe([("reveal", _root)]);
    }
}
