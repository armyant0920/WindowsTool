using System.Runtime.InteropServices;

namespace WindowsTool.Core.Platform;

public sealed record DriveSummary(
    string Name,
    string DriveType,
    string? Format,
    string? Label,
    long TotalBytes,
    long FreeBytes);

public sealed record SystemInfo(
    string Platform,
    string OsDescription,
    string Architecture,
    string Framework,
    bool IsElevated,
    IReadOnlyList<DriveSummary> Drives);

public static class SystemInfoProvider
{
    /// <summary>目前平台名稱：windows / linux / macos / other。</summary>
    public static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : "other";

    public static SystemInfo Collect() => new(
        CurrentPlatform,
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        Environment.IsPrivilegedProcess,
        GetDrives());

    /// <summary>
    /// 列出可用的磁碟。未就緒（例如沒放光碟）或大小為 0 的虛擬檔案系統會略過。
    /// </summary>
    public static IReadOnlyList<DriveSummary> GetDrives()
    {
        var result = new List<DriveSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize == 0)
                {
                    continue;
                }

                result.Add(new DriveSummary(
                    drive.Name,
                    drive.DriveType.ToString(),
                    drive.DriveFormat,
                    string.IsNullOrEmpty(drive.VolumeLabel) ? null : drive.VolumeLabel,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 讀取中途失效的磁碟（例如網路磁碟斷線）直接略過
            }
        }

        return result;
    }
}
