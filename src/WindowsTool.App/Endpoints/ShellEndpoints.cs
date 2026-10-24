using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using WindowsTool.App.Common;
using WindowsTool.App.Scans;
using WindowsTool.App.Search;
using WindowsTool.App.Shell;
using WindowsTool.Core.Scanning;
using WindowsTool.Core.Search;

namespace WindowsTool.App.Endpoints;

/// <summary>
/// 指定要操作的項目。只接受「搜尋結果的第幾筆」或「掃描結果的項目 id」，
/// 不接受任意路徑：只有使用者看得到的結果才能被開啟（PLAN 9A.4）。
/// </summary>
public sealed record ShellTargetRequest(string? SearchId = null, int? HitIndex = null, string? ScanId = null, int? ItemId = null);

public static class ShellEndpoints
{
    public static IEndpointRouteBuilder MapShellEndpoints(this IEndpointRouteBuilder app)
    {
        var shell = app.MapGroup("/api/shell");
        shell.MapPost("/reveal", Reveal);
        shell.MapPost("/open", Open);
        return app;
    }

    private static Results<NoContent, BadRequest<ErrorResponse>, NotFound<ErrorResponse>> Reveal(
        ShellTargetRequest request, SearchManager searches, ScanManager scans, IShellLauncher launcher) =>
        Run(request, searches, scans, (path, _) =>
        {
            launcher.Reveal(path);
            return null;
        });

    private static Results<NoContent, BadRequest<ErrorResponse>, NotFound<ErrorResponse>> Open(
        ShellTargetRequest request, SearchManager searches, ScanManager scans, IShellLauncher launcher) =>
        Run(request, searches, scans, (path, isDirectory) =>
        {
            if (!isDirectory && ShellLauncher.IsExecutable(path))
            {
                return "基於安全考量，不能直接開啟程式或指令碼，請改用「在檔案總管中顯示」。";
            }

            launcher.Open(path);
            return null;
        });

    /// <summary>解析目標、確認仍然存在，再執行動作（回傳錯誤訊息或 null）。</summary>
    private static Results<NoContent, BadRequest<ErrorResponse>, NotFound<ErrorResponse>> Run(
        ShellTargetRequest request,
        SearchManager searches,
        ScanManager scans,
        Func<string, bool, string?> action)
    {
        string? path;
        bool isDirectory;

        if (request.SearchId is not null && request.HitIndex is { } index)
        {
            var hit = searches.Get(request.SearchId)?.Results.Get(index);
            if (hit is null)
            {
                return TypedResults.NotFound(new ErrorResponse("找不到這筆搜尋結果，可能已經被清除。"));
            }

            path = hit.Path;
            isDirectory = hit.Kind == SearchHitKind.Dir;
        }
        else if (request.ScanId is not null && request.ItemId is { } itemId)
        {
            var node = scans.Get(request.ScanId) is { Status: ScanStatus.Completed, Result: { } result }
                ? result.Find(itemId)
                : null;
            if (node is null)
            {
                return TypedResults.NotFound(new ErrorResponse("找不到這個掃描項目，可能已經被清除。"));
            }

            path = node.GetFullPath();
            isDirectory = node is DirNode;
        }
        else
        {
            return TypedResults.BadRequest(new ErrorResponse("請指定 searchId + hitIndex，或 scanId + itemId。"));
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return TypedResults.NotFound(new ErrorResponse("這個項目已經不存在（可能已被移動或刪除）。"));
        }

        try
        {
            var error = action(path, isDirectory);
            return error is null ? TypedResults.NoContent() : TypedResults.BadRequest(new ErrorResponse(error));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return TypedResults.BadRequest(new ErrorResponse($"無法開啟：{ex.Message}"));
        }
    }
}
