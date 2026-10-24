using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WindowsTool.Core.Execution;

/// <summary>
/// 操作紀錄（PLAN 6.2）：<c>logs/actions-YYYYMM.jsonl</c>，一行一筆，只附加不修改。
/// 每一次執行（包含被擋下、略過、失敗的項目）都會寫入。
/// </summary>
public sealed class ActionLog
{
    private static readonly JsonSerializerOptions Options = new(ExecutionJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = ExecutionJsonContext.Default,
    };

    private readonly Lock _lock = new();

    public ActionLog(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        Directory = logsDirectory;
    }

    public string Directory { get; }

    public void Append(ActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Ts == default)
        {
            entry.Ts = DateTime.UtcNow;
        }

        var line = JsonSerializer.Serialize(entry, Options.GetTypeInfo(typeof(ActionLogEntry))) + Environment.NewLine;
        lock (_lock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(FileFor(entry.Ts), line);
        }
    }

    /// <summary>讀取紀錄（新的在前）。損壞的行會略過。</summary>
    public IReadOnlyList<ActionLogEntry> Read(DateTime? fromUtc = null, DateTime? toUtc = null, string? planId = null, int limit = 500)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var files = System.IO.Directory.GetFiles(Directory, "actions-*.jsonl").Order(StringComparer.Ordinal).Reverse();
        var result = new List<ActionLogEntry>();
        lock (_lock)
        {
            foreach (var file in files)
            {
                var entries = new List<ActionLogEntry>();
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        if (JsonSerializer.Deserialize(line, ExecutionJsonContext.Default.ActionLogEntry) is { } entry)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (JsonException)
                    {
                        // 損壞的行（例如寫入到一半時程式被關閉）
                    }
                }

                for (var i = entries.Count - 1; i >= 0; i--)
                {
                    var entry = entries[i];
                    if ((fromUtc is { } from && entry.Ts < from) || (toUtc is { } to && entry.Ts > to)
                        || (planId is not null && entry.PlanId != planId))
                    {
                        continue;
                    }

                    result.Add(entry);
                    if (result.Count >= limit)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }

    private string FileFor(DateTime ts) =>
        Path.Combine(Directory, $"actions-{ts.ToLocalTime().ToString("yyyyMM", CultureInfo.InvariantCulture)}.jsonl");
}
