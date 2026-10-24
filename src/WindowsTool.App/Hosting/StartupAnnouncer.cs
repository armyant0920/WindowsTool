using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using WindowsTool.App.Security;

namespace WindowsTool.App.Hosting;

public static class StartupAnnouncer
{
    /// <summary>
    /// 伺服器啟動後：記下實際埠號、在主控台印出網址，並視設定開啟瀏覽器。
    /// token 放在 URL fragment（# 之後），瀏覽器不會把它送給伺服器或寫進 Referer。
    /// </summary>
    public static void OnStarted(WebApplication app, AppOptions options)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault();
        if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return; // 例如整合測試用的 TestServer：沒有實際監聽位址
        }

        var state = app.Services.GetRequiredService<LocalServerState>();
        state.SetPort(uri.Port);

        var url = $"http://127.0.0.1:{uri.Port}/#token={Uri.EscapeDataString(state.Token)}";
        Console.WriteLine();
        Console.WriteLine($"  WindowsTool 已啟動：{url}");
        Console.WriteLine("  關閉此視窗即可結束程式。");
        Console.WriteLine();

        if (options.OpenBrowser)
        {
            BrowserLauncher.TryOpen(url, app.Logger);
        }
    }
}
