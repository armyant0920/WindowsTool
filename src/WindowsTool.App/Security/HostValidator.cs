using Microsoft.AspNetCore.Http;

namespace WindowsTool.App.Security;

/// <summary>
/// 防 DNS rebinding：Host header 只接受 <c>127.0.0.1:&lt;port&gt;</c> 或 <c>localhost:&lt;port&gt;</c>。
/// </summary>
public static class HostValidator
{
    public static bool IsAllowed(HostString host, int expectedPort)
    {
        if (expectedPort <= 0 || !host.HasValue || host.Port != expectedPort)
        {
            return false;
        }

        return string.Equals(host.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
