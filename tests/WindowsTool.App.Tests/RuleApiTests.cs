using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using WindowsTool.App.Scans;

namespace WindowsTool.App.Tests;

/// <summary>規則 API 與掃描結果的規則標註。每個測試一個新的 App（各自的資料夾），互不影響。</summary>
public sealed class RuleApiTests : IDisposable
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
            // 暫存資料夾刪不掉不影響結果
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

    private static string RuleJson(string id, string glob, string extra = "", string result = """{ "category": "測試", "risk": "low", "suggestedAction": "recycle" }""") =>
        $$"""{ "id": "{{id}}", "name": "{{id}}", "match": { "pathGlob": [{{System.Text.Json.JsonSerializer.Serialize(glob)}}] {{extra}} }, "result": {{result}} }""";

    private void File(string relative, long size)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = System.IO.File.Create(path);
        stream.SetLength(size);
    }

    private async Task<string> ScanAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(U("/api/scans"), new StartScanRequest(_root), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var scanId = (await response.Content.ReadFromJsonAsync<StartScanResponse>(Ct))!.ScanId;

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var status = await client.GetFromJsonAsync<ScanStatusDto>(U($"/api/scans/{scanId}"), Ct);
            if (status!.Status != "running")
            {
                status.Status.ShouldBe("completed");
                return scanId;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline);
            await Task.Delay(50, Ct);
        }
    }

    [Fact]
    public async Task Lists_builtin_rules_with_their_state()
    {
        using var client = Client();
        var body = await client.GetFromJsonAsync<JsonNode>(U("/api/rules"), Ct);

        body!["filePath"]!.GetValue<string>().ShouldStartWith(_factory.DataDirectory);
        body["platform"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        body["commands"]!.AsArray().Count.ShouldBeGreaterThan(3);
        body["tokens"]!["UserProfile"].ShouldNotBeNull();

        var rules = body["rules"]!.AsArray();
        var protect = rules.First(r => r!["rule"]!["kind"]!.GetValue<string>() == "protect")!;
        protect["canDisable"]!.GetValue<bool>().ShouldBeFalse();
        protect["editable"]!.GetValue<bool>().ShouldBeFalse();
        protect["rule"]!["source"]!.GetValue<string>().ShouldBe("builtin");

        var builtinOnly = await client.GetFromJsonAsync<JsonNode>(U("/api/rules?source=user"), Ct);
        builtinOnly!["rules"]!.AsArray().Count.ShouldBe(0);

        using var bad = await client.GetAsync(U("/api/rules?source=nope"), Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_update_disable_and_delete_a_user_rule()
    {
        using var client = Client();

        // 沒給 id 時自動配發；source 一律是 user
        using var created = await client.PostAsync(U("/api/rules"),
            Json($$"""{ "rule": {{RuleJson("", "**/node_modules").Replace("\"name\": \"\"", "\"name\": \"測試\", \"source\": \"builtin\"")}} }"""), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var saved = await ReadAsync(created);
        var id = saved["rule"]!["rule"]!["id"]!.GetValue<string>();
        id.ShouldBe("user-0001");
        saved["rule"]!["rule"]!["source"]!.GetValue<string>().ShouldBe("user");
        saved["rule"]!["active"]!.GetValue<bool>().ShouldBeTrue();
        saved["rule"]!["rule"]!["meta"]!["createdUtc"].ShouldNotBeNull();
        System.IO.File.Exists(Path.Combine(_factory.DataDirectory, "rules.user.json")).ShouldBeTrue();

        using var updated = await client.PutAsync(U($"/api/rules/{id}"),
            Json($$"""{ "rule": {{RuleJson(id, "**/bower_components")}} }"""), Ct);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await ReadAsync(updated);
        after["rule"]!["rule"]!["match"]!["pathGlob"]![0]!.GetValue<string>().ShouldBe("**/bower_components");
        after["rule"]!["rule"]!["meta"]!["updatedUtc"].ShouldNotBeNull();

        using var disabled = await client.PutAsJsonAsync(U($"/api/rules/{id}/enabled"), new { enabled = false }, Ct);
        (await ReadAsync(disabled))["active"]!.GetValue<bool>().ShouldBeFalse();

        using var deleted = await client.DeleteAsync(U($"/api/rules/{id}"), Ct);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var again = await client.DeleteAsync(U($"/api/rules/{id}"), Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invalid_rules_are_rejected_with_reasons()
    {
        using var client = Client();

        using var broad = await client.PostAsync(U("/api/rules"), Json($$"""{ "rule": {{RuleJson("user-1", "C:/**")}} }"""), Ct);
        broad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await ReadAsync(broad);
        body["valid"]!.GetValue<bool>().ShouldBeFalse();
        body["issues"]!.AsArray().ShouldContain(i => i!["message"]!.GetValue<string>().Contains("太寬"));

        using var typo = await client.PostAsync(U("/api/rules"),
            Json("""{ "rule": { "id": "user-1", "name": "x", "match": { "pathGlobs": ["**/x"] } } }"""), Ct);
        typo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(typo))["error"]!.GetValue<string>().ShouldContain("pathGlobs");

        using var command = await client.PostAsync(U("/api/rules"), Json($$"""{ "rule": {{RuleJson("user-2", "**/cache", result: """{ "category": "c", "risk": "low", "suggestedAction": "command", "method": "format c:" }""")}} }"""), Ct);
        command.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var empty = await client.PostAsync(U("/api/rules"), Json("{}"), Ct);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.GetFromJsonAsync<JsonNode>(U("/api/rules?source=user"), Ct))!["rules"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Builtin_rules_are_read_only_and_protect_rules_cannot_be_disabled()
    {
        using var client = Client();

        using var protect = await client.PutAsJsonAsync(U("/api/rules/protect-windows/enabled"), new { enabled = false }, Ct);
        protect.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var classify = await client.PutAsJsonAsync(U("/api/rules/log-old/enabled"), new { enabled = false }, Ct);
        classify.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(classify))["active"]!.GetValue<bool>().ShouldBeFalse();

        using var edit = await client.PutAsync(U("/api/rules/log-old"), Json($$"""{ "rule": {{RuleJson("log-old", "**/*.log")}} }"""), Ct);
        edit.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var delete = await client.DeleteAsync(U("/api/rules/log-old"), Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var duplicate = await client.PostAsync(U("/api/rules"), Json($$"""{ "rule": {{RuleJson("log-old", "**/*.log2")}} }"""), Ct);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var missing = await client.PutAsJsonAsync(U("/api/rules/nope/enabled"), new { enabled = false }, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Scan_items_carry_classifications_and_protected_items_are_never_selectable()
    {
        File("web/package.json", 10);
        File("web/node_modules/lib/a.js", 2 * 1024 * 1024);
        File("vault/secret.bin", 2 * 1024 * 1024);
        File("vault/inner/more.bin", 2 * 1024 * 1024);
        File("other/x.bin", 2 * 1024 * 1024);

        using var client = Client();
        var vault = Path.Combine(_root, "vault").Replace('\\', '/');
        using (var protect = await client.PostAsync(U("/api/rules"), Json($$"""
            { "rule": { "id": "user-vault", "name": "金庫", "kind": "protect", "match": { "pathGlob": [{{System.Text.Json.JsonSerializer.Serialize(vault)}}] }, "result": { "category": "我的保護" } } }
            """), Ct))
        {
            protect.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var scanId = await ScanAsync(client);

        var page = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&limit=100"), Ct);
        var items = page!["items"]!.AsArray().Select(i => i!).ToList();
        JsonNode Item(string name) => items.Single(i => i["name"]!.GetValue<string>() == name);

        var nm = Item("node_modules");
        nm["classification"]!["ruleId"]!.GetValue<string>().ShouldBe("dev-node-modules");
        nm["classification"]!["risk"]!.GetValue<string>().ShouldBe("low");
        nm["selectable"]!.GetValue<bool>().ShouldBeTrue();

        foreach (var name in new[] { "vault", "secret.bin", "inner", "more.bin" })
        {
            Item(name)["classification"]!["risk"]!.GetValue<string>().ShouldBe("protected");
            Item(name)["selectable"]!.GetValue<bool>().ShouldBeFalse();
            Item(name)["autoSelected"]!.GetValue<bool>().ShouldBeFalse();
        }

        Item("other")["classification"].ShouldBeNull();
        Item("other")["selectable"]!.GetValue<bool>().ShouldBeTrue();
        page["parent"]!["containsProtected"]!.GetValue<bool>().ShouldBeTrue(); // 根目錄
        page["parent"]!["selectable"]!.GetValue<bool>().ShouldBeFalse();

        // 依風險、類別、來源篩選
        var protectedOnly = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&risk=protected"), Ct);
        protectedOnly!["total"]!.GetValue<int>().ShouldBe(4);
        var byCategory = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&category={Uri.EscapeDataString("可重建的相依套件")}"), Ct);
        byCategory!["total"]!.GetValue<int>().ShouldBe(1);
        var unclassified = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&source=none&kind=file"), Ct);
        unclassified!["items"]!.AsArray().ShouldAllBe(i => i!["classification"] == null);
        using var badRisk = await client.GetAsync(U($"/api/scans/{scanId}/items?risk=extreme"), Ct);
        badRisk.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // 類別統計
        var categories = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/categories"), Ct);
        var mine = categories!["categories"]!.AsArray().Single(c => c!["category"]!.GetValue<string>() == "我的保護")!;
        mine["count"]!.GetValue<int>().ShouldBe(1);
        mine["size"]!.GetValue<long>().ShouldBe(4 * 1024 * 1024);

        // 詳情：命中的規則
        var detail = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items/{Item("more.bin")["id"]}"), Ct);
        var hit = detail!["rules"]!.AsArray().Single()!;
        hit["id"]!.GetValue<string>().ShouldBe("user-vault");
        hit["inherited"]!.GetValue<bool>().ShouldBeTrue();
        hit["role"]!.GetValue<string>().ShouldBe("effective");
    }

    [Fact]
    public async Task Changing_rules_re_evaluates_existing_scans()
    {
        File("a/big.dat", 2 * 1024 * 1024);
        using var client = Client();
        var scanId = await ScanAsync(client);

        async Task<JsonNode?> ClassificationOfBigDat()
        {
            var page = await client.GetFromJsonAsync<JsonNode>(U($"/api/scans/{scanId}/items?flat=true&q=big.dat"), Ct);
            return page!["items"]![0]!["classification"];
        }

        (await ClassificationOfBigDat()).ShouldBeNull();

        using (var created = await client.PostAsync(U("/api/rules"), Json($$"""{ "rule": {{RuleJson("user-dat", "**/*.dat")}} }"""), Ct))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await ClassificationOfBigDat())!["ruleId"]!.GetValue<string>().ShouldBe("user-dat");
    }

    [Fact]
    public async Task Validate_dry_runs_against_a_scan_and_blocks_protected_hits()
    {
        File("p1/package.json", 10);
        File("p1/node_modules/a.js", 2 * 1024 * 1024);
        File("p2/package.json", 10);
        File("p2/node_modules/a.js", 3 * 1024 * 1024);
        File("locked/node_modules/a.js", 1024 * 1024);

        using var client = Client();
        var locked = Path.Combine(_root, "locked").Replace('\\', '/');
        using (var protect = await client.PostAsync(U("/api/rules"), Json($$"""
            { "rule": { "id": "user-locked", "name": "鎖住", "kind": "protect", "match": { "pathGlob": [{{System.Text.Json.JsonSerializer.Serialize(locked)}}] } } }
            """), Ct))
        {
            protect.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var scanId = await ScanAsync(client);

        using var withSibling = await client.PostAsync(U("/api/rules/validate"), Json($$"""
            { "scanId": "{{scanId}}", "rule": {{RuleJson("user-nm", "**/node_modules", """, "kind": "dir", "siblingExists": ["package.json"]""")}} }
            """), Ct);
        var ok = await ReadAsync(withSibling);
        ok["valid"]!.GetValue<bool>().ShouldBeTrue();
        ok["dryRun"]!["count"]!.GetValue<int>().ShouldBe(2);
        ok["dryRun"]!["size"]!.GetValue<long>().ShouldBe(5 * 1024 * 1024);
        ok["dryRun"]!["samples"]![0]!["kind"]!.GetValue<string>().ShouldBe("dir");
        ok["scanRoot"]!.GetValue<string>().ShouldBe(_root);

        using var everywhere = await client.PostAsync(U("/api/rules/validate"), Json($$"""
            { "scanId": "{{scanId}}", "rule": {{RuleJson("user-nm", "**/node_modules")}} }
            """), Ct);
        var blocked = await ReadAsync(everywhere);
        blocked["valid"]!.GetValue<bool>().ShouldBeFalse();
        blocked["dryRun"]!["conflictCount"]!.GetValue<int>().ShouldBe(1);
        blocked["dryRun"]!["suggestedExcludes"]![0]!.GetValue<string>().ShouldBe(locked + "/**");

        using var unknownScan = await client.PostAsync(U("/api/rules/validate"), Json($$"""
            { "scanId": "nope", "rule": {{RuleJson("user-nm", "**/node_modules")}} }
            """), Ct);
        (await ReadAsync(unknownScan))["issues"]!.AsArray().ShouldContain(i => i!["field"]!.GetValue<string>() == "scanId");
    }

    [Fact]
    public async Task Export_and_import_round_trip()
    {
        using var client = Client();
        using (var a = await client.PostAsync(U("/api/rules"), Json($$"""{ "rule": {{RuleJson("user-a", "**/a-cache")}} }"""), Ct))
        {
            a.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var off = await client.PutAsJsonAsync(U("/api/rules/log-old/enabled"), new { enabled = false }, Ct))
        {
            off.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var export = await client.GetAsync(U("/api/rules/export"), Ct);
        export.Content.Headers.ContentDisposition!.FileName.ShouldBe("rules.user.json");
        var exported = await export.Content.ReadAsStringAsync(Ct);
        exported.ShouldContain("user-a");
        exported.ShouldContain("log-old");

        // 匯入到另一個全新的 App
        using var other = new AppFactory();
        using var otherClient = other.CreateLocalClient();
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Token);

        using var imported = await otherClient.PostAsync(U("/api/rules/import"), Json(exported), Ct);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await ReadAsync(imported);
        result["imported"]!.GetValue<int>().ShouldBe(1);
        result["disabledBuiltin"]!.GetValue<int>().ShouldBe(1);

        // 再匯入一次：id 重複，全部不寫入
        using var duplicate = await otherClient.PostAsync(U("/api/rules/import"), Json(exported), Ct);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(duplicate))["problems"]!.AsArray().Count.ShouldBe(1);

        using var broken = await otherClient.PostAsync(U("/api/rules/import"), Json("{ nope"), Ct);
        broken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var invalid = await otherClient.PostAsync(U("/api/rules/import"), Json($$"""{ "rules": [ {{RuleJson("user-b", "**")}} ] }"""), Ct);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var reload = await otherClient.PostAsync(U("/api/rules/reload"), null, Ct);
        reload.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(reload))["rules"]!.AsArray().ShouldContain(r => r!["rule"]!["id"]!.GetValue<string>() == "user-a");
    }

    [Fact]
    public async Task System_info_reports_the_data_directory()
    {
        using var client = Client();
        var info = await client.GetFromJsonAsync<JsonNode>(U("/api/system/info"), Ct);
        info!["dataDirectory"]!.GetValue<string>().ShouldBe(_factory.DataDirectory);
        info["portable"]!.GetValue<bool>().ShouldBeFalse();
    }
}
