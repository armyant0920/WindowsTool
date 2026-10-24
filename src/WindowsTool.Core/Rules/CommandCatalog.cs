namespace WindowsTool.Core.Rules;

/// <summary>
/// 白名單清理命令（PLAN 10.3）。規則的 <c>method</c> 只能引用這裡的 id；
/// 命令內容由程式內建，規則（包含 AI 提案）不能夾帶任意命令字串。實際執行在 Phase 3。
/// </summary>
public static class CommandCatalog
{
    public sealed record CommandInfo(string Id, string Display, string Platform, string Description);

    public static IReadOnlyList<CommandInfo> All { get; } =
    [
        new("nuget-clear", "dotnet nuget locals all --clear", "all", "清除 NuGet 本機快取"),
        new("npm-cache-clean", "npm cache clean --force", "all", "清除 npm 快取"),
        new("pip-cache-purge", "pip cache purge", "all", "清除 pip 快取"),
        new("docker-prune", "docker system prune", "all", "清除停止的容器、未使用的網路與 dangling 映像"),
        new("dism-component-cleanup", "dism /online /cleanup-image /StartComponentCleanup", "windows", "清理 WinSxS 元件存放區（需要系統管理員權限）"),
        new("wsl-compact", "（操作指引，不自動執行）", "windows", "壓縮 WSL / Docker 的虛擬磁碟"),
    ];

    public static bool Contains(string? id) => id is not null && All.Any(c => c.Id == id);
}
