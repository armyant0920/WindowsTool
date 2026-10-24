using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using WindowsTool.Core.FileSystem;

namespace WindowsTool.Core.Scanning;

/// <summary>無法讀取的路徑與原因。</summary>
public sealed record ScanError(string Path, string Reason);

/// <summary>
/// 一般模式的掃描器（PLAN 9.1 / 9.2）：走訪整個目錄樹並彙總大小。
/// 記憶體控管：目錄節點全部保留；檔案只保留「≥ 門檻」或「每個目錄前 N 大」的項目，其餘只計入彙總。
/// </summary>
public static class FileSystemScanner
{
    private const int ExtStatsPerDirectory = 8;
    private const int ProgressFlushInterval = 4096;

    public static async Task<ScanResult> ScanAsync(
        string root,
        ScanOptions options,
        ScanProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);

        var rootPath = PathUtil.NormalizeUserPath(root);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"找不到資料夾：{rootPath}");
        }

        var started = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var rootInfo = new DirectoryInfo(rootPath);
        var rootNode = new DirNode(rootPath, null, ScanItemFlags.None, rootInfo.LastWriteTimeUtc.Ticks);
        var handler = new ScanHandler(options, progress);

        var walkOptions = new WalkOptions
        {
            IncludeHidden = options.IncludeHidden,
            IncludeSystem = options.IncludeSystem,
            ExcludeDirectoryNames = options.ExcludeDirectoryNames,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
        };

        await ParallelWalker.WalkAsync(rootPath, rootNode, handler, walkOptions, cancellationToken).ConfigureAwait(false);

        var byId = Finish(rootNode);
        stopwatch.Stop();

        return new ScanResult(rootNode, byId, [.. handler.Errors], handler.ErrorCount, started, stopwatch.Elapsed);
    }

    /// <summary>依 BFS 順序配發 id，再由下往上彙總大小。</summary>
    private static ScanNode?[] Finish(DirNode root)
    {
        var order = new List<DirNode> { root };
        for (var i = 0; i < order.Count; i++)
        {
            order.AddRange(order[i].Dirs);
        }

        var all = new List<ScanNode?>(order.Count * 2) { null }; // id 0 不使用
        foreach (var dir in order)
        {
            dir.Id = all.Count;
            all.Add(dir);
            foreach (var file in dir.Files)
            {
                file.Id = all.Count;
                all.Add(file);
            }
        }

        for (var i = order.Count - 1; i >= 0; i--)
        {
            order[i].Aggregate();
            order[i].Compact();
        }

        return [.. all];
    }

    private static ScanItemFlags MapFlags(EntryFlags flags)
    {
        var result = ScanItemFlags.None;
        if ((flags & EntryFlags.Hidden) != 0)
        {
            result |= ScanItemFlags.Hidden;
        }

        if ((flags & EntryFlags.System) != 0)
        {
            result |= ScanItemFlags.System;
        }

        if ((flags & EntryFlags.CloudPlaceholder) != 0)
        {
            result |= ScanItemFlags.CloudPlaceholder;
        }

        return result;
    }

    private readonly record struct SmallFile(string Name, long Size, long Ticks, ScanItemFlags Flags);

    private struct ExtAccumulator
    {
        public int Count;
        public long Size;
    }

    /// <summary>每條執行緒一份的暫存區：同一個目錄的回呼都在同一條執行緒上依序進行。</summary>
    private sealed class DirScratch
    {
        /// <summary>超過這個數量的副檔名就整個清掉，避免怪異檔名讓字典無限成長。</summary>
        public const int MaxRetainedExtensions = 10_000;

        public readonly PriorityQueue<SmallFile, long> Heap = new();

        /// <summary>
        /// 副檔名累計。字典跨目錄保留，副檔名字串因此可以重複使用（不必每個目錄重新配置）；
        /// 每個目錄用到的副檔名記在 <see cref="TouchedExt"/>，結束時歸零。
        /// </summary>
        public readonly Dictionary<string, ExtAccumulator> Ext = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> TouchedExt = [];
        public long PendingFiles;
        public long PendingBytes;
    }

    private sealed class ScanHandler(ScanOptions options, ScanProgress progress) : IWalkHandler<DirNode>
    {
        [ThreadStatic]
        private static DirScratch? t_scratch;

        private readonly ConcurrentQueue<ScanError> _errors = new();
        private int _recordedErrors;
        private long _errorCount;

        public IEnumerable<ScanError> Errors => _errors;

        public long ErrorCount => Interlocked.Read(ref _errorCount);

        private static DirScratch Scratch => t_scratch ??= new DirScratch();

        public void OnDirectoryStarted(DirNode node, string path)
        {
            progress.DirectoryStarted(path);
            var scratch = Scratch;
            scratch.Heap.Clear();
            scratch.PendingFiles = 0;
            scratch.PendingBytes = 0;
        }

        public void OnFile(DirNode parent, ref FileSystemEntry entry, EntryFlags flags)
        {
            var size = entry.Length;
            var ticks = entry.LastWriteTimeUtc.UtcTicks;
            var scratch = Scratch;

            parent.OwnFileCount++;
            parent.OwnSize += size;
            if (ticks > parent.OwnNewestTicks)
            {
                parent.OwnNewestTicks = ticks;
            }

            // 副檔名統計：用 span 查字典，只有這條執行緒第一次看到的副檔名才配置字串
            var extension = Path.GetExtension(entry.FileName);
            var lookup = scratch.Ext.GetAlternateLookup<ReadOnlySpan<char>>();
            ref var ext = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, extension, out _);
            if (ext.Count == 0)
            {
                lookup.TryGetValue(extension, out var key, out _);
                scratch.TouchedExt.Add(key!);
            }

            ext.Count++;
            ext.Size += size;

            scratch.PendingBytes += size;
            if (++scratch.PendingFiles >= ProgressFlushInterval)
            {
                FlushProgress(scratch);
            }

            var itemFlags = MapFlags(flags);
            if (size >= options.KeepFileThreshold)
            {
                parent.AddFile(new FileNode(entry.FileName.ToString(), parent, ScanItemKind.File, size, ticks, itemFlags));
                return;
            }

            var heap = scratch.Heap;
            if (heap.Count < options.TopFilesPerDirectory)
            {
                heap.Enqueue(new SmallFile(entry.FileName.ToString(), size, ticks, itemFlags), size);
            }
            else if (options.TopFilesPerDirectory > 0 && heap.TryPeek(out _, out var smallest) && size > smallest)
            {
                var evicted = heap.DequeueEnqueue(new SmallFile(entry.FileName.ToString(), size, ticks, itemFlags), size);
                parent.OtherFileCount++;
                parent.OtherFileSize += evicted.Size;
            }
            else
            {
                parent.OtherFileCount++;
                parent.OtherFileSize += size;
            }
        }

        public bool OnDirectory(DirNode parent, ref FileSystemEntry entry, EntryFlags flags, bool canDescend, out DirNode child)
        {
            child = new DirNode(entry.FileName.ToString(), parent, MapFlags(flags), entry.LastWriteTimeUtc.UtcTicks);
            parent.AddDir(child);
            return true;
        }

        public void OnLink(DirNode parent, ref FileSystemEntry entry, EntryFlags flags)
        {
            parent.AddFile(new FileNode(
                entry.FileName.ToString(),
                parent,
                ScanItemKind.Link,
                0,
                entry.LastWriteTimeUtc.UtcTicks,
                MapFlags(flags) | ScanItemFlags.ReparsePoint));
        }

        public void OnDirectoryError(DirNode node, string path, Exception exception)
        {
            node.Flags |= ScanItemFlags.AccessDenied;
            progress.AddDenied();
            Interlocked.Increment(ref _errorCount);

            if (Interlocked.Increment(ref _recordedErrors) <= options.MaxDeniedPathsRecorded)
            {
                var reason = exception is UnauthorizedAccessException ? "權限不足" : exception.Message;
                _errors.Enqueue(new ScanError(path, reason));
            }
        }

        public void OnDirectoryCompleted(DirNode node)
        {
            var scratch = Scratch;
            FlushProgress(scratch);

            while (scratch.Heap.TryDequeue(out var small, out _))
            {
                node.AddFile(new FileNode(small.Name, node, ScanItemKind.File, small.Size, small.Ticks, small.Flags));
            }

            if (scratch.TouchedExt.Count > 0)
            {
                node.OwnExtStats = TakeExtStats(scratch);
            }
        }

        private void FlushProgress(DirScratch scratch)
        {
            if (scratch.PendingFiles > 0 || scratch.PendingBytes > 0)
            {
                progress.AddFiles(scratch.PendingFiles, scratch.PendingBytes);
                scratch.PendingFiles = 0;
                scratch.PendingBytes = 0;
            }
        }

        /// <summary>取出這個目錄的副檔名統計（前幾名 + 其他），並把累計歸零。</summary>
        private static ExtStat[] TakeExtStats(DirScratch scratch)
        {
            var stats = new ExtStat[scratch.TouchedExt.Count];
            for (var i = 0; i < stats.Length; i++)
            {
                var key = scratch.TouchedExt[i];
                ref var acc = ref CollectionsMarshal.GetValueRefOrNullRef(scratch.Ext, key);
                stats[i] = new ExtStat(key, acc.Count, acc.Size);
                acc = default;
            }

            scratch.TouchedExt.Clear();
            if (scratch.Ext.Count > DirScratch.MaxRetainedExtensions)
            {
                scratch.Ext.Clear();
            }

            Array.Sort(stats, static (a, b) =>
            {
                var bySize = b.Size.CompareTo(a.Size);
                return bySize != 0 ? bySize : StringComparer.OrdinalIgnoreCase.Compare(a.Ext, b.Ext);
            });

            if (stats.Length <= ExtStatsPerDirectory)
            {
                return stats;
            }

            var result = new ExtStat[ExtStatsPerDirectory];
            Array.Copy(stats, result, ExtStatsPerDirectory - 1);
            int otherCount = 0;
            long otherSize = 0;
            for (var i = ExtStatsPerDirectory - 1; i < stats.Length; i++)
            {
                otherCount += stats[i].Count;
                otherSize += stats[i].Size;
            }

            result[^1] = new ExtStat(ExtStat.OtherExtension, otherCount, otherSize);
            return result;
        }
    }
}
