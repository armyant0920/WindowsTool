using System.IO.Enumeration;
using System.Threading.Channels;

namespace WindowsTool.Core.FileSystem;

/// <summary>走訪時對項目附加的標記。</summary>
[Flags]
public enum EntryFlags
{
    None = 0,
    Hidden = 1 << 0,
    System = 1 << 1,

    /// <summary>OneDrive / iCloud 等雲端預留位置：邏輯大小不等於實際佔用。</summary>
    CloudPlaceholder = 1 << 2,
}

public sealed class WalkOptions
{
    /// <summary>同時處理的目錄數；0 = 自動（CPU 核心數，網路磁碟為 2）。</summary>
    public int MaxDegreeOfParallelism { get; init; }

    public bool IncludeHidden { get; init; } = true;

    public bool IncludeSystem { get; init; } = true;

    /// <summary>
    /// 最大深度。根目錄內的項目深度為 1；設為 n 代表深度 n 的目錄不再往下走訪。null = 不限。
    /// </summary>
    public int? MaxDepth { get; init; }

    /// <summary>完全略過的目錄名稱（不分大小寫），例如 <c>System Volume Information</c>。</summary>
    public IReadOnlyCollection<string> ExcludeDirectoryNames { get; init; } = [];
}

/// <summary>
/// <see cref="ParallelWalker"/> 的回呼。同一個目錄的所有回呼都在同一條執行緒上依序呼叫，
/// 不同目錄可能在不同執行緒上同時進行。
/// </summary>
public interface IWalkHandler<TNode>
{
    /// <summary>開始列舉一個目錄。</summary>
    void OnDirectoryStarted(TNode node, string path);

    /// <summary>一般檔案（包含雲端預留位置）。</summary>
    void OnFile(TNode parent, ref FileSystemEntry entry, EntryFlags flags);

    /// <summary>
    /// 子目錄。回傳 true 並提供 <paramref name="child"/> 才會往下走訪；
    /// <paramref name="canDescend"/> 為 false（超過深度上限）時，回傳值會被忽略。
    /// </summary>
    bool OnDirectory(TNode parent, ref FileSystemEntry entry, EntryFlags flags, bool canDescend, out TNode child);

    /// <summary>Symlink / Junction。一律不跟進，避免重複計算與無限遞迴。</summary>
    void OnLink(TNode parent, ref FileSystemEntry entry, EntryFlags flags);

    /// <summary>目錄無法讀取（權限不足、走訪途中被刪除等）。</summary>
    void OnDirectoryError(TNode node, string path, Exception exception);

    /// <summary>目錄列舉結束（不論成功或失敗都會呼叫）。</summary>
    void OnDirectoryCompleted(TNode node);
}

/// <summary>
/// 平行目錄走訪器：Scanner 與快速搜尋共用。
/// <list type="bullet">
/// <item>以 work-queue 方式非遞迴走訪，目錄層級平行化。</item>
/// <item>Reparse point（Symlink / Junction）不跟進；雲端預留位置照常走訪。</item>
/// <item>拒絕存取的目錄回報給 handler，不中斷整體走訪。</item>
/// </list>
/// </summary>
public static class ParallelWalker
{
    // .NET 的 FileAttributes 沒有定義這些雲端相關屬性
    private const int RecallOnOpen = 0x0004_0000;
    private const int Pinned = 0x0008_0000;
    private const int Unpinned = 0x0010_0000;
    private const int RecallOnDataAccess = 0x0040_0000;
    private const int CloudMask = RecallOnOpen | Pinned | Unpinned | RecallOnDataAccess | (int)FileAttributes.Offline;

    private const int CancellationCheckInterval = 256;

    public static async Task WalkAsync<TNode>(
        string rootPath,
        TNode rootNode,
        IWalkHandler<TNode> handler,
        WalkOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);

