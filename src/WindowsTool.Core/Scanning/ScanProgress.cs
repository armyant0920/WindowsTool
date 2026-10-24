namespace WindowsTool.Core.Scanning;

public readonly record struct ScanProgressSnapshot(
    long Dirs,
    long Files,
    long Bytes,
    long Denied,
    string? CurrentPath);

/// <summary>掃描進度計數器，可以從任何執行緒讀取。</summary>
public sealed class ScanProgress
{
    private long _dirs;
    private long _files;
    private long _bytes;
    private long _denied;
    private string? _currentPath;

    public ScanProgressSnapshot Snapshot() => new(
        Interlocked.Read(ref _dirs),
        Interlocked.Read(ref _files),
        Interlocked.Read(ref _bytes),
        Interlocked.Read(ref _denied),
        Volatile.Read(ref _currentPath));

    internal void DirectoryStarted(string path)
    {
        Interlocked.Increment(ref _dirs);
        Volatile.Write(ref _currentPath, path);
    }

    internal void AddFiles(long count, long bytes)
    {
        Interlocked.Add(ref _files, count);
        Interlocked.Add(ref _bytes, bytes);
    }

    internal void AddDenied() => Interlocked.Increment(ref _denied);
}
