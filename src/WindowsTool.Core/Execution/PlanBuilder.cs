using WindowsTool.Core.FileSystem;
using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;

namespace WindowsTool.Core.Execution;

/// <summary>產生計畫與執行時需要的環境。</summary>
public sealed class PlanEnvironment
{
    public required RuleSet Rules { get; init; }

    public required IEntryProbe Probe { get; init; }

    public required ITrash Trash { get; init; }

    public required QuarantineStore Quarantine { get; init; }

    public required ICommandRunner Commands { get; init; }

    public bool IsElevated { get; init; }

    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>逐一檢查資料夾內容時最多檢查幾個項目，超過就保守地擋下。</summary>
    public int MaxEntriesToInspect { get; init; } = 500_000;
}

/// <summary>
/// 產生執行計畫並做 dry-run（PLAN 10.2）：重新檢查存在性、保護規則、連結、目的地、是否被鎖定，
/// 並去掉已經包含在上層項目裡的子項目。執行器在執行每一項之前會再用 <see cref="Check"/> 檢查一次。
/// </summary>
public static class PlanBuilder
{
    public static Plan Build(string planId, string? scanId, IEnumerable<PlanItemRequest> requests, PlanEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(environment);

        // 上層先處理，子項目才能判斷是否已經包含在上層裡
        var ordered = requests
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => r with { Path = Normalize(r.Path) })
            .DistinctBy(r => r.Path, PathUtil.PathComparer)
            .OrderBy(r => r.Path.Length)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var acceptedDirs = new HashSet<string>(PathUtil.PathComparer);
        var commandOwners = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new List<PlanItem>(ordered.Count);

        foreach (var request in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = items.Count + 1;
            PlanItemState state;
            string? reason;

            if (request.BlockedReason is not null)
            {
                (state, reason) = (PlanItemState.Blocked, request.BlockedReason);
            }
            else if (request.Action == PlanAction.Command && request.CommandId is { } commandId && commandOwners.TryGetValue(commandId, out var owner))
            {
                (state, reason) = (PlanItemState.Skipped, $"同一個清理命令只執行一次（見第 {owner} 項）。");
            }
            else if (request.Action != PlanAction.Command && FindAcceptedAncestor(request.Path, acceptedDirs) is { } ancestor)
            {
                (state, reason) = (PlanItemState.Skipped, $"已經包含在上層項目裡：{ancestor}");
            }
            else
            {
                (state, reason) = Check(request, environment, cancellationToken);
            }

            if (state is PlanItemState.Ready or PlanItemState.Warning)
            {
                if (request.Action == PlanAction.Command && request.CommandId is not null)
                {
                    commandOwners[request.CommandId] = index;
                }
                else if (request.Kind == ScanItemKind.Dir)
                {
                    acceptedDirs.Add(request.Path);
                }
            }

            items.Add(new PlanItem(index, request.ItemId, request.Path, request.Kind, request.Size, request.Action, request.Destination,
                request.CommandId, request.RuleId, request.Category, state, reason));
        }

