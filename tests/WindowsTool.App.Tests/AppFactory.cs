using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WindowsTool.App.Security;
using WindowsTool.Core.Execution;
using WindowsTool.Core.Rules;

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

    /// <summary>假的資源回收筒：測試絕對不會把東西丟進使用者真正的資源回收筒。</summary>
    public TestTrash Trash { get; } = new();

    /// <summary>假的命令執行器：測試絕對不會執行真正的清理命令。</summary>
    public TestCommands Commands { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("WindowsTool:Port", TestPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("WindowsTool:OpenBrowser", "false");
        builder.UseSetting("WindowsTool:DataDirectory", DataDirectory);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITrash>(Trash);
            services.AddSingleton<ICommandRunner>(Commands);
        });
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

/// <summary>把「回收」的項目搬到暫存資料夾。</summary>
public sealed class TestTrash : ITrash
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "WindowsTool.Tests", "trash-" + Guid.NewGuid().ToString("N"));

    public System.Collections.Concurrent.ConcurrentQueue<string> Recycled { get; } = new();

    public bool IsSupported => true;

    public string? CannotRecycleReason(string path) => null;

    public TrashResult Recycle(string path)
    {
        Directory.CreateDirectory(_folder);
        var target = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }

        Recycled.Enqueue(path);
        return new TrashResult(true, target, null);
    }
}

public sealed class TestCommands : ICommandRunner
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Ran { get; } = new();

    public bool IsAvailable(CommandCatalog.CommandInfo command) => true;

    public Task<CommandResult> RunAsync(CommandCatalog.CommandInfo command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Ran.Enqueue(command.Id);
        return Task.FromResult(new CommandResult(0, "ok", false));
    }
}
