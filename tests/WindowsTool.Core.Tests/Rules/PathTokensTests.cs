using WindowsTool.Core.Rules;

namespace WindowsTool.Core.Tests.Rules;

public sealed class PathTokensTests
{
    private static PathTokens Create(Dictionary<string, string>? env = null) => new(
        new Dictionary<string, string>
        {
            ["UserProfile"] = @"C:\Users\me\",
            ["LocalAppData"] = @"C:\Users\me\AppData\Local",
            ["Windows"] = "",
        },
        name => env is not null && env.TryGetValue(name, out var v) ? v : null);

    [Fact]
    public void Expands_tokens_case_insensitively_and_trims_trailing_separators()
    {
        var tokens = Create();
        tokens.Expand("{UserProfile}/.nuget").Value.ShouldBe(@"C:\Users\me/.nuget");
        tokens.Expand("{localappdata}/Temp/**").Value.ShouldBe(@"C:\Users\me\AppData\Local/Temp/**");
    }

    [Fact]
    public void Unknown_token_is_an_error_and_missing_location_is_unavailable()
    {
        var tokens = Create();

        var unknown = tokens.Expand("{Nope}/x");
        unknown.IsOk.ShouldBeFalse();
        unknown.Unavailable.ShouldBeFalse();
        unknown.Error!.ShouldContain("{Nope}");

        var missing = tokens.Expand("{Windows}/Temp");
        missing.IsOk.ShouldBeFalse();
        missing.Unavailable.ShouldBeTrue();

        tokens.Expand("{UserProfile").IsOk.ShouldBeFalse();
        tokens.Expand("a}b").IsOk.ShouldBeFalse();
    }

    [Fact]
    public void Expands_environment_variables()
    {
        var tokens = Create(new Dictionary<string, string> { ["DATA"] = @"D:\data\", ["HOME"] = "/home/me" });

        tokens.Expand("%DATA%/x").Value.ShouldBe(@"D:\data/x");
        tokens.Expand("${HOME}/x").Value.ShouldBe("/home/me/x");
        tokens.Expand("$HOME/x").Value.ShouldBe("/home/me/x");
        tokens.Expand("%MISSING%/x").Unavailable.ShouldBeTrue();
        tokens.Expand("${MISSING}/x").Unavailable.ShouldBeTrue();
        tokens.Expand("${HOME").IsOk.ShouldBeFalse();
    }

    [Fact]
    public void Undefined_bare_dollar_names_stay_literal()
    {
        // $Recycle.Bin 是資料夾名稱，不是環境變數
        Create().Expand("?:/$Recycle.Bin").Value.ShouldBe("?:/$Recycle.Bin");
        Create().Expand("100%").Value.ShouldBe("100%");
    }

    [Fact]
    public void From_environment_resolves_the_current_user()
    {
        var tokens = PathTokens.FromEnvironment();
        tokens.Expand("{UserProfile}").Value.ShouldBe(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/'));
        tokens.Values.Keys.ShouldBe(PathTokens.Names, ignoreOrder: true);
    }
}
