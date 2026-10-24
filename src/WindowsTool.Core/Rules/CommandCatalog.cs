namespace WindowsTool.Core.Rules;

/// <summary>
/// 白名單清理命令（PLAN 10.3）。規則的 <c>method</c> 只能引用這裡的 id；
/// 命令內容由程式內建，規則（包含 AI 提案）不能夾帶任意命令字串。
/// 參數已用各工具的 <c>--help</c> 確認（2026-10-25）：npm 5 起 <c>cache clean</c> 必須加 <c>--force</c>；
/// <c>docker system prune</c> 不加 <c>-f</c> 會停下來等待確認，因此加上 <c>-f</c>（不加 <c>-a</c>）。
/// </summary>
public static class CommandCatalog
{
    /// <param name="Id">規則引用的 id。</param>
    /// <param name="Display">顯示給使用者確認的完整命令。</param>
    /// <param name="Platform"><c>all</c> 或 <c>windows</c>。</param>
    /// <param name="Description">說明。</param>
    /// <param name="Executable">要執行的程式（在 PATH 中尋找）；null 代表只提供操作指引。</param>
    /// <param name="Arguments">固定的參數。</param>
    /// <param name="RequiresElevation">需要系統管理員權限。</param>
    public sealed record CommandInfo(
        string Id,
        string Display,
        string Platform,
        string Description,
        string? Executable,
        IReadOnlyList<string> Arguments,
        bool RequiresElevation = false)
    {
        public bool GuidanceOnly => Executable is null;

        public bool AppliesTo(string platform) => Platform == "all" || Platform == platform;
    }

    public static IReadOnlyList<CommandInfo> All { get; } =
    [
        new("nuget-clear", "dotnet nuget locals all --clear", "all", "清除 NuGet 本機快取", "dotnet", ["nuget", "locals", "all", "--clear"]),
        new("npm-cache-clean", "npm cache clean --force", "all", "清除 npm 快取", "npm", ["cache", "clean", "--force"]),
        new("pip-cache-purge", "pip cache purge", "all", "清除 pip 快取", "pip", ["cache", "purge"]),
        new("docker-prune", "docker system prune -f", "all", "清除停止的容器、未使用的網路與 dangling 映像（不含 -a，不會刪除有標籤的映像）", "docker", ["system", "prune", "-f"]),
        new("dism-component-cleanup", "dism /online /cleanup-image /StartComponentCleanup", "windows", "清理 WinSxS 元件存放區（需要系統管理員權限）", "dism", ["/online", "/cleanup-image", "/StartComponentCleanup"], RequiresElevation: true),
        new("wsl-compact", "（操作指引，不自動執行）", "windows",
            "壓縮 WSL / Docker 的虛擬磁碟：先在 Docker Desktop 執行清理並結束 Docker，執行 wsl --shutdown，再用 diskpart 的 compact vdisk 壓縮 ext4.vhdx。", null, []),
    ];

    public static bool Contains(string? id) => id is not null && All.Any(c => c.Id == id);

    public static CommandInfo? Find(string? id) => All.FirstOrDefault(c => c.Id == id);
}
