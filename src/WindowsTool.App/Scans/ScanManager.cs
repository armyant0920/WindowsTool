using System.Collections.Concurrent;
using WindowsTool.Core.Scanning;

namespace WindowsTool.App.Scans;

public enum ScanStatus
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>一次掃描：在背景執行，結果保留在記憶體中供 API 查詢。</summary>
public sealed class ScanSession : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    internal ScanSession(string id, string root, ScanOptions options, long? expectedBytes)
    {
        Id = id;
        Root = root;
        Options = options;
        ExpectedBytes = expectedBytes;
        StartedUtc = DateTime.UtcNow;
        Completion = Task.Run(RunAsync);
    }

    public string Id { get; }

    public string Root { get; }

    public ScanOptions Options { get; }

    /// <summary>掃描磁碟根目錄時，預期的總量（已用空間），用來顯示百分比。</summary>
    public long? ExpectedBytes { get; }

    public DateTime StartedUtc { get; }

    public DateTime? FinishedUtc { get; private set; }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public ScanProgress Progress { get; } = new();

    private volatile ScanStatus _status = ScanStatus.Running;

    /// <summary>volatile：背景執行緒先寫入 Result / Error 再更新狀態，讀取端看到結束狀態時一定也看得到結果。</summary>
    public ScanStatus Status => _status;

    public ScanResult? Result { get; private set; }

    public string? Error { get; private set; }

    /// <summary>掃描結束（不論成功、取消或失敗）時完成，不會拋出例外。</summary>
    public Task Completion { get; }

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已經結束並釋放
        }
    }

    private async Task RunAsync()
    {
        var final = ScanStatus.Failed;
        try
        {
            Result = await FileSystemScanner.ScanAsync(Root, Options, Progress, _cts.Token).ConfigureAwait(false);
            final = ScanStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            final = ScanStatus.Cancelled;
        }
#pragma warning disable CA1031 // 掃描失敗只回報錯誤，不讓程式結束
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = ex.Message;
        }
        finally
        {
            _stopwatch.Stop();
            FinishedUtc = DateTime.UtcNow;
            _status = final; // 最後才更新狀態（volatile 寫入）
        }
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>管理進行中與最近完成的掃描。只保留最近幾次，避免佔用太多記憶體。</summary>
public sealed class ScanManager : IDisposable
{
    public const int MaxSessions = 3;

    private readonly ConcurrentDictionary<string, ScanSession> _sessions = new();
    private readonly Lock _lock = new();

    public ScanSession Start(string root, ScanOptions options)
    {
        var normalized = FileSystemScanner.NormalizeRoot(root);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"找不到資料夾：{normalized}");
        }

        lock (_lock)
        {
            EvictOldSessions();
            var session = new ScanSession(Guid.NewGuid().ToString("N")[..12], normalized, options, GetExpectedBytes(normalized));
            _sessions[session.Id] = session;
            return session;
        }
    }

    public ScanSession? Get(string id) => _sessions.GetValueOrDefault(id);

    public bool Remove(string id)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        session.Cancel();
        _ = session.Completion.ContinueWith(_ => session.Dispose(), TaskScheduler.Default);
        return true;
    }

    private void EvictOldSessions()
    {
        // 為新的掃描騰出位置：先移除已結束的，最舊的優先
        var ordered = _sessions.Values.OrderBy(s => s.StartedUtc).ToList();
        var excess = ordered.Count - (MaxSessions - 1);
        foreach (var session in ordered.OrderBy(s => s.Status == ScanStatus.Running).Take(Math.Max(0, excess)))
        {
            Remove(session.Id);
        }
    }

    /// <summary>根目錄是磁碟根時，回傳已使用空間，用來估算進度百分比。</summary>
    private static long? GetExpectedBytes(string root)
    {
        try
        {
            var pathRoot = Path.GetPathRoot(root);
            if (pathRoot is null || !string.Equals(Path.TrimEndingDirectorySeparator(pathRoot), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var drive = new DriveInfo(pathRoot);
            return drive.IsReady ? drive.TotalSize - drive.TotalFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var id in _sessions.Keys)
        {
            Remove(id);
        }
    }
}
