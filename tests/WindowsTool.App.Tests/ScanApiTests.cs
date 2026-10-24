using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using WindowsTool.App.Scans;

namespace WindowsTool.App.Tests;

public sealed class ScanApiTests(AppFactory factory) : IClassFixture<AppFactory>, IDisposable
{
    private readonly string _root = CreateTree();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 暫存資料夾刪不掉不影響結果
        }
    }

    /// <summary>
    /// root/
    ///   big.iso        3 MB
    ///   docs/a.txt     100
    ///   docs/b.log     200
    ///   docs/deep/c.log 300
    /// </summary>
    private static string CreateTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsTool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs", "deep"));
        WriteFile(Path.Combine(root, "big.iso"), 3 * 1024 * 1024);
        WriteFile(Path.Combine(root, "docs", "a.txt"), 100);
        WriteFile(Path.Combine(root, "docs", "b.log"), 200);
        WriteFile(Path.Combine(root, "docs", "deep", "c.log"), 300);
        return root;

        static void WriteFile(string path, long size)
        {
            using var stream = File.Create(path);
            stream.SetLength(size);
        }
    }

    private HttpClient Client()
    {
        var client = factory.CreateLocalClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token);
        return client;
    }

    private static Uri U(string relative) => new(relative, UriKind.Relative);

    private static async Task<string> StartAsync(HttpClient client, string root)
    {
        using var response = await client.PostAsJsonAsync(U("/api/scans"), new StartScanRequest(root), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<StartScanResponse>(Ct);
        return body!.ScanId;
    }

    private static async Task<ScanStatusDto> WaitForCompletionAsync(HttpClient client, string scanId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var status = await client.GetFromJsonAsync<ScanStatusDto>(U($"/api/scans/{scanId}"), Ct);
            if (status!.Status != "running" || DateTime.UtcNow > deadline)
            {
                return status;
            }

            await Task.Delay(50, Ct);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Start_requires_a_root(string root)
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync(U("/api/scans"), new StartScanRequest(root), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_rejects_missing_folder()
    {
        using var client = Client();

        using var response = await client.PostAsJsonAsync(
            U("/api/scans"), new StartScanRequest(Path.Combine(_root, "nope")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ErrorResponse>(Ct))!.Error.ShouldContain("找不到資料夾");
    }

    [Fact]
    public async Task Full_flow_scan_then_browse_results()
    {
        using var client = Client();
        var scanId = await StartAsync(client, _root);

        var status = await WaitForCompletionAsync(client, scanId);
        status.Status.ShouldBe("completed");
        status.Root.ShouldBe(_root);
        status.FinishedUtc.ShouldNotBeNull();
        status.Summary.ShouldNotBeNull();
        status.Summary.TotalSize.ShouldBe((3 * 1024 * 1024) + 600);
        status.Summary.TotalFiles.ShouldBe(4);
        status.Summary.TotalDirs.ShouldBe(2);
        status.Summary.ErrorCount.ShouldBe(0);

        // 根目錄的子項目，依大小排序
        var page = await client.GetFromJsonAsync<ItemPageDto>(U($"/api/scans/{scanId}/items"), Ct);
        page!.Items.Select(i => i.Name).ShouldBe(["big.iso", "docs"]);
        page.Parent!.Id.ShouldBe(status.Summary.RootId);
        var docs = page.Items.Single(i => i.Name == "docs");
        docs.Kind.ShouldBe("dir");
        docs.Size.ShouldBe(600);
        docs.FileCount.ShouldBe(3);
        docs.HasChildren.ShouldBeTrue();
        docs.Path.ShouldBe(Path.Combine(_root, "docs"));

        // 整個結果中的 .log 檔
        var logs = await client.GetFromJsonAsync<ItemPageDto>(
            U($"/api/scans/{scanId}/items?flat=true&kind=file&ext=log&sort=name&order=asc"), Ct);
        logs!.Items.Select(i => i.Name).ShouldBe(["b.log", "c.log"]);

        // 項目詳情：祖先路徑與副檔名統計
        var deep = (await client.GetFromJsonAsync<ItemPageDto>(
            U($"/api/scans/{scanId}/items?parentId={docs.Id}&kind=dir"), Ct))!.Items.Single();
        var detail = await client.GetFromJsonAsync<ItemDetailDto>(U($"/api/scans/{scanId}/items/{deep.Id}"), Ct);
        detail!.Ancestors.Select(a => a.Name).ShouldBe([_root, "docs"]);
        var rootDetail = await client.GetFromJsonAsync<ItemDetailDto>(
            U($"/api/scans/{scanId}/items/{status.Summary.RootId}"), Ct);
        rootDetail!.ExtStats[0].Ext.ShouldBe(".iso");
        rootDetail.ExtStats.Single(e => e.Ext == ".log").Count.ShouldBe(2);

        // 刪除後就查不到
        using (var deleted = await client.DeleteAsync(U($"/api/scans/{scanId}"), Ct))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using var gone = await client.GetAsync(U($"/api/scans/{scanId}"), Ct);
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Events_stream_progress_and_ends_with_done()
    {
        using var client = Client();
        var scanId = await StartAsync(client, _root);

        using var request = new HttpRequestMessage(HttpMethod.Get, U($"/api/scans/{scanId}/events"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");

        var body = await response.Content.ReadAsStringAsync(Ct); // 掃描結束後串流會自動關閉
        body.ShouldContain("event: done");
        body.ShouldContain("\"status\":\"completed\"");
        body.TrimEnd().Split("\n\n").Last().ShouldContain("event: done");
    }

    [Fact]
    public async Task Unknown_scan_returns_404()
    {
        using var client = Client();

        using var status = await client.GetAsync(U("/api/scans/doesnotexist"), Ct);
        using var events = await client.GetAsync(U("/api/scans/doesnotexist/events"), Ct);
        using var items = await client.GetAsync(U("/api/scans/doesnotexist/items"), Ct);

        status.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        events.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        items.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("sort=banana")]
    [InlineData("kind=everything")]
    [InlineData("order=sideways")]
    public async Task Invalid_query_parameters_return_400(string query)
    {
        using var client = Client();
        var scanId = await StartAsync(client, _root);
        await WaitForCompletionAsync(client, scanId);

        using var response = await client.GetAsync(U($"/api/scans/{scanId}/items?{query}"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_item_returns_404()
    {
        using var client = Client();
        var scanId = await StartAsync(client, _root);
        await WaitForCompletionAsync(client, scanId);

        using var detail = await client.GetAsync(U($"/api/scans/{scanId}/items/99999"), Ct);
        using var children = await client.GetAsync(U($"/api/scans/{scanId}/items?parentId=99999"), Ct);

        detail.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        children.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Scan_endpoints_require_the_token()
    {
        using var client = factory.CreateLocalClient();

        using var response = await client.PostAsJsonAsync(U("/api/scans"), new StartScanRequest(_root), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
