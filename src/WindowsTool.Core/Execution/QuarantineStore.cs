using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using WindowsTool.Core.FileSystem;
using WindowsTool.Core.Scanning;

namespace WindowsTool.Core.Execution;

public sealed record PurgeResult(int Purged, long Bytes, IReadOnlyList<string> Failures);

/// <summary>
/// 隔離區（PLAN 10.1）：把項目搬到工具管理的資料夾，可以一鍵還原。
/// <list type="bullet">
/// <item>只在**同一個磁碟內搬移**（瞬間完成、不需要額外空間）：和資料資料夾同一個磁碟時放在 <c>&lt;資料資料夾&gt;/quarantine/</c>，
/// 其他磁碟放在該磁碟根目錄的 <c>WindowsTool.quarantine/</c>（隱藏）。兩者都有內建的保護規則。</item>
/// <item>每個項目一個子資料夾：<c>&lt;隔離區&gt;/&lt;entryId&gt;/&lt;原本的名稱&gt;</c>。</item>
/// <item>manifest：<c>&lt;資料資料夾&gt;/quarantine/manifest.jsonl</c>，只附加（隔離 / 還原 / 清空各一行）。</item>
/// </list>
/// </summary>
public sealed class QuarantineStore
{
    public const string VolumeFolderName = "WindowsTool.quarantine";

    private static readonly JsonSerializerOptions Options = new(ExecutionJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = ExecutionJsonContext.Default,
    };

    private readonly Lock _lock = new();
    private readonly string _dataDirectory;

    public QuarantineStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DefaultRoot => Path.Combine(_dataDirectory, "quarantine");

    public string ManifestPath => Path.Combine(DefaultRoot, "manifest.jsonl");

    /// <summary>這個路徑的項目會被放到哪個隔離區（同一個磁碟）。</summary>
    public string RootFor(string path)
    {
        var volume = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        var dataVolume = Path.GetPathRoot(_dataDirectory) ?? "";
        return string.Equals(volume, dataVolume, PathUtil.PathComparison) || volume.Length == 0
            ? DefaultRoot
            : Path.Combine(volume, VolumeFolderName);
    }

    /// <summary>事先判斷不能隔離的原因；可以時回傳 null。</summary>
    public string? CannotQuarantineReason(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        var root = RootFor(full);
        if (PathUtil.IsSameOrUnder(full, root) || PathUtil.IsSameOrUnder(full, DefaultRoot))
        {
            return "已經在隔離區裡。";
        }

        var volume = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(volume) || !Directory.Exists(volume))
        {
            return "找不到這個項目所在的磁碟。";
        }

