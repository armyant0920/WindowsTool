using WindowsTool.Core.Rules;
using static WindowsTool.Core.Tests.Rules.TestRules;

namespace WindowsTool.Core.Tests.Rules;

public sealed class BuiltinRulesTests
{
    [Fact]
    public void Builtin_rules_load_with_unique_ids()
    {
        var rules = BuiltinRules.All;
        rules.Count.ShouldBeGreaterThan(15);
        rules.Select(r => r.Id).ShouldBeUnique();
        rules.ShouldAllBe(r => r.Source == RuleSource.Builtin);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    [InlineData("macos")]
    public void Every_builtin_rule_passes_validation(string platform)
    {
        var current = RuleSet.Create(BuiltinRules.All, Tokens("C:"), platform, ignoreCase: true);
        foreach (var rule in BuiltinRules.All.Where(r => RuleSet.AppliesTo(r, platform)))
        {
            var result = RuleValidator.Validate(rule, new RuleValidationContext { Current = current, Probe = new FakeProbe() });
            result.IsValid.ShouldBeTrue($"{rule.Id}: {string.Join("; ", result.Issues.Where(i => i.IsError).Select(i => i.Message))}");
        }
    }

    [Fact]
    public void Windows_builtin_rules_classify_the_documented_paths()
    {
        var set = RuleSet.Create(BuiltinRules.All, Tokens("C:"), "windows", ignoreCase: true);
        var old = DateTime.UtcNow.AddDays(-400);
        var probe = new FakeProbe()
            .With("C:/src/web", "package.json")
            .With("C:/src/api", "Api.csproj")
            .With("C:/src/sln", "App.sln");

        string? Rule(string path, bool file = false, long size = 0) =>
            RuleEngine.EvaluatePath(set, path, file ? File(path, size, old) : Dir(path, size, old), probe, DateTime.UtcNow).Classification?.RuleId;

        // 保護（PLAN 13）
        Rule("C:/Windows/System32").ShouldBe("protect-windows");
        Rule("C:/Windows/WinSxS/amd64_x").ShouldBe("protect-winsxs");
        Rule("C:/Windows/Installer/abc.msi", file: true).ShouldBe("protect-windows-installer");
        Rule("C:/Program Files/App").ShouldBe("protect-program-files");
        Rule("C:/Program Files (x86)/App").ShouldBe("protect-program-files");
        Rule("C:/pagefile.sys", file: true).ShouldBe("protect-system-files");
        Rule("D:/pagefile.sys", file: true).ShouldBe("protect-system-files");
        Rule("D:/$Recycle.Bin/S-1-5").ShouldBe("protect-system-volume");
        Rule("C:/System Volume Information").ShouldBe("protect-system-volume");
        Rule("C:/Windows/MEMORY.DMP", file: true).ShouldBe("protect-windows"); // *.dmp 規則被保護蓋過

        // 分類（PLAN 14）
        Rule("C:/Users/me/AppData/Local/Temp/abc").ShouldBe("win-temp-user");
        Rule("C:/Users/me/AppData/Local/Temp").ShouldBeNull(); // Temp 本身不分類
        Rule("C:/Windows/Temp/abc").ShouldBe("win-temp-sys");
        Rule("C:/Windows/SoftwareDistribution/Download/x").ShouldBe("win-wu-download");
        Rule("C:/Windows/Minidump/1.dmp", file: true).ShouldBe("win-crashdumps");
        Rule("C:/Users/me/AppData/Local/CrashDumps/a.dmp", file: true).ShouldBe("win-crashdumps");
        Rule("D:/x/core.dmp", file: true).ShouldBe("win-crashdumps");
        Rule("C:/src/web/node_modules").ShouldBe("dev-node-modules");
        Rule("C:/src/api/bin").ShouldBe("dev-dotnet-binobj");
        Rule("C:/src/api/obj").ShouldBe("dev-dotnet-binobj");
        Rule("C:/src/web/bin").ShouldBeNull();
        Rule("C:/src/sln/.vs").ShouldBe("dev-vs-cache");
        Rule("C:/Users/me/.nuget/packages").ShouldBe("dev-nuget");
        Rule("C:/Users/me/AppData/Local/npm-cache").ShouldBe("dev-npm-cache");
        Rule("C:/Users/me/AppData/Local/pip/Cache").ShouldBe("dev-pip-cache");
        Rule("C:/Users/me/AppData/Local/Google/Chrome/User Data/Default/Cache").ShouldBe("browser-cache");
        Rule("C:/Users/me/AppData/Local/Microsoft/Edge/User Data/Profile 1/Code Cache").ShouldBe("browser-cache");
        Rule("C:/Users/me/AppData/Local/Docker/wsl/data/ext4.vhdx", file: true).ShouldBe("docker-vhdx");
        Rule("C:/ProgramData/Package Cache").ShouldBe("win-installer-cache");
        Rule("C:/ProgramData/Package Cache/{guid}/setup.exe", file: true).ShouldBe("win-installer-cache");
        Rule("C:/Users/me/Downloads/old.iso", file: true).ShouldBe("downloads-old");
        Rule("C:/app/logs/big.log", file: true, size: 20 * 1024 * 1024).ShouldBe("log-old");
        Rule("C:/app/logs/small.log", file: true, size: 1024).ShouldBeNull();

        // 提醒（風險至少 high）
        var docs = RuleEngine.EvaluatePath(set, "C:/Users/me/Documents/a.docx", File("a.docx"), probe, DateTime.UtcNow).Classification!;
        docs.RuleId.ShouldBe("caution-user-folders");
        docs.Risk.ShouldBe(RiskLevel.High);
        var git = RuleEngine.EvaluatePath(set, "C:/src/web/.git/objects", Dir("objects"), probe, DateTime.UtcNow).Classification!;
        git.RuleId.ShouldBe("caution-git");
    }

    [Fact]
    public void Temp_items_younger_than_two_days_are_not_classified()
    {
        var set = RuleSet.Create(BuiltinRules.All, Tokens("C:"), "windows", ignoreCase: true);
        RuleEngine.EvaluatePath(set, "C:/Users/me/AppData/Local/Temp/new", Dir("new", 0, DateTime.UtcNow), new FakeProbe(), DateTime.UtcNow)
            .Classification.ShouldBeNull();
    }

    [Fact]
    public void Linux_protects_system_folders()
    {
        var set = RuleSet.Create(BuiltinRules.All, Tokens("/"), "linux", ignoreCase: false);
        RuleEngine.EvaluatePath(set, "/usr/lib/x", Dir("x"), new FakeProbe(), DateTime.UtcNow).IsProtected.ShouldBeTrue();
        RuleEngine.EvaluatePath(set, "/home/me/x", Dir("x"), new FakeProbe(), DateTime.UtcNow).IsProtected.ShouldBeFalse();
    }

    [Fact]
    public void Builtin_rules_resolve_on_this_machine()
    {
        // 在真實環境的 token 上編譯：不應該有任何錯誤（缺少的位置只是警告）
        var set = RuleSet.Create(BuiltinRules.All, PathTokens.FromEnvironment());
        set.Issues.Values.SelectMany(i => i).ShouldAllBe(i => !i.IsError);
        set.ActiveCount.ShouldBeGreaterThan(0);
    }
}
