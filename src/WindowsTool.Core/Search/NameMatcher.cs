using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace WindowsTool.Core.Search;

public enum NameMatchMode
{
    /// <summary>
    /// 一般：含 <c>*</c> 或 <c>?</c> 時視為萬用字元（以 <c>;</c> 分隔多個樣式，符合任一即可）；
    /// 否則以空白分隔關鍵字，全部都要出現（不分大小寫）。
    /// </summary>
    Auto,

    /// <summary>正規表示式（不分大小寫；使用 NonBacktracking 引擎，不會因為樣式寫法而卡住）。</summary>
    Regex,
}

/// <summary>預先編譯好的名稱比對器，直接比對 span，不配置字串。</summary>
public sealed class NameMatcher
{
    private readonly string[] _terms = [];
    private readonly string[] _wildcards = [];
    private readonly Regex? _regex;

    private NameMatcher(string[] terms, string[] wildcards, Regex? regex)
    {
        _terms = terms;
        _wildcards = wildcards;
        _regex = regex;
    }

    /// <summary>沒有任何條件：全部符合。</summary>
    public bool MatchesEverything => _terms.Length == 0 && _wildcards.Length == 0 && _regex is null;

    /// <summary>建立比對器。正規表示式語法錯誤時拋出 <see cref="ArgumentException"/>，訊息可以直接顯示給使用者。</summary>
    public static NameMatcher Create(string? pattern, NameMatchMode mode)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new NameMatcher([], [], null);
        }

        if (mode == NameMatchMode.Regex)
        {
            try
            {
                var regex = new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                return new NameMatcher([], [], regex);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                throw new ArgumentException($"正規表示式有誤：{ex.Message}", ex);
            }
        }

        if (pattern.AsSpan().IndexOfAny('*', '?') >= 0)
        {
            var wildcards = pattern
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            return new NameMatcher([], wildcards, null);
        }

        var terms = pattern.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return new NameMatcher(terms, [], null);
    }

    public bool IsMatch(ReadOnlySpan<char> text)
    {
        if (_regex is not null)
        {
            return _regex.IsMatch(text);
        }

        if (_wildcards.Length > 0)
        {
            foreach (var wildcard in _wildcards)
            {
                if (FileSystemName.MatchesSimpleExpression(wildcard, text, ignoreCase: true))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var term in _terms)
        {
            if (!text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
