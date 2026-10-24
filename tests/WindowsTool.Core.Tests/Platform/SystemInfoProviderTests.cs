using WindowsTool.Core.Platform;

namespace WindowsTool.Core.Tests.Platform;

public sealed class SystemInfoProviderTests
{
    [Fact]
    public void Collect_returns_current_platform_and_at_least_one_drive()
    {
        var info = SystemInfoProvider.Collect();

        info.Platform.ShouldBe(
            OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "macos"
            : "other");
        info.OsDescription.ShouldNotBeNullOrWhiteSpace();
        info.Drives.ShouldNotBeEmpty();
    }

    [Fact]
    public void Drives_have_consistent_sizes()
    {
        foreach (var drive in SystemInfoProvider.GetDrives())
        {
            drive.TotalBytes.ShouldBeGreaterThan(0);
            drive.FreeBytes.ShouldBeInRange(0, drive.TotalBytes);
        }
    }
}
