namespace WindowsTool.Core.FileSystem;

public static class PathUtil
{
    /// <summary>Windows 的路徑不分大小寫，Linux / macOS 分大小寫。</summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static StringComparer PathComparer { get; } =
        PathComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// 正規化使用者輸入的路徑：去掉前後空白與引號、展開環境變數（<c>%USERPROFILE%</c>）、
    /// 轉成完整路徑，並去掉結尾的分隔符號（磁碟根目錄 <c>C:\</c>、<c>/</c> 除外）。
    /// </summary>
    public static string NormalizeUserPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var trimmed = path.Trim().Trim('"').Trim();
        var expanded = Environment.ExpandEnvironmentVariables(trimmed);
        var full = Path.GetFullPath(expanded);

        var root = Path.GetPathRoot(full);
        if (full.Length > (root?.Length ?? 0))
        {
            full = Path.TrimEndingDirectorySeparator(full);
        }

        return full;
    }

    /// <summary><paramref name="path"/> 是否等於 <paramref name="root"/> 或位於其下。兩者都必須是正規化過的完整路徑。</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);

        if (!path.StartsWith(root, PathComparison))
        {
            return false;
        }

        if (path.Length == root.Length)
        {
            return true;
        }

        // root 本身以分隔符號結尾（例如 C:\）時，下一個字元不需要是分隔符號
        return Path.EndsInDirectorySeparator(root) || path[root.Length] is '\\' or '/';
    }

    /// <summary>
    /// 去掉重複或被其他路徑包含的根目錄（例如同時有 <c>C:\</c> 和 <c>C:\Users</c> 時只留 <c>C:\</c>），
    /// 保留原本的順序。輸入必須是正規化過的完整路徑。
    /// </summary>
    public static IReadOnlyList<string> RemoveNestedRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var result = new List<string>();
        foreach (var candidate in roots)
        {
            if (result.Any(kept => IsSameOrUnder(candidate, kept)))
            {
                continue; // 已經被前面的根目錄涵蓋（或重複）
            }

            result.RemoveAll(kept => IsSameOrUnder(kept, candidate)); // 新的涵蓋了之前的
            result.Add(candidate);
        }

        return result;
    }
}
