using WindowsTool.Core.FileSystem;

namespace WindowsTool.Core.Tests.FileSystem;

public sealed class PathUtilTests
{
    private static readonly string Temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    [Fact]
    public void NormalizeUserPath_cleans_user_input()
    {
        PathUtil.NormalizeUserPath($"  \"{Temp}{Path.DirectorySeparatorChar}\"  ").ShouldBe(Temp);

        var driveRoot = Path.GetPathRoot(Temp)!;
        PathUtil.NormalizeUserPath(driveRoot).ShouldBe(driveRoot);

        if (OperatingSystem.IsWindows())
        {
            PathUtil.NormalizeUserPath("%SystemRoot%").ShouldBe(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }
    }

    [Fact]
    public void IsSameOrUnder_respects_segment_boundaries()
    {
        var root = Path.Combine(Temp, "proj");

        PathUtil.IsSameOrUnder(root, root).ShouldBeTrue();
        PathUtil.IsSameOrUnder(Path.Combine(root, "src", "a.cs"), root).ShouldBeTrue();
        PathUtil.IsSameOrUnder(root + "2", root).ShouldBeFalse(); // proj2 不在 proj 底下
        PathUtil.IsSameOrUnder(Temp, root).ShouldBeFalse();

        var driveRoot = Path.GetPathRoot(Temp)!;
        PathUtil.IsSameOrUnder(Temp, driveRoot).ShouldBeTrue();
    }

    [Fact]
    public void RemoveNestedRoots_keeps_only_outermost_and_order()
    {
        var a = Path.Combine(Temp, "a");
        var b = Path.Combine(Temp, "b");
        var aChild = Path.Combine(a, "child");

        PathUtil.RemoveNestedRoots([aChild, b, a, b]).ShouldBe([b, a]);
        PathUtil.RemoveNestedRoots([a, Path.Combine(Temp, "a2")]).ShouldBe([a, Path.Combine(Temp, "a2")]);
    }

    [Fact]
    public void RemoveNestedRoots_treats_case_by_platform()
    {
        var a = Path.Combine(Temp, "Data");
        var lower = Path.Combine(Temp, "data");

        var result = PathUtil.RemoveNestedRoots([a, lower]);

        result.Count.ShouldBe(OperatingSystem.IsLinux() ? 2 : 1);
    }
}