        return null;
    }

    public QuarantineEntry Quarantine(string path, ScanItemKind kind, long size, string? planId)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        if (CannotQuarantineReason(full) is { } reason)
        {
            throw new IOException(reason);
        }

        var root = RootFor(full);
        var created = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        if (created && !string.Equals(root, DefaultRoot, PathUtil.PathComparison) && OperatingSystem.IsWindows())
        {
            File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.Hidden);
        }

        var entryId = NewEntryId();
        var folder = Path.Combine(root, entryId);
        Directory.CreateDirectory(folder);
        var stored = Path.Combine(folder, Path.GetFileName(full));

        try
        {
            if (kind == ScanItemKind.Dir)
            {
                Directory.Move(full, stored);
            }
            else
            {
                File.Move(full, stored);
            }
        }
        catch
        {
            TryDeleteEmpty(folder);
            throw;
        }

        var entry = new QuarantineEntry
        {
            EntryId = entryId,
            OriginalPath = full,
            StoredPath = stored,
            Kind = kind,
            Size = size,
            QuarantinedUtc = DateTime.UtcNow,
            PlanId = planId,
            Status = QuarantineStatus.Active,
        };
        Append(new QuarantineRecord { Type = "quarantined", Ts = entry.QuarantinedUtc, Entry = entry });
        return entry;
    }

    /// <summary>搬回原本的位置。原位置已經有同名項目時不覆蓋，拋出 <see cref="IOException"/>。</summary>
    public QuarantineEntry Restore(string entryId)
    {
        lock (_lock)
        {
            var entry = Find(entryId);
            if (Exists(entry.OriginalPath))
            {
                throw new IOException($"原本的位置已經有同名的項目：{entry.OriginalPath}。請先把它移走再還原。");
            }

            if (!Exists(entry.StoredPath))
            {
                throw new IOException($"隔離區中找不到這個項目：{entry.StoredPath}");
            }

            var parent = Path.GetDirectoryName(entry.OriginalPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (entry.Kind == ScanItemKind.Dir)
            {
                Directory.Move(entry.StoredPath, entry.OriginalPath);
            }
            else
            {
                File.Move(entry.StoredPath, entry.OriginalPath);
            }

            TryDeleteEmpty(Path.GetDirectoryName(entry.StoredPath));
            var now = DateTime.UtcNow;
            Append(new QuarantineRecord { Type = "restored", Ts = now, EntryId = entryId });
            return entry with { Status = QuarantineStatus.Restored, ClosedUtc = now };
        }
    }

    /// <summary>永久刪除隔離時間早於 <paramref name="beforeUtc"/> 的項目（呼叫端負責二次確認）。</summary>
    public PurgeResult Purge(DateTime beforeUtc, Action<QuarantineEntry, Exception?>? onEach = null)
    {
        var purged = 0;
        long bytes = 0;
        var failures = new List<string>();
        lock (_lock)
        {
            foreach (var entry in List().Where(e => e.QuarantinedUtc < beforeUtc))
            {
                try
                {
                    if (entry.Kind == ScanItemKind.Dir && Directory.Exists(entry.StoredPath))
                    {
                        FileOperations.DeleteDirectory(entry.StoredPath);
                    }
                    else if (File.Exists(entry.StoredPath))
                    {
                        FileOperations.DeleteFile(entry.StoredPath);
                    }

                    TryDeleteEmpty(Path.GetDirectoryName(entry.StoredPath));
                    Append(new QuarantineRecord { Type = "purged", Ts = DateTime.UtcNow, EntryId = entry.EntryId });
                    purged++;
                    bytes += entry.Size;
                    onEach?.Invoke(entry, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{entry.OriginalPath}：{ex.Message}");
                    onEach?.Invoke(entry, ex);
                }
            }
        }

        return new PurgeResult(purged, bytes, failures);
    }

    /// <summary>目前的項目（預設只列出還在隔離區中的），新的在前。</summary>
    public IReadOnlyList<QuarantineEntry> List(bool includeClosed = false)
    {
        var entries = new Dictionary<string, QuarantineEntry>(StringComparer.Ordinal);
        lock (_lock)
        {
            if (!File.Exists(ManifestPath))
            {
                return [];
            }

            foreach (var line in File.ReadLines(ManifestPath))
            {
                QuarantineRecord? record;
                try
                {
                    record = string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize(line, ExecutionJsonContext.Default.QuarantineRecord);
                }
                catch (JsonException)
                {
                    continue; // 損壞的行
                }

                switch (record?.Type)
                {
                    case "quarantined" when record.Entry is not null:
                        entries[record.Entry.EntryId] = record.Entry;
                        break;
                    case "restored" when record.EntryId is not null && entries.TryGetValue(record.EntryId, out var restored):
                        entries[record.EntryId] = restored with { Status = QuarantineStatus.Restored, ClosedUtc = record.Ts };
                        break;
                    case "purged" when record.EntryId is not null && entries.TryGetValue(record.EntryId, out var purged):
                        entries[record.EntryId] = purged with { Status = QuarantineStatus.Purged, ClosedUtc = record.Ts };
                        break;
                }
            }
        }

        return
        [
            .. entries.Values
                .Where(e => includeClosed || e.Status == QuarantineStatus.Active)
                .OrderByDescending(e => e.QuarantinedUtc),
        ];
    }

    private QuarantineEntry Find(string entryId) =>
        List().FirstOrDefault(e => e.EntryId == entryId)
        ?? throw new KeyNotFoundException($"隔離區中找不到這個項目：{entryId}");

    private void Append(QuarantineRecord record)
    {
        var line = JsonSerializer.Serialize(record, Options.GetTypeInfo(typeof(QuarantineRecord))) + Environment.NewLine;
        lock (_lock)
        {
            Directory.CreateDirectory(DefaultRoot);
            File.AppendAllText(ManifestPath, line);
        }
    }

    private static string NewEntryId() =>
        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void TryDeleteEmpty(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉空資料夾不影響結果
        }
    }
}

/// <summary>永久刪除的輔助方法：唯讀屬性會先清除（例如 .git 的物件檔），不跟進連結。</summary>
public static class FileOperations
{
    public static void DeleteFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.Delete(path);
    }

    /// <summary>刪除整個資料夾；遇到唯讀檔案時清除屬性後重試一次。連結（Junction / Symlink）只刪除連結本身。</summary>
    public static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            ClearReadOnly(path);
            Directory.Delete(path, recursive: true);
        }
    }

    private static void ClearReadOnly(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }

                if (entry is DirectoryInfo && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    stack.Push(entry.FullName);
                }
            }
        }
    }
}