        var walk = new Walk<TNode>(handler, options, cancellationToken);
        await walk.RunAsync(new WorkItem<TNode>(rootPath, rootNode, 0)).ConfigureAwait(false);
    }

    /// <summary>依路徑判斷合適的平行度：網路磁碟 2，其他為 CPU 核心數（2–16）。</summary>
    public static int GetDefaultParallelism(string rootPath)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(rootPath));
            if (!string.IsNullOrEmpty(root)
                && (root.StartsWith(@"\\", StringComparison.Ordinal)
                    || new DriveInfo(root).DriveType == DriveType.Network))
            {
                return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // 無法判斷時用預設值
        }

        return Math.Clamp(Environment.ProcessorCount, 2, 16);
    }

    private readonly record struct WorkItem<TNode>(string Path, TNode Node, int Depth);

    private sealed class Walk<TNode>
    {
        private readonly IWalkHandler<TNode> _handler;
        private readonly WalkOptions _options;
        private readonly CancellationToken _cancellationToken;
        private readonly EnumerationOptions _enumerationOptions;
        private readonly HashSet<string> _excluded;
        private readonly Channel<WorkItem<TNode>> _queue = Channel.CreateUnbounded<WorkItem<TNode>>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        private int _pending;

        public Walk(IWalkHandler<TNode> handler, WalkOptions options, CancellationToken cancellationToken)
        {
            _handler = handler;
            _options = options;
            _cancellationToken = cancellationToken;
            _excluded = new HashSet<string>(options.ExcludeDirectoryNames, StringComparer.OrdinalIgnoreCase);

            var skip = (FileAttributes)0;
            if (!options.IncludeHidden)
            {
                skip |= FileAttributes.Hidden;
            }

            if (!options.IncludeSystem)
            {
                skip |= FileAttributes.System;
            }

            _enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false, // 要自己記錄拒絕存取的目錄
                ReturnSpecialDirectories = false,
                AttributesToSkip = skip,
                BufferSize = 64 * 1024,
            };
        }

        public async Task RunAsync(WorkItem<TNode> root)
        {
            _pending = 1;
            _queue.Writer.TryWrite(root);

            var parallelism = _options.MaxDegreeOfParallelism > 0
                ? _options.MaxDegreeOfParallelism
                : GetDefaultParallelism(root.Path);

            var workers = new Task[parallelism];
            for (var i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(WorkerAsync, CancellationToken.None);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            _cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task WorkerAsync()
        {
            try
            {
                await foreach (var item in _queue.Reader.ReadAllAsync(_cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        // ReadAllAsync 只有在需要等待時才檢查取消；佇列裡還有項目時要自己檢查
                        _cancellationToken.ThrowIfCancellationRequested();
                        ProcessDirectory(item);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref _pending) == 0)
                        {
                            _queue.Writer.TryComplete();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 讓其他 worker 也停下來，不要永遠等待
                _queue.Writer.TryComplete(ex);
                throw;
            }
        }

        private void ProcessDirectory(WorkItem<TNode> item)
        {
            _handler.OnDirectoryStarted(item.Node, item.Path);

            var subdirectories = new List<WorkItem<TNode>>();
            var visited = 0;
            try
            {
                var enumerable = new FileSystemEnumerable<byte>(
                    item.Path,
                    static (ref FileSystemEntry _) => 0,
                    _enumerationOptions)
                {
                    // 全部在 predicate 內處理完並回傳 false：不符合條件的項目完全不配置字串
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    {
                        if (++visited % CancellationCheckInterval == 0)
                        {
                            _cancellationToken.ThrowIfCancellationRequested();
                        }

                        Visit(ref entry, item, subdirectories);
                        return false;
                    },
                };

                using var enumerator = enumerable.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    // 不會進來：predicate 一律回傳 false
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                _handler.OnDirectoryError(item.Node, item.Path, ex);
            }
            finally
            {
                _handler.OnDirectoryCompleted(item.Node);
            }

            foreach (var sub in subdirectories)
            {
                Interlocked.Increment(ref _pending);
                _queue.Writer.TryWrite(sub);
            }
        }

        private void Visit(ref FileSystemEntry entry, WorkItem<TNode> item, List<WorkItem<TNode>> subdirectories)
        {
            var attributes = entry.Attributes;
            var flags = EntryFlags.None;
            if ((attributes & FileAttributes.Hidden) != 0)
            {
                flags |= EntryFlags.Hidden;
            }

            if ((attributes & FileAttributes.System) != 0)
            {
                flags |= EntryFlags.System;
            }

            if (((int)attributes & CloudMask) != 0)
            {
                flags |= EntryFlags.CloudPlaceholder;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0
                && (flags & EntryFlags.CloudPlaceholder) == 0
                && IsLink(ref entry))
            {
                _handler.OnLink(item.Node, ref entry, flags);
                return;
            }

            if (!entry.IsDirectory)
            {
                _handler.OnFile(item.Node, ref entry, flags);
                return;
            }

            if (_excluded.Count > 0 && _excluded.GetAlternateLookup<ReadOnlySpan<char>>().Contains(entry.FileName))
            {
                return;
            }

            var depth = item.Depth + 1;
            var canDescend = _options.MaxDepth is not { } max || depth < max;
            if (_handler.OnDirectory(item.Node, ref entry, flags, canDescend, out var child) && canDescend)
            {
                subdirectories.Add(new WorkItem<TNode>(entry.ToFullPath(), child, depth));
            }
        }

        /// <summary>
        /// 是否為 Symlink / Junction。其他類型的 reparse point（例如 Windows 的 AppExecLink、
        /// 重複資料刪除）視為一般項目。
        /// </summary>
        private static bool IsLink(ref FileSystemEntry entry)
        {
            try
            {
                return entry.ToFileSystemInfo().LinkTarget is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true; // 無法判斷時保守處理：不跟進
            }
        }
    }
}
