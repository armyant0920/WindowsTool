using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WindowsTool.App.Security;

namespace WindowsTool.App.Tests;

/// <summary>
/// 以正式環境（Production）設定啟動 App：隨機 token、讀內嵌的前端檔案、不開瀏覽器。
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const int TestPort = 5179;

    public string Token => Services.GetRequiredService<LocalServerState>().Token;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("WindowsTool:Port", TestPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("WindowsTool:OpenBrowser", "false");
    }

    /// <summary>建立 Host header 為 127.0.0.1:5179 的 client。</summary>
    public HttpClient CreateLocalClient(string host = "127.0.0.1")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{host}:{TestPort}"),
            AllowAutoRedirect = false,
        });
        return client;
    }
}
