using System.Diagnostics;

namespace WindowsTool.App.Hosting;

public static partial class BrowserLauncher
{
    /// <summary>用預設瀏覽器開啟網址；失敗只記錄警告，使用者仍可手動複製主控台上的網址。</summary>
    public static void TryOpen(string url, ILogger logger)
    {
        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(url) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url);

            using var _ = Process.Start(startInfo);
        }
#pragma warning disable CA1031 // 開瀏覽器失敗不應該讓程式結束
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogOpenFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "無法自動開啟瀏覽器，請手動開啟主控台上顯示的網址。")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception);
}
