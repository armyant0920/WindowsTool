using System.Diagnostics;

namespace WindowsTool.App.Shell;

/// <summary>和作業系統的檔案管理員互動。測試時會換成假的實作，不會真的開視窗。</summary>
public interface IShellLauncher
{
    /// <summary>在檔案總管中顯示並選取項目。</summary>
    void Reveal(string path);

    /// <summary>用預設程式開啟檔案，或在檔案總管中開啟資料夾。</summary>
    void Open(string path);
}

public sealed class ShellLauncher : IShellLauncher
{
    /// <summary>
    /// 會執行程式碼的檔案類型：基於安全考量，不提供「開啟」，只能「在檔案總管中顯示」。
    /// </summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".scr", ".pif", ".lnk", ".url", ".hta", ".cpl", ".reg", ".jar", ".msc",
        ".appref-ms", ".application", ".gadget", ".sh", ".app", ".command",
    };

    public static bool IsExecutable(string path) => ExecutableExtensions.Contains(Path.GetExtension(path));

    public void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // explorer.exe 的參數格式特殊：/select,"完整路徑"
            Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path } });
        }
        else
        {
            Start(new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path) ?? path } });
        }
    }

    public void Open(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        else
        {
            Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { path } });
        }
    }

    private static void Start(ProcessStartInfo startInfo)
    {
        using var _ = Process.Start(startInfo);
    }
}
