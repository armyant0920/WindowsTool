using System.Collections.Concurrent;

namespace WindowsTool.App.Common;

/// <summary>在背景執行的工作（掃描、搜尋）。</summary>
public interface IBackgroundSession : IDisposable
{
    string Id { get; }

    DateTime StartedUtc { get; }

    bool IsRunning { get; }

    /// <summary>工作結束（不論成功、取消或失敗）時完成，不會拋出例外。</summary>
    Task Completion { get; }

    void Cancel();
}

/// <summary>保存最近幾次的工作結果。超過上限時先移除已結束的、最舊的。</summary>
public sealed class SessionStore<T>(int maxSessions) : IDisposable
    where T : class, IBackgroundSession
{
    private readonly ConcurrentDictionary<string, T> _sessions = new();
    private readonly Lock _lock = new();

    public int MaxSessions { get; } = maxSessions;

    public T Add(Func<string, T> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        lock (_lock)
        {
            var ordered = _sessions.Values
                .OrderBy(s => s.IsRunning)   // 已結束的優先移除
                .ThenBy(s => s.StartedUtc)   // 其次是最舊的
                .ToList();
            foreach (var old in ordered.Take(Math.Max(0, ordered.Count - (MaxSessions - 1))))
            {
                Remove(old.Id);
            }

            var session = create(Guid.NewGuid().ToString("N")[..12]);
            _sessions[session.Id] = session;
            return session;
        }
    }

    public T? Get(string id) => _sessions.GetValueOrDefault(id);

    /// <summary>移除並取消；資源在工作真正結束後才釋放。</summary>
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

    public void Dispose()
    {
        foreach (var id in _sessions.Keys)
        {
            Remove(id);
        }
    }
}
