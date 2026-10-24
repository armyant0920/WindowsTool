using System.Text;

namespace WindowsTool.Core.Rules;

/// <summary>
/// 規則中的路徑變數（PLAN 8.2）：<c>{UserProfile}</c>、<c>{LocalAppData}</c> 等，
/// 以及 <c>%VAR%</c>、<c>${VAR}</c>、<c>$VAR</c> 形式的環境變數。
/// 規則不寫死絕對路徑，才能套用到不同的電腦與使用者。
/// </summary>
public sealed class PathTokens
{
    /// <summary>支援的 token 名稱（不分大小寫）。</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "UserProfile", "LocalAppData", "RoamingAppData", "Temp", "ProgramData", "Windows",
        "ProgramFiles", "ProgramFiles(x86)", "SystemDrive",
        "Documents", "Desktop", "Pictures", "Videos", "Music",
        "DataDirectory",
    ];

    private readonly Dictionary<string, string> _values;
    private readonly Func<string, string?> _environment;

    /// <param name="values">token 名稱 → 路徑；值為空字串代表這個平台沒有該位置。</param>
    /// <param name="environment">讀取環境變數（測試時可以替換）。</param>
    public PathTokens(IReadOnlyDictionary<string, string> values, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            _values[name] = TrimEnd(value);
        }

        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>目前使用者、目前作業系統的實際位置。</summary>
    /// <param name="dataDirectory">WindowsTool 的資料資料夾（<c>{DataDirectory}</c>），省略時這個 token 不生效。</param>
    public static PathTokens FromEnvironment(string? dataDirectory = null)
    {
        static string Folder(Environment.SpecialFolder folder) =>
            Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

        var windows = Folder(Environment.SpecialFolder.Windows);
        var systemDrive = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("SystemDrive") ?? (windows.Length >= 2 ? windows[..2] : "")
            : "";

        return new PathTokens(new Dictionary<string, string>
        {
            ["UserProfile"] = Folder(Environment.SpecialFolder.UserProfile),
            ["LocalAppData"] = Folder(Environment.SpecialFolder.LocalApplicationData),
            ["RoamingAppData"] = Folder(Environment.SpecialFolder.ApplicationData),
            ["Temp"] = Path.GetTempPath(),
            ["ProgramData"] = Folder(Environment.SpecialFolder.CommonApplicationData),
            ["Windows"] = windows,
            ["ProgramFiles"] = Folder(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Folder(Environment.SpecialFolder.ProgramFilesX86),
            ["SystemDrive"] = systemDrive,
            ["Documents"] = Folder(Environment.SpecialFolder.MyDocuments),
            ["Desktop"] = Folder(Environment.SpecialFolder.DesktopDirectory),
            ["Pictures"] = Folder(Environment.SpecialFolder.MyPictures),
            ["Videos"] = Folder(Environment.SpecialFolder.MyVideos),
            ["Music"] = Folder(Environment.SpecialFolder.MyMusic),
            ["DataDirectory"] = dataDirectory ?? "",
        });
    }

    /// <summary>所有 token 目前的值（給 UI 顯示）。</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>
    /// 展開 token 與環境變數。
    /// 回傳 <see cref="TokenExpansion.Ok"/>；token 名稱打錯時為 <see cref="TokenExpansion.Error"/>；
    /// token 在這個平台不存在、或環境變數沒有設定時為 <see cref="TokenExpansion.Unavailable"/>（規則照常載入，只是這個樣式不會符合任何項目）。
    /// </summary>
    public TokenExpansion Expand(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var builder = new StringBuilder(input.Length + 32);
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (c == '{')
            {
                var end = input.IndexOf('}', i + 1);
                if (end < 0)
                {
                    return TokenExpansion.Fail($"缺少右大括號：{input}");
                }

                var name = input[(i + 1)..end];
                if (!_values.TryGetValue(name, out var value))
                {
                    if (Names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        return TokenExpansion.NotAvailable($"{{{name}}} 沒有設定");
                    }

                    return TokenExpansion.Fail($"不認識的路徑變數 {{{name}}}；可以使用：{string.Join("、", Names.Select(n => "{" + n + "}"))}");
                }

                if (value.Length == 0)
                {
                    return TokenExpansion.NotAvailable($"{{{name}}} 在這個作業系統上不存在");
                }

                builder.Append(value);
                i = end + 1;
                continue;
            }

            if (c == '}')
            {
                return TokenExpansion.Fail($"多出右大括號：{input}");
            }

            if (c == '%')
            {
                var end = input.IndexOf('%', i + 1);
                if (end > i + 1)
                {
                    var name = input[(i + 1)..end];
                    var value = _environment(name);
                    if (string.IsNullOrEmpty(value))
                    {
                        return TokenExpansion.NotAvailable($"環境變數 %{name}% 沒有設定");
                    }

                    builder.Append(TrimEnd(value));
                    i = end + 1;
                    continue;
                }
            }

            if (c == '$' && i + 1 < input.Length)
            {
                if (input[i + 1] == '{')
                {
                    // ${VAR}
                    var end = input.IndexOf('}', i + 2);
                    if (end < 0)
                    {
                        return TokenExpansion.Fail($"缺少右大括號：{input}");
                    }

                    var name = input[(i + 2)..end];
                    var value = _environment(name);
                    if (string.IsNullOrEmpty(value))
                    {
                        return TokenExpansion.NotAvailable($"環境變數 ${{{name}}} 沒有設定");
                    }

                    builder.Append(TrimEnd(value));
                    i = end + 1;
                    continue;
                }

                // $VAR：只有環境變數存在時才展開，否則照字面（例如 $Recycle.Bin）
                var length = 0;
                while (i + 1 + length < input.Length && (char.IsAsciiLetterOrDigit(input[i + 1 + length]) || input[i + 1 + length] == '_'))
                {
                    length++;
                }

                if (length > 0)
                {
                    var name = input.Substring(i + 1, length);
                    var value = _environment(name);
                    if (!string.IsNullOrEmpty(value))
                    {
                        builder.Append(TrimEnd(value));
                        i += 1 + length;
                        continue;
                    }
                }
            }

            builder.Append(c);
            i++;
        }

        return TokenExpansion.Ok(builder.ToString());
    }

    private static string TrimEnd(string value)
    {
        var trimmed = value.TrimEnd('\\', '/');
        // 保留根目錄本身：「/」、「C:\」
        if (trimmed.Length == 0 && value.Length > 0)
        {
            return value[..1];
        }

        return trimmed;
    }
}

public readonly record struct TokenExpansion(string? Value, string? Error, bool Unavailable)
{
    public bool IsOk => Value is not null;

    public static TokenExpansion Ok(string value) => new(value, null, false);

    public static TokenExpansion Fail(string error) => new(null, error, false);

    public static TokenExpansion NotAvailable(string reason) => new(null, reason, true);
}
