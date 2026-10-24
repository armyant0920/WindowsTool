using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WindowsTool.Core.Tests.Fixtures;

/// <summary>
/// 在暫存資料夾建立假的目錄樹，測試結束時自動刪除。
/// 所有破壞性 / 權限相關的測試都只在這裡產生的目錄上進行，不碰真實資料。
/// </summary>
public sealed class TempTree : IDisposable
{
    private readonly List<Action> _cleanups = [];

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "WindowsTool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>建立指定大小的檔案（用 SetLength，不實際寫入資料）。</summary>
    public string File(string relative, long size, DateTime? lastWriteUtc = null, FileAttributes? attributes = null)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.SetLength(size);
        }

        if (lastWriteUtc is { } time)
        {
            System.IO.File.SetLastWriteTimeUtc(path, time);
        }

        if (attributes is { } attrs)
        {
            System.IO.File.SetAttributes(path, attrs);
        }

        return path;
    }

    public string Dir(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 建立指向目錄的連結：Windows 用 Junction（不需要系統管理員權限），其他平台用 symlink。
    /// 無法建立時回傳 false，呼叫端應該略過測試。
    /// </summary>
    public bool TryCreateDirectoryLink(string linkRelative, string targetRelative)
    {
        var link = PathOf(linkRelative);
        var target = PathOf(targetRelative);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
                process.WaitForExit();
                return process.ExitCode == 0 && Directory.Exists(link);
            }

            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>讓目前使用者無法列出這個目錄的內容。無法設定時回傳 false。</summary>
    public bool TryDenyListing(string relative)
    {
        var path = PathOf(relative);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                DenyOnWindows(path);
            }
            else
            {
                DenyOnUnix(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }

        // 以 root 執行（例如某些容器）時權限不會生效
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private void DenyOnUnix(string path)
    {
        var original = System.IO.File.GetUnixFileMode(path);
        System.IO.File.SetUnixFileMode(path, UnixFileMode.None);
        _cleanups.Add(() => System.IO.File.SetUnixFileMode(path, original));
    }

    [SupportedOSPlatform("windows")]
    private void DenyOnWindows(string path)
    {
        var info = new DirectoryInfo(path);
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);

        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);

        _cleanups.Add(() =>
        {
            var current = info.GetAccessControl();
            current.RemoveAccessRule(rule);
            info.SetAccessControl(current);
        });
    }

    public void Dispose()
    {
        foreach (var cleanup in _cleanups)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 盡量還原
            }
        }

        try
        {
            ResetAttributes(Root);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 暫存資料夾刪不掉不影響測試結果
        }
    }

    private static void ResetAttributes(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        }))
        {
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}
