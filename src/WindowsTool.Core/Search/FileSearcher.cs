using System.IO.Enumeration;
using WindowsTool.Core.FileSystem;

namespace WindowsTool.Core.Search;

/// <summary>
/// 快速檔案搜尋（PLAN 9A）：只列舉目錄項目、比對中繼資料，不讀檔案內容，並以 <see cref="ParallelWalker"/> 平行走訪。
/// 比對在 <see cref="FileSystemEntry"/> 上直接進行，不符合的項目不配置任何字串。
/// </summary>
public static class FileSearcher
{
    /// <summary>
    /// 正規化搜尋位置並去掉重疊的部分。不存在的位置會拋出 <see cref="DirectoryNotFoundException"/>。
    /// </summary>
    public static IReadOnlyList<string> PrepareRoots(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var normalized = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(PathUtil.NormalizeUserPath)
            .ToList();

        if (normalized.Count == 0)
        {
            throw new ArgumentException("請至少指定一個搜尋位置。");
        }

        var missing = normalized.Where(r => !Directory.Exists(r)).ToList();
        if (missing.Count > 0)
        {
            throw new DirectoryNotFoundException($"找不到資料夾：{string.Join("、", missing)}");
        }

        return PathUtil.RemoveNestedRoots(normalized);
    }

    /// <summary>
    /// 執行搜尋，結果陸續加入 <paramref name="results"/>。達到結果上限時正常結束（<see cref="SearchResults.Truncated"/> 為 true）；
    /// 被取消時拋出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public static async Task SearchAsync(SearchQuery query, SearchResults results, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(results);

        var roots = PrepareRoots(query.Roots);
        var matcher = NameMatcher.Create(query.Pattern, query.Mode);

        using var limitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var handler = new SearchHandler(query, matcher, results, limitCts);
        var options = new WalkOptions
        {
            IncludeHidden = query.IncludeHidden,
            IncludeSystem = query.IncludeSystem,
            MaxDepth = query.MaxDepth,
            ExcludeDirectoryNames = query.ExcludeDirectoryNames,
        };

