using WindowsTool.App.Common;
using WindowsTool.Core.FileSystem;
using WindowsTool.Core.Rules;
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
public sealed class ScanSession : IBackgroundSession
{
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    private readonly Func<RuleSnapshot>? _rules;
    private readonly Lock _evaluationLock = new();
    private (long Version, RuleEvaluation Evaluation)? _evaluation;

    internal ScanSession(string id, string root, ScanOptions options, long? expectedBytes, Func<RuleSnapshot>? rules = null)
    {
        _rules = rules;
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

    /// <summary>使用者勾選的項目與已經處理過的項目。</summary>
    public ScanSelection Selection { get; } = new();

    private volatile ScanStatus _status = ScanStatus.Running;

    /// <summary>volatile：背景執行緒先寫入 Result / Error 再更新狀態，讀取端看到結束狀態時一定也看得到結果。</summary>
    public ScanStatus Status => _status;

    public bool IsRunning => _status == ScanStatus.Running;

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
            if (_rules is not null)
            {
                // 掃描完成時先套用規則，第一次查詢清單就不用等
                Evaluate(_rules(), _cts.Token);
            }

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

    /// <summary>
    /// 以指定版本的規則評估掃描結果；同一個版本只計算一次，規則變動後再查詢時重新計算。
    /// 掃描尚未完成時回傳 null。
    /// </summary>
    public RuleEvaluation? GetEvaluation(RuleSnapshot rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return Result is null ? null : Evaluate(rules, CancellationToken.None);
    }

    private RuleEvaluation Evaluate(RuleSnapshot rules, CancellationToken cancellationToken)
    {
        lock (_evaluationLock)
        {
            if (_evaluation is { } cached && cached.Version == rules.Version)
            {
                return cached.Evaluation;
            }

            var evaluation = RuleEngine.Evaluate(Result!, rules.Set, new FileSystemEntryProbe(), DateTime.UtcNow, cancellationToken);
            _evaluation = (rules.Version, evaluation);
            return evaluation;
        }
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>管理進行中與最近完成的掃描。只保留最近幾次，避免佔用太多記憶體。</summary>
public sealed class ScanManager : IDisposable
{
    public const int MaxSessions = 3;

    private readonly SessionStore<ScanSession> _store = new(MaxSessions);
    private readonly RuleStore? _rules;

    public ScanManager(RuleStore? rules = null)
    {
        _rules = rules;
    }

    public ScanSession Start(string root, ScanOptions options)
    {
        var normalized = PathUtil.NormalizeUserPath(root);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"找不到資料夾：{normalized}");
        }

        var expected = GetExpectedBytes(normalized);
        Func<RuleSnapshot>? rules = _rules is null ? null : () => _rules.Current;
        return _store.Add(id => new ScanSession(id, normalized, options, expected, rules));
    }

    public ScanSession? Get(string id) => _store.Get(id);

    public bool Remove(string id) => _store.Remove(id);

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

    public void Dispose() => _store.Dispose();
}
