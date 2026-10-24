using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using WindowsTool.App.Scans;

namespace WindowsTool.App.Tests;

/// <summary>
/// 執行計畫的 API：從掃描結果建立計畫（dry-run）、執行、操作紀錄、隔離區。
/// 所有操作都在暫存的 fixture 上；資源回收筒與清理命令都是假的（見 <see cref="AppFactory"/>）。
/// </summary>
public sealed class PlanApiTests : IDisposable
{
    private readonly AppFactory _factory = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsTool.Tests", Guid.NewGuid().ToString("N"));

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
        }
    }

    private HttpClient Client()
    {
        var client = _factory.CreateLocalClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token);
        return client;
    }

    private static Uri U(string relative) => new(relative, UriKind.Relative);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private string P(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void File(string relative, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(relative))!);
        using var stream = System.IO.File.Create(P(relative));
        stream.SetLength(size);
    }

    /// <summary>
    /// root/
    ///   junk/       （自訂規則：可回收、預設勾選）
    ///   vault/      （自訂保護規則）
    ///   other.bin   （未分類）
    /// </summary>
    private async Task<(HttpClient Client, string ScanId, Dictionary<string, int> Ids)> SetupAsync()
    {
        File("junk/a.bin", 2 * 1024 * 1024);
        File("junk/b.bin", 2 * 1024 * 1024);
        File("vault/secret.bin", 2 * 1024 * 1024);
        File("other.bin", 2 * 1024 * 1024);

        var client = Client();
        var root = _root.Replace('\\', '/');
        foreach (var rule in new[]
        {
            $$"""{ "rule": { "id": "user-junk", "name": "垃圾", "match": { "kind": "dir", "pathGlob": ["{{root}}/junk"] }, "result": { "category": "垃圾", "risk": "low", "suggestedAction": "recycle", "autoSelect": true } } }""",
            $$"""{ "rule": { "id": "user-vault", "name": "金庫", "kind": "protect", "match": { "pathGlob": ["{{root}}/vault"] } } }""",
        })
        {
            using var created = await client.PostAsync(U("/api/rules"), Json(rule), Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        }

        using var start = await client.PostAsJsonAsync(U("/api/scans"), new StartScanRequest(_root), Ct);
        var scanId = (await start.Content.ReadFromJsonAsync<StartScanResponse>(Ct))!.ScanId;
        while ((await client.GetFromJsonAsync<ScanStatusDto>(U($"/api/scans/{scanId}"), Ct))!.Status == "running")
        {
            await Task.Delay(50, Ct);
        }

        var page = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&limit=100"), Ct);
        var ids = page!["items"]!.AsArray().ToDictionary(i => i!["name"]!.GetValue<string>(), i => i!["id"]!.GetValue<int>());
        return (client, scanId, ids);
    }

    private static async Task<JsonNode> CreatePlanAsync(HttpClient client, string body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using var response = await client.PostAsync(U("/api/plans"), Json(body), Ct);
        var node = await ReadAsync(response);
        response.StatusCode.ShouldBe(expected, node.ToJsonString());
        return node;
    }

    private static async Task<JsonNode> RunAsync(HttpClient client, string planId, string? confirmation = null)
    {
        using var execute = await client.PostAsync(U($"/api/plans/{planId}/execute"),
            Json(confirmation is null ? "{}" : $$"""{ "confirmation": "{{confirmation}}" }"""), Ct);
        execute.StatusCode.ShouldBe(HttpStatusCode.Accepted, await execute.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var status = await client.GetFromJsonAsync<JsonNode>(U($"/api/plans/{planId}"), Ct);
            if (status!["execution"]!["status"]!.GetValue<string>() != "running")
            {
                return status;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Auto_selected_items_are_planned_and_protected_items_are_blocked()
    {
        var (client, scanId, ids) = await SetupAsync();
        using var _ = client;

        // 預設勾選 junk；手動加入 vault（受保護）和 other.bin
        var created = await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "include": [{{ids["vault"]}}, {{ids["other.bin"]}}] }""");
        var plan = created["plan"]!;
        var items = plan["items"]!.AsArray().Select(i => i!).ToList();
        JsonNode Item(string name) => items.Single(i => i["path"]!.GetValue<string>().EndsWith(name, StringComparison.Ordinal));

        Item("junk")["state"]!.GetValue<string>().ShouldBe("ready");
        Item("junk")["action"]!.GetValue<string>().ShouldBe("recycle");
        Item("junk")["ruleId"]!.GetValue<string>().ShouldBe("user-junk");
        Item("vault")["state"]!.GetValue<string>().ShouldBe("blocked");
        Item("vault")["reason"]!.GetValue<string>().ShouldContain("受保護");
        Item("other.bin")["action"]!.GetValue<string>().ShouldBe("recycle"); // 未分類 → 可以還原的資源回收筒
        plan["summary"]!["blocked"]!.GetValue<int>().ShouldBe(1);
        plan["summary"]!["bytes"]!.GetValue<long>().ShouldBe(6 * 1024 * 1024);

        // 建立計畫不會動到任何檔案
        Directory.Exists(P("junk")).ShouldBeTrue();
        _factory.Trash.Recycled.ShouldBeEmpty();

        var done = await RunAsync(client, plan["planId"]!.GetValue<string>());
        var execution = done["execution"]!;
        execution["status"]!.GetValue<string>().ShouldBe("completed");
        execution["totals"]!["ok"]!.GetValue<int>().ShouldBe(2);
        execution["totals"]!["skipped"]!.GetValue<int>().ShouldBe(1);
        Directory.Exists(P("junk")).ShouldBeFalse();
        Directory.Exists(P("vault")).ShouldBeTrue();
        _factory.Trash.Recycled.Count.ShouldBe(2);

        // 每一項都有紀錄
        var logs = await client.GetFromJsonAsync<JsonNode>(U($"/api/logs?planId={plan["planId"]}"), Ct);
        logs!["entries"]!.AsArray().Count.ShouldBe(3);
        logs["entries"]!.AsArray().ShouldContain(e => e!["result"]!.GetValue<string>() == "skipped" && e["error"]!.GetValue<string>().Contains("受保護"));

        // 同一個計畫不能執行兩次
        using var again = await client.PostAsync(U($"/api/plans/{plan["planId"]}/execute"), Json("{}"), Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Selection_is_kept_on_the_server_and_drives_the_plan()
    {
        var (client, scanId, ids) = await SetupAsync();
        using var _ = client;

        async Task<JsonNode> Selection(string? body = null)
        {
            if (body is null)
            {
                return (await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/selection"), Ct))!;
            }

            using var response = await client.PostAsync(U($"/api/scans/{scanId}/selection"), Json(body), Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
            return await ReadAsync(response);
        }

        // 掃描完成時不會自動勾選；建議清理的項目另外統計
        var initial = await Selection();
        initial["count"]!.GetValue<int>().ShouldBe(0);
        initial["suggestedCount"]!.GetValue<int>().ShouldBe(1);
        initial["suggestedSize"]!.GetValue<long>().ShouldBe(4 * 1024 * 1024);

        // 受保護的項目勾不進去
        (await Selection($$"""{ "action": "add", "ids": [{{ids["vault"]}}, {{ids["secret.bin"]}}] }"""))["count"]!.GetValue<int>().ShouldBe(0);

        var suggested = await Selection("""{ "action": "suggested" }""");
        suggested["count"]!.GetValue<int>().ShouldBe(1);
        suggested["suggestedRemaining"]!.GetValue<int>().ShouldBe(0);
        initial["suggestedRemaining"]!.GetValue<int>().ShouldBe(1);

        // 依條件全選：所有可以勾選的項目；大小在上下層都勾選時只算一次
        var all = await Selection("""{ "action": "add", "filter": { "flat": true } }""");
        all["count"]!.GetValue<int>().ShouldBe(2); // junk（含 a.bin、b.bin）、other.bin
        all["size"]!.GetValue<long>().ShouldBe(6 * 1024 * 1024);

        var selectedPage = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&selected=true"), Ct);
        selectedPage!["total"]!.GetValue<int>().ShouldBe(2); // 只列最上層：junk、other.bin
        selectedPage["items"]!.AsArray().ShouldAllBe(i => i!["selected"]!.GetValue<bool>());

        var everything = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true"), Ct);
        everything!["selectableInView"]!.GetValue<int>().ShouldBe(4);
        everything["selectedInView"]!.GetValue<int>().ShouldBe(4);

        (await Selection($$"""{ "action": "remove", "ids": [{{ids["other.bin"]}}] }"""))["count"]!.GetValue<int>().ShouldBe(1);

        // 用勾選建立計畫並執行：上下層只處理上層；處理過的項目從勾選中移除並標示
        var created = await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "useSelection": true, "action": "quarantine" }""");
        created["plan"]!["summary"]!["ready"]!.GetValue<int>().ShouldBe(1);
        created["plan"]!["summary"]!["skipped"]!.GetValue<int>().ShouldBe(2);
        await RunAsync(client, created["plan"]!["planId"]!.GetValue<string>());

        (await Selection())["count"]!.GetValue<int>().ShouldBe(0);
        var after = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true"), Ct);
        var junk = after!["items"]!.AsArray().Single(i => i!["name"]!.GetValue<string>() == "junk")!;
        junk["done"]!.GetValue<bool>().ShouldBeTrue();
        junk["selectable"]!.GetValue<bool>().ShouldBeFalse();
        after["items"]!.AsArray().Single(i => i!["name"]!.GetValue<string>() == "a.bin")!["done"]!.GetValue<bool>().ShouldBeTrue(); // 上層處理過

        (await Selection("""{ "action": "clear" }"""))["count"]!.GetValue<int>().ShouldBe(0);
        using var bad = await client.PostAsync(U($"/api/scans/{scanId}/selection"), Json("""{ "action": "explode" }"""), Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Top_level_view_matches_the_category_totals()
    {
        File("deep/x/y.bin", 2 * 1024 * 1024);
        File("deep/z.bin", 2 * 1024 * 1024);
        var client = Client();
        using var _ = client;
        var root = _root.Replace('\\', '/');
        using (var rule = await client.PostAsync(U("/api/rules"), Json($$"""
            { "rule": { "id": "user-deep", "name": "深", "match": { "pathGlob": ["{{root}}/deep", "{{root}}/deep/**"] }, "result": { "category": "深層", "risk": "low" } } }
            """), Ct))
        {
            rule.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var start = await client.PostAsJsonAsync(U("/api/scans"), new StartScanRequest(_root), Ct);
        var scanId = (await start.Content.ReadFromJsonAsync<StartScanResponse>(Ct))!.ScanId;
        while ((await client.GetFromJsonAsync<ScanStatusDto>(U($"/api/scans/{scanId}"), Ct))!.Status == "running")
        {
            await Task.Delay(50, Ct);
        }

        var category = Uri.EscapeDataString("深層");
        var all = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&category={category}"), Ct);
        all!["total"]!.GetValue<int>().ShouldBe(4); // deep、x、y.bin、z.bin

        var top = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&category={category}&topLevel=true"), Ct);
        top!["total"]!.GetValue<int>().ShouldBe(1);
        var categories = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/categories"), Ct);
        var summary = categories!["categories"]!.AsArray().Single(c => c!["category"]!.GetValue<string>() == "深層")!;
        top["sizeInView"]!.GetValue<long>().ShouldBe(summary["size"]!.GetValue<long>());
        all["sizeInView"]!.GetValue<long>().ShouldBe(summary["size"]!.GetValue<long>()); // 去除巢狀後也一樣
    }

    [Fact]
    public async Task Quarantine_and_restore_through_the_api()
    {
        var (client, scanId, ids) = await SetupAsync();
        using var _ = client;

        var created = await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "useAutoSelect": false, "include": [{{ids["other.bin"]}}], "action": "quarantine" }""");
        var done = await RunAsync(client, created["plan"]!["planId"]!.GetValue<string>());
        done["execution"]!["totals"]!["ok"]!.GetValue<int>().ShouldBe(1);
        System.IO.File.Exists(P("other.bin")).ShouldBeFalse();

        var list = await client.GetFromJsonAsync<JsonNode>(U("/api/quarantine"), Ct);
        var entry = list!["entries"]!.AsArray().Single()!;
        entry["originalPath"]!.GetValue<string>().ShouldBe(P("other.bin"));
        list["totalBytes"]!.GetValue<long>().ShouldBe(2 * 1024 * 1024);
        list["root"]!.GetValue<string>().ShouldStartWith(_factory.DataDirectory);

        using var restored = await client.PostAsync(U($"/api/quarantine/{entry["entryId"]}/restore"), null, Ct);
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        System.IO.File.Exists(P("other.bin")).ShouldBeTrue();
        (await client.GetFromJsonAsync<JsonNode>(U("/api/quarantine"), Ct))!["entries"]!.AsArray().ShouldBeEmpty();
        (await client.GetFromJsonAsync<JsonNode>(U("/api/quarantine?all=true"), Ct))!["entries"]![0]!["status"]!.GetValue<string>().ShouldBe("restored");

        var logs = await client.GetFromJsonAsync<JsonNode>(U("/api/logs"), Ct);
        logs!["entries"]![0]!["action"]!.GetValue<string>().ShouldBe("restore");

        using var missing = await client.PostAsync(U("/api/quarantine/nope/restore"), null, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Permanent_delete_and_purge_need_the_confirmation_text()
    {
        var (client, scanId, ids) = await SetupAsync();
        using var _ = client;

        var created = await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "useAutoSelect": false, "include": [{{ids["other.bin"]}}], "action": "delete" }""");
        var planId = created["plan"]!["planId"]!.GetValue<string>();
        created["plan"]!["summary"]!["requiresConfirmation"]!.GetValue<bool>().ShouldBeTrue();

        using (var unconfirmed = await client.PostAsync(U($"/api/plans/{planId}/execute"), Json("{}"), Ct))
        {
            unconfirmed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadAsync(unconfirmed))["error"]!.GetValue<string>().ShouldContain("永久刪除");
        }

        System.IO.File.Exists(P("other.bin")).ShouldBeTrue();
        await RunAsync(client, planId, "永久刪除");
        System.IO.File.Exists(P("other.bin")).ShouldBeFalse();

        using var purge = await client.PostAsync(U("/api/quarantine/purge"), Json("""{ "olderThanDays": 0 }"""), Ct);
        purge.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var purgeOk = await client.PostAsync(U("/api/quarantine/purge"), Json("""{ "olderThanDays": 0, "confirmation": "永久刪除" }"""), Ct);
        purgeOk.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Commands_from_rules_run_through_the_whitelist_only()
    {
        File("pkgcache/x.nupkg", 2 * 1024 * 1024);
        var client = Client();
        using var _ = client;
        var root = _root.Replace('\\', '/');
        using (var rule = await client.PostAsync(U("/api/rules"), Json($$"""
            { "rule": { "id": "user-pkg", "name": "套件", "match": { "kind": "dir", "pathGlob": ["{{root}}/pkgcache"] },
              "result": { "category": "套件快取", "risk": "low", "suggestedAction": "command", "method": "nuget-clear" } } }
            """), Ct))
        {
            rule.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var start = await client.PostAsJsonAsync(U("/api/scans"), new StartScanRequest(_root), Ct);
        var scanId = (await start.Content.ReadFromJsonAsync<StartScanResponse>(Ct))!.ScanId;
        while ((await client.GetFromJsonAsync<ScanStatusDto>(U($"/api/scans/{scanId}"), Ct))!.Status == "running")
        {
            await Task.Delay(50, Ct);
        }

        var page = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&q=pkgcache"), Ct);
        var id = page!["items"]![0]!["id"]!.GetValue<int>();

        var created = await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "include": [{{id}}] }""");
        var item = created["plan"]!["items"]![0]!;
        item["action"]!.GetValue<string>().ShouldBe("command");
        item["commandId"]!.GetValue<string>().ShouldBe("nuget-clear");
        created["plan"]!["summary"]!["commands"]![0]!.GetValue<string>().ShouldBe("nuget-clear");

        await RunAsync(client, created["plan"]!["planId"]!.GetValue<string>());
        _factory.Commands.Ran.ShouldBe(["nuget-clear"]);
        Directory.Exists(P("pkgcache")).ShouldBeTrue(); // 命令自己處理，不會刪資料夾
    }

    [Fact]
    public async Task Bad_requests_are_rejected()
    {
        var (client, scanId, ids) = await SetupAsync();
        using var _ = client;

        await CreatePlanAsync(client, """{ "scanId": "nope" }""", HttpStatusCode.NotFound);
        await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "useAutoSelect": false }""", HttpStatusCode.BadRequest);
        await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "action": "shred" }""", HttpStatusCode.BadRequest);
        await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "action": "move" }""", HttpStatusCode.BadRequest);
        await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "include": [999999] }""", HttpStatusCode.BadRequest);

        // 排除後就沒有東西
        await CreatePlanAsync(client, $$"""{ "scanId": "{{scanId}}", "exclude": [{{ids["junk"]}}] }""", HttpStatusCode.BadRequest);

        using var missingPlan = await client.GetAsync(U("/api/plans/nope"), Ct);
        missingPlan.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var missingRun = await client.PostAsync(U("/api/plans/nope/execute"), Json("{}"), Ct);
        missingRun.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var missingCancel = await client.PostAsync(U("/api/plans/nope/cancel"), null, Ct);
        missingCancel.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Move_to_a_destination_folder()
    {
        var (client, scanId, ids) = await SetupAsync();
        using var _ = client;
        var dest = Directory.CreateDirectory(Path.Combine(_root, "..", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var created = await CreatePlanAsync(client, $$"""
                { "scanId": "{{scanId}}", "useAutoSelect": false, "include": [{{ids["other.bin"]}}], "action": "move", "destination": {{System.Text.Json.JsonSerializer.Serialize(dest)}} }
                """);
            await RunAsync(client, created["plan"]!["planId"]!.GetValue<string>());
            System.IO.File.Exists(Path.Combine(dest, "other.bin")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }
}
