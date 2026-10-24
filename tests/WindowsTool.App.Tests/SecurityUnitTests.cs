using Microsoft.AspNetCore.Http;
using WindowsTool.App.Hosting;
using WindowsTool.App.Security;

namespace WindowsTool.App.Tests;

public sealed class SecurityUnitTests
{
    [Theory]
    [InlineData("127.0.0.1:5179", 5179, true)]
    [InlineData("localhost:5179", 5179, true)]
    [InlineData("LOCALHOST:5179", 5179, true)]
    [InlineData("127.0.0.1:5180", 5179, false)]
    [InlineData("127.0.0.1", 5179, false)]
    [InlineData("127.0.0.2:5179", 5179, false)]
    [InlineData("[::1]:5179", 5179, false)]
    [InlineData("example.com:5179", 5179, false)]
    [InlineData("127.0.0.1:5179", 0, false)] // 埠號尚未確定時一律拒絕
    public void HostValidator_only_accepts_loopback_with_exact_port(string host, int port, bool expected)
    {
        HostValidator.IsAllowed(new HostString(host), port).ShouldBe(expected);
    }

    [Fact]
    public void HostValidator_rejects_missing_host()
    {
        HostValidator.IsAllowed(default, 5179).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Bearer secret", true)]
    [InlineData("bearer secret", true)]
    [InlineData("Bearer  secret ", true)]
    [InlineData("Bearer secret2", false)]
    [InlineData("Bearer secre", false)]
    [InlineData("Basic secret", false)]
    [InlineData("secret", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SessionToken_checks_bearer_header(string? header, bool expected)
    {
        SessionToken.IsAuthorized(header, "secret").ShouldBe(expected);
    }

    [Fact]
    public void SessionToken_is_unique_per_launch_in_production()
    {
        var a = SessionToken.Create(isDevelopment: false, devToken: "ignored");
        var b = SessionToken.Create(isDevelopment: false, devToken: "ignored");

        a.ShouldNotBe(b);
        a.ShouldNotBe("ignored");
    }

    [Fact]
    public void SessionToken_is_fixed_in_development()
    {
        SessionToken.Create(isDevelopment: true, devToken: null).ShouldBe("dev");
        SessionToken.Create(isDevelopment: true, devToken: "custom").ShouldBe("custom");
    }

    [Fact]
    public void CommandLine_maps_custom_switches_and_keeps_the_rest()
    {
        var parsed = CommandLineOptions.Parse(["--no-browser", "--port", "6000", "--urls", "x"]);

        parsed.Overrides["WindowsTool:OpenBrowser"].ShouldBe("false");
        parsed.Overrides["WindowsTool:Port"].ShouldBe("6000");
        parsed.RemainingArgs.ShouldBe(["--urls", "x"]);
    }
}
