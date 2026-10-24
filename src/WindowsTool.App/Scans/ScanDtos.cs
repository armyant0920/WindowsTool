using WindowsTool.App.Common;
using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;

namespace WindowsTool.App.Scans;

public sealed record StartScanRequest(
    string? Root,
    bool? IncludeHidden = null,
    bool? IncludeSystem = null,
    IReadOnlyList<string>? ExcludeNames = null,
    long? KeepFileThreshold = null);

public sealed record StartScanResponse(string ScanId);

public sealed record ScanProgressDto(long Dirs, long Files, long Bytes, long Denied, string? CurrentPath);

public sealed record ScanErrorDto(string Path, string Reason);

public sealed record ScanSummaryDto(
    int RootId,
    long TotalSize,
    long TotalFiles,
    int TotalDirs,
    int ItemCount,
    long ErrorCount,
    IReadOnlyList<ScanErrorDto> Errors);

public sealed record ScanStatusDto(
    string ScanId,
    string Root,
    string Status,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    long ElapsedMs,
    long? ExpectedBytes,
    ScanProgressDto Progress,
    ScanSummaryDto? Summary,
    string? Error);

public sealed record ScanItemDto(
    int Id,
    int? ParentId,
    string Name,
    string Path,
    string Kind,
    long Size,
    long FileCount,
    int DirCount,
    DateTime LastWriteUtc,
    DateTime NewestWriteUtc,
    IReadOnlyList<string> Flags,
    int DeniedCount,
    bool HasChildren,
    Classification? Classification,
    bool Selectable,
    bool AutoSelected,
    bool ContainsProtected);

public sealed record OtherFilesDto(int Count, long Size);

public sealed record ItemPageDto(
    ScanItemDto? Parent,
    IReadOnlyList<ScanItemDto> Items,
    int Total,
    OtherFilesDto? OtherFiles);

public sealed record ExtStatDto(string Ext, int Count, long Size);

public sealed record AncestorDto(int Id, string Name);

public sealed record RuleHitDto(string Id, string Name, RuleKind Kind, RuleSource Source, bool Inherited, RuleHitRole Role);

public sealed record ItemDetailDto(
    ScanItemDto Item,
    IReadOnlyList<AncestorDto> Ancestors,
    IReadOnlyList<ExtStatDto> ExtStats,
    OtherFilesDto? OtherFiles,
    IReadOnlyList<RuleHitDto> Rules);

/// <summary>各類別的統計（只計算上層沒有同類別的項目；不同類別之間大小可能重疊）。</summary>
public sealed record CategoriesDto(long RulesVersion, long EvaluationMs, IReadOnlyList<CategorySummary> Categories);

public static class ScanDtoMapper
{
    public static string ToApi(this ScanStatus status) => status switch
    {
        ScanStatus.Running => "running",
        ScanStatus.Completed => "completed",
        ScanStatus.Cancelled => "cancelled",
        _ => "failed",
    };

    public static ScanStatusDto ToDto(this ScanSession session)
    {
        // 先讀狀態（volatile），再讀結果
        var status = session.Status;
        var progress = session.Progress.Snapshot();
        var result = status == ScanStatus.Completed ? session.Result : null;

        ScanSummaryDto? summary = null;
        if (result is not null)
        {
            summary = new ScanSummaryDto(
                result.Root.Id,
                result.Root.Size,
                result.Root.TotalFileCount,
                result.Root.TotalDirCount,
                result.ItemCount,
                result.ErrorCount,
                [.. result.Errors.Select(e => new ScanErrorDto(e.Path, e.Reason))]);
        }

        return new ScanStatusDto(
            session.Id,
            session.Root,
            status.ToApi(),
            session.StartedUtc,
            status == ScanStatus.Running ? null : session.FinishedUtc,
            (long)session.Elapsed.TotalMilliseconds,
            session.ExpectedBytes,
            new ScanProgressDto(progress.Dirs, progress.Files, progress.Bytes, progress.Denied, progress.CurrentPath),
            summary,
            session.Error);
    }

    public static ScanItemDto ToDto(this ScanNode node, RuleEvaluation? evaluation)
    {
        var dir = node as DirNode;
        return new ScanItemDto(
            node.Id,
            node.Parent?.Id,
            node.Name,
            node.GetFullPath(),
            node.Kind switch
            {
                ScanItemKind.Dir => "dir",
                ScanItemKind.Link => "link",
                _ => "file",
            },
            node.Size,
            dir?.TotalFileCount ?? 0,
            dir?.TotalDirCount ?? 0,
            node.LastWriteUtc,
            node.NewestWriteUtc,
            FlagNames(node.Flags),
            dir?.DeniedCount ?? 0,
            dir?.HasChildren ?? false,
            evaluation?.Get(node.Id),
            evaluation?.IsSelectable(node.Id) ?? true,
            evaluation?.IsAutoSelected(node.Id) ?? false,
            evaluation?.ContainsProtected(node.Id) ?? false);
    }

    public static OtherFilesDto? OtherFiles(this DirNode dir) =>
        dir.OtherFileCount > 0 ? new OtherFilesDto(dir.OtherFileCount, dir.OtherFileSize) : null;

    private static string[] FlagNames(ScanItemFlags flags)
    {
        if (flags == ScanItemFlags.None)
        {
            return [];
        }

        var names = new List<string>(2);
        if ((flags & ScanItemFlags.ReparsePoint) != 0)
        {
            names.Add("reparsePoint");
        }

        if ((flags & ScanItemFlags.AccessDenied) != 0)
        {
            names.Add("accessDenied");
        }

        if ((flags & ScanItemFlags.CloudPlaceholder) != 0)
        {
            names.Add("cloudPlaceholder");
        }

        if ((flags & ScanItemFlags.Hidden) != 0)
        {
            names.Add("hidden");
        }

        if ((flags & ScanItemFlags.System) != 0)
        {
            names.Add("system");
        }

        return [.. names];
    }
}
