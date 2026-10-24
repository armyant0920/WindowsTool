namespace WindowsTool.App.Hosting;

/// <summary>
/// 啟動設定，對應設定區段 <c>WindowsTool</c>（環境變數 <c>WindowsTool__Port</c> 等）。
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "WindowsTool";

    /// <summary>監聽的埠號。0 代表由系統配置隨機埠（正式環境預設）。</summary>
    public int Port { get; set; }

    /// <summary>啟動後是否自動開啟預設瀏覽器。</summary>
    public bool OpenBrowser { get; set; } = true;

    /// <summary>固定的 session token，只在 Development 環境有效，正式環境一律忽略。</summary>
    public string? DevToken { get; set; }
}
