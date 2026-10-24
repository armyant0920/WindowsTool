namespace WindowsTool.Core.Rules;

/// <summary>
/// 規則用的路徑表示法：分隔符號統一成 <c>/</c>，再切成一段一段。
/// <list type="bullet">
/// <item><c>C:\Users\a</c> → <c>["C:", "Users", "a"]</c></item>
/// <item><c>/usr/bin</c> → <c>["", "usr", "bin"]</c>（開頭的空字串代表根目錄）</item>
/// <item><c>\\server\share\x</c> → <c>["", "", "server", "share", "x"]</c></item>
/// </list>
/// </summary>
public static class RulePath
{
    public static string[] Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');

        var leading = 0;
        while (leading < parts.Length && parts[leading].Length == 0 && leading < 2)
        {
            leading++;
        }

        var result = new List<string>(parts.Length);
        for (var i = 0; i < leading && i < parts.Length - 1; i++)
        {
            result.Add(""); // 根目錄（/）或 UNC（//）
        }

        for (var i = leading; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                result.Add(parts[i]); // 中間或結尾的空段落（a//b、a/）忽略
            }
        }

        // 單獨的 "/"：至少保留一個代表根目錄的段落
        if (result.Count == 0 && normalized.StartsWith('/'))
        {
            result.Add("");
        }

        return [.. result];
    }

    /// <summary>把段落組回作業系統的路徑（<c>["C:"]</c> → <c>C:\</c>）。</summary>
    public static string Join(ReadOnlySpan<string> segments)
    {
        if (segments.Length == 0)
        {
            return "";
        }

        var separator = Path.DirectorySeparatorChar;
        if (segments.Length == 1)
        {
            return segments[0].Length == 0 ? separator.ToString() : segments[0] + (segments[0].EndsWith(':') ? separator : "");
        }

        return string.Join(separator, segments.ToArray());
    }

    /// <summary>是否為絕對路徑（磁碟代號、<c>/</c> 或 UNC 開頭）。</summary>
    public static bool IsAbsolute(ReadOnlySpan<string> segments) =>
        segments.Length > 0 && (segments[0].Length == 0 || IsDriveSegment(segments[0]));

    /// <summary><c>C:</c>，或規則中代表任何磁碟的 <c>?:</c>、<c>*:</c>。</summary>
    public static bool IsDriveSegment(string segment) =>
        segment.Length == 2 && segment[1] == ':' && (char.IsAsciiLetter(segment[0]) || segment[0] is '?' or '*');

    /// <summary>段落是否只是檔案系統的根（<c>C:</c>、<c>/</c>、<c>\\server\share</c>）。</summary>
    public static bool IsRootOnly(ReadOnlySpan<string> segments)
    {
        if (segments.Length == 1)
        {
            return segments[0].Length == 0 || IsDriveSegment(segments[0]);
        }

        // UNC：["", "", server] 或 ["", "", server, share]
        return segments.Length is 3 or 4 && segments[0].Length == 0 && segments[1].Length == 0;
    }
}

