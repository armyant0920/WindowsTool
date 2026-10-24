namespace WindowsTool.App.Hosting;

/// <summary>
/// 解析本工具自己的命令列參數，其餘參數原封不動交給 ASP.NET Core。
/// </summary>
/// <remarks>
/// 支援：<c>--port &lt;n&gt;</c>、<c>--no-browser</c>。
/// </remarks>
public sealed record CommandLineOptions(string[] RemainingArgs, IReadOnlyDictionary<string, string?> Overrides)
{
    public static CommandLineOptions Parse(string[] args)
    {
        var remaining = new List<string>();
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--no-browser", StringComparison.OrdinalIgnoreCase))
            {
                overrides[$"{AppOptions.SectionName}:{nameof(AppOptions.OpenBrowser)}"] = "false";
            }
            else if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                overrides[$"{AppOptions.SectionName}:{nameof(AppOptions.Port)}"] = args[++i];
            }
            else
            {
                remaining.Add(arg);
            }
        }

        return new CommandLineOptions([.. remaining], overrides);
    }
}
