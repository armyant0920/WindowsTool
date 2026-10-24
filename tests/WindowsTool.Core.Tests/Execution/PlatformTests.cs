using System.Runtime.Versioning;
using WindowsTool.Core.Execution;
using WindowsTool.Core.Rules;
using WindowsTool.Core.Tests.Fixtures;

namespace WindowsTool.Core.Tests.Execution;

/// <summary>真正的資源回收筒與命令執行（只在 fixture 上操作，不執行任何真正的清理命令）。</summary>
public sealed class PlatformTests
{
    [Fact]
    [Trait("os", "windows")]
    public void Windows_recycle_bin_receives_files_and_folders()
    {
        if (OperatingSystem.IsWindows())
        {
            RecycleOnWindows();
        }
        else
        {
            Assert.Skip("Windows 專用");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RecycleOnWindows()
    {
        using var tree = new TempTree();
        var file = tree.File("recycle-me.txt", 10);
        tree.File("recycle-dir/inner.txt", 10);
        var dir = tree.PathOf("recycle-dir");
        var trash = new WindowsTrash();

        trash.IsSupported.ShouldBeTrue();
        trash.CannotRecycleReason(file).ShouldBeNull();
        trash.CannotRecycleReason(@"\\server\share\x").ShouldNotBeNull();

        var recycled = new List<string>();
        try
        {
            foreach (var path in new[] { file, dir })
            {
                var result = trash.Recycle(path);
                result.Success.ShouldBeTrue(result.Error);
                (File.Exists(path) || Directory.Exists(path)).ShouldBeFalse();
                result.RecycledPath.ShouldNotBeNull();
                result.RecycledPath.ShouldContain("$Recycle.Bin", Case.Insensitive);
                (File.Exists(result.RecycledPath) || Directory.Exists(result.RecycledPath)).ShouldBeTrue();
                recycled.Add(result.RecycledPath);
            }

            trash.Recycle(tree.PathOf("missing.txt")).Success.ShouldBeFalse();
        }
        finally
        {
            // 不要在使用者的資源回收筒留下測試檔案：刪掉 $R（內容）與 $I（原始路徑資訊）
            foreach (var path in recycled)
            {
                var info = Path.Combine(Path.GetDirectoryName(path)!, "$I" + Path.GetFileName(path)[2..]);
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }

                    File.Delete(info);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public void Unsupported_trash_explains_itself()
    {
        var trash = new UnsupportedTrash();
        trash.IsSupported.ShouldBeFalse();
        trash.Recycle("x").Error!.ShouldContain("隔離區");
    }

    [Fact]
    public async Task Command_runner_runs_fixed_arguments_without_a_shell()
    {
        var runner = new CommandRunner();
        CommandRunner.FindOnPath("dotnet").ShouldNotBeNull();
        CommandRunner.FindOnPath("definitely-not-a-real-tool-xyz").ShouldBeNull();

        var version = new CommandCatalog.CommandInfo("test", "dotnet --version", "all", "測試", "dotnet", ["--version"]);
        runner.IsAvailable(version).ShouldBeTrue();
        var result = await runner.RunAsync(version, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        result.Succeeded.ShouldBeTrue(result.Output);
        result.Output.ShouldContain("10.");

        var guidance = CommandCatalog.Find("wsl-compact")!;
        guidance.GuidanceOnly.ShouldBeTrue();
        runner.IsAvailable(guidance).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => runner.RunAsync(guidance, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        var missing = version with { Executable = "definitely-not-a-real-tool-xyz" };
        await Should.ThrowAsync<FileNotFoundException>(() => runner.RunAsync(missing, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Glob_can_tell_whether_a_folder_may_contain_matches()
    {
        bool Could(string glob, string dir) => GlobPattern.Parse(glob, true).CouldMatchDescendantOf(RulePath.Split(dir));

        Could("**/keep.txt", "C:/a/b").ShouldBeTrue();
        Could("C:/Windows", "C:/a").ShouldBeFalse();
        Could("C:/a/b/c", "C:/a").ShouldBeTrue();
        Could("C:/a/b", "C:/a/b").ShouldBeFalse(); // 只看子孫
        Could("C:/a/**", "C:/a/b").ShouldBeTrue();
        Could("?:/pagefile.sys", "C:/Users").ShouldBeFalse();
        Could("?:/pagefile.sys", "C:/").ShouldBeTrue();
        Could("C:/x/*/cache", "C:/x/p1").ShouldBeTrue();
    }

    [Fact]
    public void Data_directory_token_is_optional()
    {
        PathTokens.FromEnvironment().Expand("{DataDirectory}/x").Unavailable.ShouldBeTrue();
        PathTokens.FromEnvironment(@"D:\data").Expand("{DataDirectory}/x").Value.ShouldBe(@"D:\data/x");
        new PathTokens(new Dictionary<string, string>()).Expand("{Windows}").Unavailable.ShouldBeTrue();
    }
}