        return new Plan(planId, environment.Clock(), scanId, items, Summarize(items));
    }

    public static PlanSummary Summarize(IReadOnlyList<PlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new PlanSummary(
            items.Count(i => i.State == PlanItemState.Ready),
            items.Count(i => i.State == PlanItemState.Warning),
            items.Count(i => i.State == PlanItemState.Blocked),
            items.Count(i => i.State == PlanItemState.Skipped),
            items.Where(i => i.WillRun && i.Action != PlanAction.Command).Sum(i => i.Size),
            items.Any(i => i.WillRun && i.Action == PlanAction.Delete),
            [.. items.Where(i => i.WillRun && i.Action == PlanAction.Command && i.CommandId is not null).Select(i => i.CommandId!).Distinct()]);
    }

    /// <summary>檢查單一項目（產生計畫時，以及執行前再檢查一次）。</summary>
    public static (PlanItemState State, string? Reason) Check(PlanItemRequest request, PlanEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);

        if (request.Action == PlanAction.Command)
        {
            return CheckCommand(request, environment);
        }

        var path = Normalize(request.Path);
        if (!Path.IsPathFullyQualified(path))
        {
            return Blocked("必須是完整路徑。");
        }

        if (IsCritical(path, environment.Rules))
        {
            return Blocked("這是系統的關鍵資料夾（磁碟根目錄、使用者資料夾、AppData 等），不能整個處理；請改選裡面的項目。");
        }

        FileSystemInfo info = request.Kind == ScanItemKind.Dir ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
        {
            return Blocked(request.Kind == ScanItemKind.Dir && File.Exists(path) || request.Kind != ScanItemKind.Dir && Directory.Exists(path)
                ? "類型和掃描時不同（檔案變成資料夾，或相反），請重新掃描。"
                : "已經不存在（可能已經被移動或刪除）。");
        }

        if (request.Kind == ScanItemKind.Link || info.LinkTarget is not null)
        {
            return Blocked("這是連結（Symlink / Junction）。為了避免誤刪連結指向的內容，目前不處理連結。");
        }

        // 保護規則：項目本身（含從上層延伸的保護）
        var facts = new ItemFacts(
            info.Name,
            request.Kind,
            info is FileInfo file ? file.Length : long.MaxValue,
            0); // 時間未知時當作很舊，讓有時間條件的保護規則也生效（寧可多保護）
        var verdict = RuleEngine.EvaluatePath(environment.Rules, path, facts, environment.Probe, environment.Clock());
        if (verdict.IsProtected)
        {
            return Blocked($"受保護：{verdict.Classification!.RuleName}");
        }

        // 資料夾裡面有受保護的項目（包含掃描時沒有個別保留的小檔案）
        if (request.Kind == ScanItemKind.Dir && FindProtectedInside(path, environment, cancellationToken) is { } inside)
        {
            return Blocked($"裡面有受保護的項目：{inside}");
        }

        var actionCheck = request.Action switch
        {
            PlanAction.Recycle => !environment.Trash.IsSupported || environment.Trash.CannotRecycleReason(path) is not null
                ? Blocked(environment.Trash.CannotRecycleReason(path) ?? "這個作業系統目前還不支援資源回收筒，請改用隔離區。")
                : Ok(),
            PlanAction.Quarantine => environment.Quarantine.CannotQuarantineReason(path) is { } q ? Blocked(q) : Ok(),
            PlanAction.Move => CheckMove(path, request),
            PlanAction.Delete => Ok(),
            _ => Blocked("不支援的動作。"),
        };
        if (actionCheck.Item1 == PlanItemState.Blocked)
        {
            return actionCheck;
        }

        if (request.Kind == ScanItemKind.File && IsLocked(path))
        {
            return (PlanItemState.Warning, "檔案可能正在被其他程式使用，執行時可能會失敗。");
        }

        if (!environment.IsElevated && NeedsElevation(path, environment.Rules))
        {
            return (PlanItemState.Warning, "位於系統資料夾，可能需要系統管理員權限才能處理。");
        }

        return Ok();
    }

    /// <summary>
    /// 逐一檢查資料夾裡面的項目是否受保護。只有可能符合的保護規則存在時才會走訪（例如 <c>**/重要.txt</c> 這類規則）。
    /// 回傳第一個受保護的路徑；沒有時回傳 null。項目太多檢查不完時保守地回傳說明。
    /// </summary>
    public static string? FindProtectedInside(string directory, PlanEnvironment environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(environment);

        var rootSegments = RulePath.Split(directory);
        var candidates = environment.Rules.Protect.Where(r => r.Globs.Any(g => g.CouldMatchDescendantOf(rootSegments))).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var context = new MatchContext(environment.Probe, environment.Clock().Ticks);
        var stack = new Stack<(string Path, string[] Segments)>();
        stack.Push((directory, rootSegments));
        var inspected = 0;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, segments) = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(current).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (++inspected > environment.MaxEntriesToInspect)
                {
                    return "（項目太多，無法完整檢查是否含有受保護的項目）";
                }

                var childSegments = new string[segments.Length + 1];
                segments.CopyTo(childSegments, 0);
                childSegments[^1] = entry.Name;

                var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0 && entry.LinkTarget is not null;
                var kind = isLink ? ScanItemKind.Link : entry is DirectoryInfo ? ScanItemKind.Dir : ScanItemKind.File;
                var facts = new ItemFacts(entry.Name, kind, entry is FileInfo f ? f.Length : long.MaxValue, 0);
                foreach (var rule in candidates)
                {
                    if (rule.Matches(childSegments, facts, context, out _))
                    {
                        return entry.FullName;
                    }
                }

                if (kind == ScanItemKind.Dir)
                {
                    stack.Push((entry.FullName, childSegments));
                }
            }
        }

        return null;
    }

    private static (PlanItemState, string?) CheckCommand(PlanItemRequest request, PlanEnvironment environment)
    {
        var command = CommandCatalog.Find(request.CommandId);
        if (command is null)
        {
            return Blocked($"不在白名單中的命令：{request.CommandId ?? "（未指定）"}");
        }

        if (!command.AppliesTo(environment.Rules.Platform))
        {
            return Blocked($"{command.Id} 不適用於這個作業系統。");
        }

        if (command.GuidanceOnly)
        {
            return Blocked($"只提供操作指引，不會自動執行：{command.Description}");
        }

        if (command.RequiresElevation && !environment.IsElevated)
        {
            return Blocked($"{command.Display} 需要系統管理員權限；請以系統管理員身分重新啟動 WindowsTool。");
        }

        if (!environment.Commands.IsAvailable(command))
        {
            return Blocked($"找不到 {command.Executable}，請確認已經安裝並加入 PATH。");
        }

        return Ok();
    }

    private static (PlanItemState, string?) CheckMove(string path, PlanItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Destination))
        {
            return Blocked("請指定要搬到哪個資料夾。");
        }

        string destination;
        try
        {
            destination = PathUtil.NormalizeUserPath(request.Destination);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return Blocked($"目的地路徑不正確：{ex.Message}");
        }

        if (!Directory.Exists(destination))
        {
            return Blocked($"找不到目的地資料夾：{destination}");
        }

        if (PathUtil.IsSameOrUnder(destination, path))
        {
            return Blocked("目的地不能在要搬移的資料夾裡面。");
        }

        var target = Path.Combine(destination, Path.GetFileName(path));
        if (File.Exists(target) || Directory.Exists(target))
        {
            return Blocked($"目的地已經有同名的項目：{target}");
        }

        var sameVolume = string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(destination), PathUtil.PathComparison);
        if (!sameVolume)
        {
            if (request.Kind == ScanItemKind.Dir)
            {
                return Blocked("資料夾目前只能搬到同一個磁碟（跨磁碟搬移資料夾需要複製，容易中途失敗）。");
            }

            try
            {
                var free = new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
                if (free < request.Size)
                {
                    return Blocked("目的地的磁碟空間不足。");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                return Blocked($"無法確認目的地的磁碟空間：{ex.Message}");
            }
        }

        return Ok();
    }

    /// <summary>磁碟根目錄、使用者資料夾本身、AppData 等關鍵資料夾：不能整個處理（裡面的項目可以）。</summary>
    private static bool IsCritical(string path, RuleSet rules)
    {
        var segments = RulePath.Split(path);
        if (RulePath.IsRootOnly(segments))
        {
            return true;
        }

        foreach (var value in rules.Tokens.Values.Values)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var token = Normalize(value);
            if (string.Equals(token, path, PathUtil.PathComparison))
            {
                return true;
            }
        }

        // C:\Users（使用者資料夾的上一層）
        if (rules.Tokens.Values.TryGetValue("UserProfile", out var profile) && !string.IsNullOrEmpty(profile)
            && string.Equals(Path.GetDirectoryName(Normalize(profile)), path, PathUtil.PathComparison))
        {
            return true;
        }

        return false;
    }

    private static bool NeedsElevation(string path, RuleSet rules)
    {
        foreach (var name in new[] { "Windows", "ProgramFiles", "ProgramFiles(x86)", "ProgramData" })
        {
            if (rules.Tokens.Values.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)
                && PathUtil.IsSameOrUnder(path, Normalize(value)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>用「不共用」的方式短暫開啟檔案：其他程式正在使用時會失敗。</summary>
    private static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // 沒有讀取權限不代表被鎖定，執行時再看結果
        }
    }

    private static string? FindAcceptedAncestor(string path, HashSet<string> acceptedDirs)
    {
        for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (acceptedDirs.Contains(parent))
            {
                return parent;
            }
        }

        return null;
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? Path.TrimEndingDirectorySeparator(full) : full;
    }

    private static (PlanItemState, string?) Ok() => (PlanItemState.Ready, null);

    private static (PlanItemState, string?) Blocked(string reason) => (PlanItemState.Blocked, reason);
}
