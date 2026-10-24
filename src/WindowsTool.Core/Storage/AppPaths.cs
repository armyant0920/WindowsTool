namespace WindowsTool.Core.Storage;

/// <summary>
/// 資料存放位置（PLAN 6.1）：
/// 程式旁邊有 <c>WindowsTool.data/</c> 資料夾或 <c>portable</c> 標記檔時為可攜模式，資料放在 <c>WindowsTool.data/</c>；
/// 否則放在使用者的設定資料夾（Windows：<c>%APPDATA%\WindowsTool</c>）。
/// </summary>
public sealed class AppPaths
{
    public const string PortableFolderName = "WindowsTool.data";
    public const string PortableMarkerName = "portable";

    private AppPaths(string dataDirectory, bool isPortable)
    {
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
    }

    public string DataDirectory { get; }

    public bool IsPortable { get; }

    public string UserRulesFile => Path.Combine(DataDirectory, "rules.user.json");

    /// <param name="overrideDirectory">明確指定的位置（設定 <c>WindowsTool:DataDirectory</c>，測試也用這個）。</param>
    /// <param name="baseDirectory">程式所在的資料夾。</param>
    public static AppPaths Resolve(string? overrideDirectory, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);

        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return new AppPaths(Path.GetFullPath(overrideDirectory), isPortable: false);
        }

        var portable = Path.Combine(baseDirectory, PortableFolderName);
        if (Directory.Exists(portable) || File.Exists(Path.Combine(baseDirectory, PortableMarkerName)))
        {
            return new AppPaths(portable, isPortable: true);
        }

        return new AppPaths(DefaultDirectory(), isPortable: false);
    }

    private static string DefaultDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsTool");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "WindowsTool");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "windowstool");
    }
}
