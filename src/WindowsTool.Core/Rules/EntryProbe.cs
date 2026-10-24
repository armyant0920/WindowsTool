using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace WindowsTool.Core.Rules;

/// <summary>
/// 確認資料夾內是否有符合名稱的項目（給 <c>siblingExists</c> / <c>childExists</c> 使用）。
/// 掃描器不會保留每一個小檔案（例如 <c>package.json</c>），所以需要回到檔案系統確認。
/// </summary>
public interface IEntryProbe
{
    bool HasEntry(string directoryPath, WildcardSegment pattern);
}

/// <summary>直接查詢檔案系統，結果會快取（同一次評估中同一個資料夾只查一次）。</summary>
public sealed class FileSystemEntryProbe : IEntryProbe
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly ConcurrentDictionary<(string Dir, string Pattern), bool> _cache = new();

    public bool HasEntry(string directoryPath, WildcardSegment pattern)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        return _cache.GetOrAdd((directoryPath, pattern.Pattern), _ => Probe(directoryPath, pattern));
    }

    private static bool Probe(string directoryPath, WildcardSegment pattern)
    {
        try
        {
            if (!pattern.HasWildcard)
            {
                var path = Path.Combine(directoryPath, pattern.Pattern);
                return File.Exists(path) || Directory.Exists(path);
            }

            var enumerable = new FileSystemEnumerable<bool>(directoryPath, static (ref FileSystemEntry _) => true, Options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => pattern.IsMatch(entry.FileName),
            };

            foreach (var _ in enumerable)
            {
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
