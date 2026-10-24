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

    /// <summary>每個 factory 一個暫存的資料夾，測試不會寫到使用者真正的設定資料夾。</summary>
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "WindowsTool.Tests", "data-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("WindowsTool:Port", TestPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("WindowsTool:OpenBrowser", "false");
        builder.UseSetting("WindowsTool:DataDirectory", DataDirectory);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 暫存資料夾刪不掉不影響結果
        }
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
