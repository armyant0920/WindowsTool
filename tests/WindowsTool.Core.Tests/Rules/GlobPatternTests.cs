using WindowsTool.Core.Rules;

namespace WindowsTool.Core.Tests.Rules;

public sealed class GlobPatternTests
{
    [Theory]
    [InlineData("C:/Users/a/node_modules", "**/node_modules", true)]
    [InlineData("C:/node_modules", "**/node_modules", true)]
    [InlineData("C:/Users/a/node_modules/x", "**/node_modules", false)]
    [InlineData("C:/Users/a/node_modules_old", "**/node_modules", false)]
    [InlineData("C:/a/b/c.log", "**/*.log", true)]
    [InlineData("C:/a/b/c.log.txt", "**/*.log", false)]
    [InlineData("C:/a/Temp/x", "C:/a/Temp/**", true)]
    [InlineData("C:/a/Temp/x/y/z", "C:/a/Temp/**", true)]
    [InlineData("C:/a/Temp", "C:/a/Temp/**", false)] // 結尾 /** 不含資料夾本身
    [InlineData("C:/a/Temp", "C:/a/Temp", true)]
    [InlineData("C:/a/b/c/d", "C:/a/**/d", true)]
    [InlineData("C:/a/d", "C:/a/**/d", true)] // ** 可以是零段
    [InlineData("C:/a/b/c/e", "C:/a/**/d", false)]
    [InlineData("C:/x/User Data/Default/Cache", "C:/x/User Data/*/Cache", true)]
    [InlineData("C:/x/User Data/Profile 1/Cache", "C:/x/User Data/*/Cache", true)]
    [InlineData("C:/x/User Data/a/b/Cache", "C:/x/User Data/*/Cache", false)] // * 只比對一段
    [InlineData("D:/pagefile.sys", "?:/pagefile.sys", true)]
    [InlineData("D:/x/pagefile.sys", "?:/pagefile.sys", false)]
    [InlineData("C:/a/file1.txt", "C:/a/file?.txt", true)]
    [InlineData("C:/a/file12.txt", "C:/a/file?.txt", false)]
    [InlineData("C:/a/b/c", "**/a/**/c", true)]
    [InlineData("C:/a/x/y/b/z/c", "C:/**/b/**/c", true)]
    [InlineData("/usr/bin/ls", "/usr/**", true)]
    [InlineData("/usr", "/usr/**", false)]
    [InlineData("C:/aXbXc", "C:/*X*X*", true)]
    [InlineData("C:/abc", "C:/a*b*c*d", false)]
    public void Matches_like_gitignore(string path, string pattern, bool expected)
    {
        GlobPattern.Parse(pattern, ignoreCase: true).IsMatch(path).ShouldBe(expected);
    }

    [Fact]
    public void Case_sensitivity_is_configurable()
    {
        GlobPattern.Parse("**/Node_Modules", ignoreCase: true).IsMatch("C:/a/node_modules").ShouldBeTrue();
        GlobPattern.Parse("**/Node_Modules", ignoreCase: false).IsMatch("/a/node_modules").ShouldBeFalse();
        GlobPattern.Parse("**/*.LOG", ignoreCase: true).IsMatch("C:/a/x.log").ShouldBeTrue();
    }

    [Fact]
    public void Backslashes_are_separators()
    {
        GlobPattern.Parse(@"C:\Users\**\obj", ignoreCase: true).IsMatch(@"C:\Users\me\proj\obj").ShouldBeTrue();
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("a**b/c")]
    [InlineData("C:/a**")]
    [InlineData("")]
    public void Rejects_invalid_patterns(string pattern)
    {
        Should.Throw<ArgumentException>(() => GlobPattern.Parse(pattern, ignoreCase: true));
    }

    [Fact]
    public void Exposes_literal_prefix_and_significant_segments()
    {
        var glob = GlobPattern.Parse("C:/Users/me/*/x/**", ignoreCase: true);
        glob.LiteralPrefix.ShouldBe(["C:", "Users", "me"]);
        glob.GetSignificantSegments().ShouldBe(["C:", "Users", "me", "*", "x"]);
        glob.IsLiteral.ShouldBeFalse();
        GlobPattern.Parse("C:/a/b", true).IsLiteral.ShouldBeTrue();
        GlobPattern.Parse("**/*", true).GetSignificantSegments().ShouldBeEmpty();
        GlobPattern.Parse("C:/**/*.*", true).GetSignificantSegments().ShouldBe(["C:"]);
    }

    [Fact]
    public void Literal_length_counts_non_wildcard_characters()
    {
        GlobPattern.Parse("**/node_modules", true).LiteralLength.ShouldBe("node_modules".Length);
        GlobPattern.Parse("C:/a/*.log", true).LiteralLength.ShouldBe("C:a.log".Length);
    }

    [Theory]
    [InlineData(@"C:\Users\a", new[] { "C:", "Users", "a" })]
    [InlineData(@"C:\", new[] { "C:" })]
    [InlineData("/usr/bin/", new[] { "", "usr", "bin" })]
    [InlineData("/", new[] { "" })]
    [InlineData(@"\\server\share\x", new[] { "", "", "server", "share", "x" })]
    [InlineData("C:/a//b", new[] { "C:", "a", "b" })]
    public void Splits_paths(string path, string[] expected)
    {
        RulePath.Split(path).ShouldBe(expected);
    }

    [Fact]
    public void Joins_segments_back_to_a_path()
    {
        var sep = Path.DirectorySeparatorChar;
        RulePath.Join(["C:"]).ShouldBe("C:" + sep);
        RulePath.Join(["C:", "a", "b"]).ShouldBe($"C:{sep}a{sep}b");
        RulePath.Join([""]).ShouldBe(sep.ToString());
        RulePath.Join([]).ShouldBe("");
    }

    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("*.csproj", "App.csproj", true)]
    [InlineData("*.csproj", "App.csproj.user", false)]
    [InlineData("a*b?c", "aXXbYc", true)]
    [InlineData("a*b?c", "abc", false)]
    [InlineData("**", "x", true)]
    public void Wildcard_segments(string pattern, string text, bool expected)
    {
        new WildcardSegment(pattern, ignoreCase: true).IsMatch(text).ShouldBe(expected);
    }
}
