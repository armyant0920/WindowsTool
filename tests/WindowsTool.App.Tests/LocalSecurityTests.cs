using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using WindowsTool.App.Endpoints;

namespace WindowsTool.App.Tests;

public sealed class LocalSecurityTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Api_without_token_is_rejected()
    {
        using var client = factory.CreateLocalClient();

        using var response = await client.GetAsync(new Uri("/api/system/info", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("dev")]
    [InlineData("")]
    public async Task Api_with_wrong_token_is_rejected(string token)
    {
        using var client = factory.CreateLocalClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync(new Uri("/api/system/info", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_with_token_in_query_string_is_rejected()
    {
        using var client = factory.CreateLocalClient();

        using var response = await client.GetAsync(
            new Uri($"/api/system/info?token={factory.Token}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task Api_with_valid_token_and_host_succeeds(string host)
    {
        using var client = factory.CreateLocalClient(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token);

        using var response = await client.GetAsync(new Uri("/api/system/info", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        var body = await response.Content.ReadFromJsonAsync<SystemInfoResponse>(Ct);
        body.ShouldNotBeNull();
        body.Name.ShouldBe("WindowsTool");
        body.System.Platform.ShouldBeOneOf("windows", "linux", "macos", "other");
    }

    [Theory]
    [InlineData("evil.example:5179")]      // DNS rebinding：攻擊者網域解析到 127.0.0.1
    [InlineData("127.0.0.1:9999")]         // 埠號不符
    [InlineData("127.0.0.1")]              // 沒有埠號
    [InlineData("192.168.1.10:5179")]      // 非 loopback 位址
    [InlineData("localhost.evil.example:5179")]
    public async Task Request_with_invalid_host_is_rejected(string hostHeader)
    {
        using var client = factory.CreateLocalClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/system/info", UriKind.Relative));
        request.Headers.Host = hostHeader;

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Static_page_is_served_from_embedded_resources_with_security_headers()
    {
        using var client = factory.CreateLocalClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("<title>WindowsTool</title>");

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.GetValues("Referrer-Policy").ShouldContain("no-referrer");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'self'");
    }

    [Fact]
    public async Task Cors_is_not_enabled()
    {
        using var client = factory.CreateLocalClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/system/info", UriKind.Relative));
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        using var response = await client.SendAsync(request, Ct);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public void Production_token_is_random_and_long()
    {
        factory.Token.ShouldNotBe("dev");
        factory.Token.Length.ShouldBeGreaterThanOrEqualTo(43); // 32 bytes → base64url 43 字元
    }
}
