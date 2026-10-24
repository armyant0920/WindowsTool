using System.Text.Json;

namespace WindowsTool.Core.Rules;

/// <summary>某個時間點的完整規則（內建 + 使用者），以及編譯好的 <see cref="RuleSet"/>。不可變。</summary>
public sealed record RuleSnapshot(
    long Version,
    IReadOnlyList<Rule> Builtin,
    IReadOnlyList<Rule> User,
    RuleSet Set,
    string? LoadError,
    IReadOnlyList<string> LoadWarnings)
{
    public IEnumerable<Rule> All => Builtin.Concat(User);

    public Rule? Find(string id) => All.FirstOrDefault(r => r.Id == id);
}

/// <summary>規則操作不被允許（訊息可以直接顯示給使用者）。</summary>
public sealed class RuleStoreException : Exception
{
    public RuleStoreException()
    {
    }

    public RuleStoreException(string message)
        : base(message)
    {
    }

    public RuleStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 管理內建規則與 <c>rules.user.json</c>：讀取、寫入（先寫暫存檔再取代，並保留 .bak）、版本號。
/// 這裡只負責資料的一致性（id 不重複、內建規則唯讀、內建保護規則不能停用）；
/// 內容是否合理由 <see cref="RuleValidator"/> 在呼叫端檢查。
/// </summary>
public sealed class RuleStore
{
    private readonly Lock _lock = new();
    private readonly IReadOnlyList<Rule> _builtin;
    private readonly PathTokens _tokens;
    private readonly string? _platform;
    private List<Rule> _user = [];
    private HashSet<string> _disabledBuiltin = new(StringComparer.Ordinal);
    private string? _loadError;
    private List<string> _loadWarnings = [];
    private volatile RuleSnapshot _current = null!;
    private long _version;

    public RuleStore(string filePath, IReadOnlyList<Rule> builtin, PathTokens tokens, string? platform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(builtin);
        ArgumentNullException.ThrowIfNull(tokens);

        FilePath = filePath;
        _builtin = builtin;
        _tokens = tokens;
        _platform = platform;

        lock (_lock)
        {
            LoadFromDisk();
            Publish();
        }
    }

    public string FilePath { get; }

    public RuleSnapshot Current => _current;

    /// <summary>重新讀取檔案（例如使用者在外部編輯過）。</summary>
    public RuleSnapshot Reload()
    {
        lock (_lock)
        {
            LoadFromDisk();
            return Publish();
        }
    }

    public RuleSnapshot Add(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_lock)
        {
            EnsureWritable();
            EnsureUserRule(rule);
            if (FindAny(rule.Id) is not null)
            {
                throw new RuleStoreException($"已經有 id 為「{rule.Id}」的規則。");
            }

            var next = new List<Rule>(_user) { rule };
            Save(next, _disabledBuiltin);
            _user = next;
            return Publish();
        }
    }

