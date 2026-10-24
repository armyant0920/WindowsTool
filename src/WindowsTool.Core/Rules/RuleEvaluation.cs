namespace WindowsTool.Core.Rules;

/// <summary>某個類別在掃描結果中的統計（只計算上層沒有同類別的項目）。</summary>
public sealed record CategorySummary(
    string Category, RiskLevel Risk, int Count, long Size, int AutoSelectCount, long AutoSelectSize);

/// <summary>規則套用在一次掃描結果上的結果，以項目 id 查詢。</summary>
public sealed class RuleEvaluation
{
    private readonly Classification?[] _byId;
    private readonly bool[] _containsProtected;

    internal RuleEvaluation(
        RuleSet rules, Classification?[] byId, bool[] containsProtected, IReadOnlyList<CategorySummary> categories, TimeSpan duration)
    {
        Rules = rules;
        _byId = byId;
        _containsProtected = containsProtected;
        Categories = categories;
        Duration = duration;
    }

    public RuleSet Rules { get; }

    public IReadOnlyList<CategorySummary> Categories { get; }

    public TimeSpan Duration { get; }

    public Classification? Get(int id) => id > 0 && id < _byId.Length ? _byId[id] : null;

    public bool IsProtected(int id) => Get(id)?.Risk == RiskLevel.Protected;

    /// <summary>資料夾裡面有受保護的項目（處理這個資料夾會連帶處理到受保護的項目）。</summary>
    public bool ContainsProtected(int id) => id > 0 && id < _containsProtected.Length && _containsProtected[id];

    /// <summary>保護路徑一律不可勾選；包含受保護項目的資料夾也不可勾選。</summary>
    public bool IsSelectable(int id) => !IsProtected(id) && !ContainsProtected(id);

    /// <summary>預設勾選：規則要求自動勾選，而且這個項目可以勾選。</summary>
    public bool IsAutoSelected(int id) => Get(id)?.AutoSelect == true && IsSelectable(id);
}
