using System.Reflection;
using WindowsTool.Core.Platform;

namespace WindowsTool.App.Endpoints;

public sealed record SystemInfoResponse(string Name, string Version, SystemInfo System);

public static class SystemEndpoints
{
    public static string AppVersion { get; } = GetVersion();

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/info", () =>
            TypedResults.Ok(new SystemInfoResponse("WindowsTool", AppVersion, SystemInfoProvider.Collect())));

        return app;
    }

    private static string GetVersion()
    {
        var version = typeof(SystemEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // 去掉 SourceLink 加上的 "+<commit hash>"
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }
}