    public RuleSnapshot Update(string id, Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_lock)
        {
            EnsureWritable();
            EnsureUserRule(rule);
            var index = IndexOfUser(id);
            if (rule.Id != id && FindAny(rule.Id) is not null)
            {
                throw new RuleStoreException($"已經有 id 為「{rule.Id}」的規則。");
            }

            var next = new List<Rule>(_user) { [index] = rule };
            Save(next, _disabledBuiltin);
            _user = next;
            return Publish();
        }
    }

    public RuleSnapshot Delete(string id)
    {
        lock (_lock)
        {
            EnsureWritable();
            var index = IndexOfUser(id);
            var next = new List<Rule>(_user);
            next.RemoveAt(index);
            Save(next, _disabledBuiltin);
            _user = next;
            return Publish();
        }
    }

    /// <summary>啟用 / 停用。內建分類規則以「停用清單」記錄；內建保護規則不能停用（PLAN 13）。</summary>
    public RuleSnapshot SetEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            EnsureWritable();
            var builtin = _builtin.FirstOrDefault(r => r.Id == id);
            if (builtin is not null)
            {
                if (builtin.Kind == RuleKind.Protect && !enabled)
                {
                    throw new RuleStoreException("內建的保護規則不能停用。");
                }

                var disabled = new HashSet<string>(_disabledBuiltin, StringComparer.Ordinal);
                if (enabled)
                {
                    disabled.Remove(id);
                }
                else
                {
                    disabled.Add(id);
                }

                Save(_user, disabled);
                _disabledBuiltin = disabled;
                return Publish();
            }

            var index = IndexOfUser(id);
            var next = new List<Rule>(_user) { [index] = _user[index] with { Enabled = enabled } };
            Save(next, _disabledBuiltin);
            _user = next;
            return Publish();
        }
    }

    /// <summary>一次加入多條規則（已經驗證過）；全部成功或全部不寫入。</summary>
    public RuleSnapshot AddRange(IReadOnlyList<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (_lock)
        {
            EnsureWritable();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                EnsureUserRule(rule);
                if (FindAny(rule.Id) is not null || !ids.Add(rule.Id))
                {
                    throw new RuleStoreException($"已經有 id 為「{rule.Id}」的規則。");
                }
            }

            var next = new List<Rule>(_user);
            next.AddRange(rules);
            Save(next, _disabledBuiltin);
            _user = next;
            return Publish();
        }
    }

    /// <summary>匯出使用者規則與內建規則的停用清單（rules.user.json 的格式）。</summary>
    public string Export()
    {
        lock (_lock)
        {
            return RuleJson.Serialize(new RuleFile { Rules = [.. _user], DisabledBuiltin = DisabledList(_disabledBuiltin) });
        }
    }

    /// <summary>產生下一個可用的使用者規則 id（user-0001、user-0002…）。</summary>
    public string NextUserId(string prefix = "user")
    {
        lock (_lock)
        {
            for (var n = _user.Count + 1; ; n++)
            {
                var id = $"{prefix}-{n:D4}";
                if (FindAny(id) is null)
                {
                    return id;
                }
            }
        }
    }

    private void LoadFromDisk()
    {
        _loadError = null;
        _loadWarnings = [];
        _user = [];
        _disabledBuiltin = new HashSet<string>(StringComparer.Ordinal);

        if (!File.Exists(FilePath))
        {
            return;
        }

        RuleFile file;
        try
        {
            file = RuleJson.ParseFile(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 不覆寫使用者的檔案：記錄錯誤並停止寫入，直到檔案修正為止
            _loadError = $"無法讀取 {FilePath}：{ex.Message}";
            return;
        }

        var ids = new HashSet<string>(_builtin.Select(r => r.Id), StringComparer.Ordinal);
        foreach (var rule in file.Rules)
        {
            if (rule.Source == RuleSource.Builtin)
            {
                _loadWarnings.Add($"略過規則「{rule.Id}」：使用者規則的 source 不能是 builtin。");
                continue;
            }

            if (!ids.Add(rule.Id))
            {
                _loadWarnings.Add($"略過規則「{rule.Id}」：id 重複。");
                continue;
            }

            _user.Add(rule);
        }

        foreach (var id in file.DisabledBuiltin ?? [])
        {
            var builtin = _builtin.FirstOrDefault(r => r.Id == id);
            if (builtin is null)
            {
                continue; // 舊版本的規則，已經不存在
            }

            if (builtin.Kind == RuleKind.Protect)
            {
                _loadWarnings.Add($"內建保護規則「{id}」不能停用，已忽略。");
                continue;
            }

            _disabledBuiltin.Add(id);
        }
    }

    private RuleSnapshot Publish()
    {
        var builtin = _builtin.Select(r => _disabledBuiltin.Contains(r.Id) ? r with { Enabled = false } : r).ToList();
        var user = _user.ToList();
        var set = RuleSet.Create(builtin.Concat(user), _tokens, _platform);
        var snapshot = new RuleSnapshot(++_version, builtin, user, set, _loadError, [.. _loadWarnings]);
        _current = snapshot;
        return snapshot;
    }

    private void Save(List<Rule> user, HashSet<string> disabled)
    {
        var json = RuleJson.Serialize(new RuleFile { Rules = [.. user], DisabledBuiltin = DisabledList(disabled) });
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temp, json);
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RuleStoreException($"無法寫入 {FilePath}：{ex.Message}", ex);
        }
    }

    private static string[]? DisabledList(HashSet<string> disabled) =>
        disabled.Count == 0 ? null : [.. disabled.Order(StringComparer.Ordinal)];

    private void EnsureWritable()
    {
        if (_loadError is not null)
        {
            throw new RuleStoreException($"{_loadError}。為了避免覆蓋你的檔案，請先修正或移除它，再重新載入規則。");
        }
    }

    private static void EnsureUserRule(Rule rule)
    {
        if (rule.Source == RuleSource.Builtin)
        {
            throw new RuleStoreException("不能新增或修改內建規則；可以複製成自訂規則後再修改。");
        }
    }

    private int IndexOfUser(string id)
    {
        var index = _user.FindIndex(r => r.Id == id);
        if (index >= 0)
        {
            return index;
        }

        throw _builtin.Any(r => r.Id == id)
            ? new RuleStoreException("內建規則不能修改或刪除，只能停用（保護規則不能停用）。")
            : new KeyNotFoundException($"找不到規則：{id}");
    }

    private Rule? FindAny(string id) => _builtin.FirstOrDefault(r => r.Id == id) ?? _user.FirstOrDefault(r => r.Id == id);
}
