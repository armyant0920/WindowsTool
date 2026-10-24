using System.Text;

namespace WindowsTool.Core.Scanning;

public enum ScanItemKind
{
    Dir,
    File,
    Link,
}

[Flags]
public enum ScanItemFlags
{
    None = 0,
    Hidden = 1 << 0,
    System = 1 << 1,
    CloudPlaceholder = 1 << 2,

    /// <summary>Symlink / Junction，不跟進。</summary>
    ReparsePoint = 1 << 3,

    /// <summary>這個目錄本身無法讀取，大小可能被低估。</summary>
    AccessDenied = 1 << 4,
}

/// <summary>副檔名統計（只保留前幾名，其餘歸到 <see cref="OtherExtension"/>）。</summary>
public readonly record struct ExtStat(string Ext, int Count, long Size)
{
    public const string OtherExtension = "*";
}

/// <summary>掃描結果中的一個項目。路徑不逐一儲存，需要時沿著 Parent 組出來，以節省記憶體。</summary>
public abstract class ScanNode
{
    protected ScanNode(string name, DirNode? parent, ScanItemFlags flags, long lastWriteTicks)
    {
        Name = name;
        Parent = parent;
        Flags = flags;
        LastWriteTicks = lastWriteTicks;
    }

    public int Id { get; internal set; }

    public string Name { get; }

    public DirNode? Parent { get; }

    public ScanItemFlags Flags { get; internal set; }

    /// <summary>項目本身的最後修改時間（UTC ticks）。</summary>
    public long LastWriteTicks { get; }

    public abstract ScanItemKind Kind { get; }

    /// <summary>邏輯大小：檔案為本身大小，目錄為所有子孫的總和。</summary>
    public abstract long Size { get; }

    /// <summary>最新修改時間：檔案為本身，目錄為整個子樹中最新的檔案。</summary>
    public abstract long NewestWriteTicks { get; }

    public DateTime LastWriteUtc => new(LastWriteTicks, DateTimeKind.Utc);

    public DateTime NewestWriteUtc => new(NewestWriteTicks, DateTimeKind.Utc);

    public string GetFullPath()
    {
        if (Parent is null)
        {
            return Name; // 根節點的 Name 就是完整路徑
        }

        var names = new Stack<string>();
        ScanNode? node = this;
        while (node.Parent is not null)
        {
            names.Push(node.Name);
            node = node.Parent;
        }

        var builder = new StringBuilder(node.Name);
        foreach (var name in names)
        {
            if (builder.Length > 0 && builder[^1] != Path.DirectorySeparatorChar && builder[^1] != Path.AltDirectorySeparatorChar)
            {
                builder.Append(Path.DirectorySeparatorChar);
            }

            builder.Append(name);
        }

        return builder.ToString();
    }
}

public sealed class FileNode : ScanNode
{
    internal FileNode(string name, DirNode parent, ScanItemKind kind, long size, long lastWriteTicks, ScanItemFlags flags)
        : base(name, parent, flags, lastWriteTicks)
    {
        Kind = kind;
        Size = size;
    }

    public override ScanItemKind Kind { get; }

    public override long Size { get; }

    public override long NewestWriteTicks => LastWriteTicks;
}

public sealed class DirNode : ScanNode
{
    // 掃描中用 List 累加；結束後轉成剛好大小的陣列以節省記憶體
    private List<DirNode>? _dirs;
    private List<FileNode>? _files;
    private IReadOnlyList<DirNode> _dirArray = [];
    private IReadOnlyList<FileNode> _fileArray = [];

    internal DirNode(string name, DirNode? parent, ScanItemFlags flags, long lastWriteTicks)
        : base(name, parent, flags, lastWriteTicks)
    {
    }

    public override ScanItemKind Kind => ScanItemKind.Dir;

    public IReadOnlyList<DirNode> Dirs => _dirs ?? _dirArray;

    /// <summary>保留下來的檔案與連結（大檔案 + 每個目錄前幾大的小檔案）。</summary>
    public IReadOnlyList<FileNode> Files => _files ?? _fileArray;

    // ---- 這個目錄「直接」包含的檔案 ----
    public int OwnFileCount { get; internal set; }

    public long OwnSize { get; internal set; }

    internal long OwnNewestTicks { get; set; }

    /// <summary>沒有個別保留的小檔案數量（只計入彙總）。</summary>
    public int OtherFileCount { get; internal set; }

    public long OtherFileSize { get; internal set; }

    /// <summary>這個目錄直接包含的檔案的副檔名統計。</summary>
    public IReadOnlyList<ExtStat> OwnExtStats { get; internal set; } = [];

    // ---- 整個子樹的彙總（掃描結束後計算） ----
    private long _totalSize;
    private long _newestTicks;

    public override long Size => _totalSize;

    public override long NewestWriteTicks => _newestTicks;

    public long TotalFileCount { get; private set; }

    public int TotalDirCount { get; private set; }

    /// <summary>子樹中（含自己）無法讀取的目錄數。</summary>
    public int DeniedCount { get; private set; }

    public bool HasChildren => Dirs.Count > 0 || Files.Count > 0 || OtherFileCount > 0;

    internal void AddDir(DirNode dir) => (_dirs ??= []).Add(dir);

    internal void AddFile(FileNode file) => (_files ??= []).Add(file);

    internal void Compact()
    {
        if (_dirs is not null)
        {
            _dirArray = _dirs.ToArray();
            _dirs = null;
        }

        if (_files is not null)
        {
            _fileArray = _files.ToArray();
            _files = null;
        }
    }

    /// <summary>由下往上彙總：呼叫時所有子目錄必須已經彙總完成。</summary>
    internal void Aggregate()
    {
        long size = OwnSize;
        long files = OwnFileCount;
        var dirs = Dirs.Count;
        var newest = Math.Max(OwnNewestTicks, LastWriteTicks);
        var denied = (Flags & ScanItemFlags.AccessDenied) != 0 ? 1 : 0;

        foreach (var child in Dirs)
        {
            size += child._totalSize;
            files += child.TotalFileCount;
            dirs += child.TotalDirCount;
            newest = Math.Max(newest, child._newestTicks);
            denied += child.DeniedCount;
        }

        _totalSize = size;
        TotalFileCount = files;
        TotalDirCount = dirs;
        _newestTicks = newest;
        DeniedCount = denied;
    }
}