/// <summary>
/// 單一段落內的萬用字元比對：<c>*</c> 任意字元（可為空）、<c>?</c> 一個字元，其餘逐字比對。
/// </summary>
public readonly struct WildcardSegment
{
    private readonly string _pattern;
    private readonly bool _ignoreCase;

    public WildcardSegment(string pattern, bool ignoreCase)
    {
        _pattern = pattern;
        _ignoreCase = ignoreCase;
        HasWildcard = pattern.AsSpan().IndexOfAny('*', '?') >= 0;
    }

    public string Pattern => _pattern;

    public bool HasWildcard { get; }

    /// <summary>只由 <c>*</c>、<c>?</c>、<c>.</c> 組成，幾乎符合所有名稱（例如 <c>*</c>、<c>*.*</c>）。</summary>
    public bool MatchesAlmostEverything => _pattern.Length > 0 && _pattern.AsSpan().IndexOfAnyExcept('*', '?', '.') < 0 && _pattern.Contains('*', StringComparison.Ordinal);

    public bool IsMatch(ReadOnlySpan<char> text)
    {
        var pattern = _pattern.AsSpan();
        if (!HasWildcard)
        {
            return pattern.Equals(text, _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        // 經典的貪婪比對：遇到 * 時記住位置，失敗時回到上一個 * 多吃一個字元
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private bool CharEquals(char a, char b) =>
        a == b || (_ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
}

/// <summary>
/// 路徑 glob（PLAN 8.2）：<c>*</c> / <c>?</c> 只比對單一段落；<c>**</c> 比對零或多個段落。
/// 結尾的 <c>/**</c> 代表「底下所有子孫」，不含該資料夾本身（和 .gitignore 相同）。
/// 輸入必須已經展開 token，並且是絕對路徑或以 <c>**</c> 開頭。
/// </summary>
public sealed class GlobPattern
{
    private readonly Segment[] _segments;
    private readonly bool _hasDoubleStar;

    private GlobPattern(string text, Segment[] segments, int literalLength, string[] literalPrefix)
    {
        Text = text;
        _segments = segments;
        _hasDoubleStar = segments.Any(s => s.IsDoubleStar);
        LiteralLength = literalLength;
        LiteralPrefix = literalPrefix;
    }

    /// <summary>正規化後的樣式（分隔符號為 <c>/</c>）。</summary>
    public string Text { get; }

    /// <summary>非萬用字元的字元數，用來計算規則的具體程度。</summary>
    public int LiteralLength { get; }

    /// <summary>開頭連續、不含萬用字元的段落（例如 <c>C:/Users/a/*/x</c> → <c>C:, Users, a</c>）。</summary>
    public IReadOnlyList<string> LiteralPrefix { get; }

    public static GlobPattern Parse(string pattern, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var parts = RulePath.Split(pattern);
        if (parts.Length == 0)
        {
            throw new ArgumentException("路徑樣式是空的。");
        }

        if (!RulePath.IsAbsolute(parts) && parts[0] != "**")
        {
            throw new ArgumentException($"路徑樣式必須是絕對路徑或以 ** 開頭：{pattern}");
        }

        var segments = new List<Segment>(parts.Length + 1);
        var literalLength = 0;
        var prefix = new List<string>();
        var inPrefix = true;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == "**")
            {
                inPrefix = false;
                if (segments.Count > 0 && segments[^1].IsDoubleStar)
                {
                    continue; // **/** 等於 **
                }

                if (i == parts.Length - 1)
                {
                    // 結尾的 ** 至少要有一段：等於 */**
                    segments.Add(new Segment(new WildcardSegment("*", ignoreCase)));
                }

                segments.Add(Segment.DoubleStar);
                continue;
            }

            if (part.Contains("**", StringComparison.Ordinal))
            {
                throw new ArgumentException($"** 必須單獨成為一段（例如 a/**/b），不能寫成 {part}：{pattern}");
            }

            var wildcard = new WildcardSegment(part, ignoreCase);
            if (wildcard.HasWildcard)
            {
                inPrefix = false;
            }
            else if (inPrefix)
            {
                prefix.Add(part);
            }

            foreach (var c in part)
            {
                if (c is not '*' and not '?')
                {
                    literalLength++;
                }
            }

            segments.Add(new Segment(wildcard));
        }

        return new GlobPattern(string.Join('/', parts), [.. segments], literalLength, [.. prefix]);
    }

    public bool IsMatch(ReadOnlySpan<string> path)
    {
        var pattern = _segments.AsSpan();
        if (path.Length == 0)
        {
            return false;
        }

        if (!_hasDoubleStar)
        {
            if (path.Length != pattern.Length)
            {
                return false;
            }

            // 先比最後一段（通常是名稱，最容易不符合）
            for (var i = pattern.Length - 1; i >= 0; i--)
            {
                if (!pattern[i].Wildcard.IsMatch(path[i]))
                {
                    return false;
                }
            }

            return true;
        }

        // 最後一段不是 ** 時，必須符合路徑的最後一段
        var last = pattern[^1];
        if (!last.IsDoubleStar && !last.Wildcard.IsMatch(path[^1]))
        {
            return false;
        }

        // 和單一段落的 * 相同的貪婪比對，只是單位換成段落
        int p = 0, s = 0, star = -1, mark = 0;
        while (s < path.Length)
        {
            if (p < pattern.Length && pattern[p].IsDoubleStar)
            {
                star = p++;
                mark = s;
            }
            else if (p < pattern.Length && pattern[p].Wildcard.IsMatch(path[s]))
            {
                p++;
                s++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                s = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p].IsDoubleStar)
        {
            p++;
        }

        return p == pattern.Length;
    }

    public bool IsMatch(string path) => IsMatch(RulePath.Split(path));

    /// <summary>
    /// 去掉結尾「幾乎符合一切」的段落（<c>**</c>、<c>*</c>、<c>*.*</c>）後剩下的段落；
    /// 用來判斷樣式是否過寬（例如 <c>**</c>、<c>**/*</c>、<c>C:/**</c>）。
    /// </summary>
    public IReadOnlyList<string> GetSignificantSegments()
    {
        var end = _segments.Length;
        while (end > 0 && (_segments[end - 1].IsDoubleStar || _segments[end - 1].Wildcard.MatchesAlmostEverything))
        {
            end--;
        }

        return _segments.Take(end).Select(s => s.IsDoubleStar ? "**" : s.Wildcard.Pattern).ToList();
    }

    internal int SegmentCount => _segments.Length;

    /// <summary>樣式本身是否沒有任何萬用字元（只會符合一個路徑）。</summary>
    public bool IsLiteral => LiteralPrefix.Count == _segments.Length;

    public override string ToString() => Text;

    private readonly struct Segment
    {
        public static readonly Segment DoubleStar = new(default, true);

        public Segment(WildcardSegment wildcard, bool isDoubleStar = false)
        {
            Wildcard = wildcard;
            IsDoubleStar = isDoubleStar;
        }

        public WildcardSegment Wildcard { get; }

        public bool IsDoubleStar { get; }
    }
}
