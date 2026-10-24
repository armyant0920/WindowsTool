using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace WindowsTool.App.Security;

public static class SessionToken
{
    public const string DefaultDevToken = "dev";

    /// <summary>
    /// 產生本次啟動的 token。正式環境一律是 256-bit 隨機值；
    /// Development 環境為了方便除錯，使用固定值（預設 <c>dev</c>）。
    /// </summary>
    public static string Create(bool isDevelopment, string? devToken)
    {
        if (isDevelopment)
        {
            return string.IsNullOrWhiteSpace(devToken) ? DefaultDevToken : devToken;
        }

        return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>
    /// 檢查 <c>Authorization: Bearer &lt;token&gt;</c>，以固定時間比較避免時序攻擊。
    /// </summary>
    public static bool IsAuthorized(string? authorizationHeader, string expectedToken)
    {
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(authorizationHeader)
            || !authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorizationHeader[scheme.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