        try
        {
            await Task.WhenAll(roots.Select(root =>
                ParallelWalker.WalkAsync(root, (byte)0, handler, options, limitCts.Token))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (results.Truncated && !cancellationToken.IsCancellationRequested)
        {
            // 達到結果上限而提前停止，不算錯誤
        }
    }

    private sealed class SearchHandler : IWalkHandler<byte>
    {
        [ThreadStatic]
        private static char[]? t_pathBuffer;

        private readonly SearchQuery _query;
        private readonly NameMatcher _matcher;
        private readonly SearchResults _results;
        private readonly CancellationTokenSource _limitCts;
        private readonly HashSet<string>? _extensions;
        private readonly bool _hasSizeFilter;
        private readonly long _afterTicks;
        private readonly long _beforeTicks;

        public SearchHandler(SearchQuery query, NameMatcher matcher, SearchResults results, CancellationTokenSource limitCts)
        {
            _query = query;
            _matcher = matcher;
            _results = results;
            _limitCts = limitCts;

            var extensions = query.Extensions?
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .ToList();
            _extensions = extensions is { Count: > 0 } ? new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase) : null;

            _hasSizeFilter = query.MinSize is not null || query.MaxSize is not null;
            _afterTicks = query.ModifiedAfterUtc?.ToUniversalTime().Ticks ?? long.MinValue;
            _beforeTicks = query.ModifiedBeforeUtc?.ToUniversalTime().Ticks ?? long.MaxValue;
        }

        /// <summary>有副檔名或大小條件時，只會找到檔案。</summary>
        private bool FilesOnly => _extensions is not null || _hasSizeFilter;

        public void OnDirectoryStarted(byte node, string path) => _results.DirectoryStarted(path);

        public void OnFile(byte parent, ref FileSystemEntry entry, EntryFlags flags)
        {
            if (_query.Kind == SearchKind.Dir || !ExtensionMatches(ref entry))
            {
                return;
            }

            var size = entry.Length;
            if ((_query.MinSize is { } min && size < min) || (_query.MaxSize is { } max && size > max))
            {
                return;
            }

            if (DateMatches(ref entry) && NameMatches(ref entry))
            {
                Add(ref entry, SearchHitKind.File, size, flags);
            }
        }

        public bool OnDirectory(byte parent, ref FileSystemEntry entry, EntryFlags flags, bool canDescend, out byte child)
        {
            child = 0;
            if (_query.Kind != SearchKind.File && !FilesOnly && DateMatches(ref entry) && NameMatches(ref entry))
            {
                Add(ref entry, SearchHitKind.Dir, 0, flags);
            }

            return true; // 不論是否符合都繼續往下找
        }

        public void OnLink(byte parent, ref FileSystemEntry entry, EntryFlags flags)
        {
            var isDirectory = entry.IsDirectory;
            if ((_query.Kind == SearchKind.File && isDirectory) || (_query.Kind == SearchKind.Dir && !isDirectory))
            {
                return;
            }

            // 連結本身沒有大小；有大小條件時一律不列出
            if (_hasSizeFilter || (_extensions is not null && (isDirectory || !ExtensionMatches(ref entry))))
            {
                return;
            }

            if (DateMatches(ref entry) && NameMatches(ref entry))
            {
                Add(ref entry, SearchHitKind.Link, 0, flags);
            }
        }

        public void OnDirectoryError(byte node, string path, Exception exception) => _results.AddDenied(path);

        public void OnDirectoryCompleted(byte node)
        {
        }

        private bool ExtensionMatches(ref FileSystemEntry entry) =>
            _extensions is null
            || _extensions.GetAlternateLookup<ReadOnlySpan<char>>().Contains(Path.GetExtension(entry.FileName));

        private bool DateMatches(ref FileSystemEntry entry)
        {
            if (_afterTicks == long.MinValue && _beforeTicks == long.MaxValue)
            {
                return true;
            }

            var ticks = entry.LastWriteTimeUtc.UtcTicks;
            return ticks >= _afterTicks && ticks <= _beforeTicks;
        }

        private bool NameMatches(ref FileSystemEntry entry)
        {
            if (_matcher.MatchesEverything)
            {
                return true;
            }

            if (!_query.MatchFullPath)
            {
                return _matcher.IsMatch(entry.FileName);
            }

            // 在執行緒共用的緩衝區組出完整路徑，避免配置字串
            var directory = entry.Directory;
            var name = entry.FileName;
            var needsSeparator = directory.Length > 0 && directory[^1] is not ('\\' or '/');
            var length = directory.Length + (needsSeparator ? 1 : 0) + name.Length;

            var buffer = t_pathBuffer;
            if (buffer is null || buffer.Length < length)
            {
                buffer = t_pathBuffer = new char[Math.Max(length, 1024)];
            }

            directory.CopyTo(buffer);
            var position = directory.Length;
            if (needsSeparator)
            {
                buffer[position++] = Path.DirectorySeparatorChar;
            }

            name.CopyTo(buffer.AsSpan(position));
            return _matcher.IsMatch(buffer.AsSpan(0, length));
        }

        private void Add(ref FileSystemEntry entry, SearchHitKind kind, long size, EntryFlags flags)
        {
            var hit = new SearchHit(
                entry.ToFullPath(),
                entry.FileName.ToString(),
                kind,
                size,
                entry.LastWriteTimeUtc.UtcDateTime,
                MapFlags(flags));

            if (!_results.TryAdd(hit))
            {
                _limitCts.Cancel(); // 已達上限：停止所有走訪
            }
        }

        private static SearchHitFlags MapFlags(EntryFlags flags)
        {
            var result = SearchHitFlags.None;
            if ((flags & EntryFlags.Hidden) != 0)
            {
                result |= SearchHitFlags.Hidden;
            }

            if ((flags & EntryFlags.System) != 0)
            {
                result |= SearchHitFlags.System;
            }

            if ((flags & EntryFlags.CloudPlaceholder) != 0)
            {
                result |= SearchHitFlags.CloudPlaceholder;
            }

            return result;
        }
    }
}
