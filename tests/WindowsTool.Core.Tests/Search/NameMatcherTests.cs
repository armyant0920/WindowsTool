using WindowsTool.Core.Search;

namespace WindowsTool.Core.Tests.Search;

public sealed class NameMatcherTests
{
    [Theory]
    [InlineData("report", "Annual REPORT 2026.pdf", true)]
    [InlineData("report", "annual.pdf", false)]
    [InlineData("annual 2026", "Annual REPORT 2026.pdf", true)]   // 空白分隔 = 全部都要出現
    [InlineData("annual 2025", "Annual REPORT 2026.pdf", false)]
    [InlineData("  ", "anything", true)]
    public void Substring_terms_must_all_appear(string pattern, string name, bool expected)
    {
        NameMatcher.Create(pattern, NameMatchMode.Auto).IsMatch(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("*.pdf", "a.PDF", true)]
    [InlineData("*.pdf", "a.pdf.bak", false)]          // 萬用字元比對整個名稱
    [InlineData("report?.txt", "report1.txt", true)]
    [InlineData("report?.txt", "report12.txt", false)]
    [InlineData("*.txt; *.md", "readme.md", true)]      // 分號分隔 = 任一符合
    [InlineData("*.txt;*.md", "readme.doc", false)]
    public void Wildcards_match_the_whole_name(string pattern, string name, bool expected)
    {
        NameMatcher.Create(pattern, NameMatchMode.Auto).IsMatch(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"^data_\d{3}\.csv$", "DATA_001.csv", true)]
    [InlineData(@"^data_\d{3}\.csv$", "data_1234.csv", false)]
    [InlineData("(a+)+$", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", false)] // 典型的災難性回溯樣式：要能立刻回傳，不會卡住
    public void Regex_is_case_insensitive_and_safe(string pattern, string name, bool expected)
    {
        NameMatcher.Create(pattern, NameMatchMode.Regex).IsMatch(name).ShouldBe(expected);
    }

    [Fact]
    public void Invalid_regex_throws_a_friendly_error()
    {
        var ex = Should.Throw<ArgumentException>(() => NameMatcher.Create("[unclosed", NameMatchMode.Regex));
        ex.Message.ShouldStartWith("正規表示式有誤");
        ex.Message.ShouldNotContain("Parameter"); // 訊息直接顯示給使用者，不要附加參數名稱
    }

    [Fact]
    public void Empty_pattern_matches_everything()
    {
        var matcher = NameMatcher.Create(null, NameMatchMode.Auto);
        matcher.MatchesEverything.ShouldBeTrue();
        matcher.IsMatch("x").ShouldBeTrue();
    }
}
