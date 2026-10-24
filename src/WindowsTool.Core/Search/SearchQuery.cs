namespace WindowsTool.Core.Search;

public enum SearchKind
{
    Any,
    File,
    Dir,
}

/// <summary>快速搜尋的條件（PLAN 9A.2），所有條件之間為 AND。</summary>
public sealed record SearchQuery
{
    public const int DefaultMaxResults = 10_000;
    public const int MaxAllowedResults = 100_000;

    public static readonly IReadOnlyList<string> DefaultExcludeNames = ["$Recycle.Bin", "System Volume Information"];

    /// <summary>搜尋位置（一或多個）。</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>檔名條件，見 <see cref="NameMatchMode"/>。</summary>
    public string? Pattern { get; init; }

    public NameMatchMode Mode { get; init; } = NameMatchMode.Auto;

    /// <summary>true = 比對完整路徑；false = 只比對名稱。</summary>
    public bool MatchFullPath { get; init; }

    public SearchKind Kind { get; init; } = SearchKind.Any;

    /// <summary>副檔名清單（含或不含點都可以）。指定時只會找到檔案。</summary>
    public IReadOnlyList<string>? Extensions { get; init; }

    /// <summary>指定大小條件時只會找到檔案。</summary>
    public long? MinSize { get; init; }

    public long? MaxSize { get; init; }

    public DateTime? ModifiedAfterUtc { get; init; }

    public DateTime? ModifiedBeforeUtc { get; init; }

    public bool IncludeHidden { get; init; } = true;

    /// <summary>預設不含系統檔（desktop.ini、Thumbs.db 等），和檔案總管的預設一致。</summary>
    public bool IncludeSystem { get; init; }

    /// <summary>根目錄內的項目深度為 1。null = 不限。</summary>
    public int? MaxDepth { get; init; }

    public IReadOnlyCollection<string> ExcludeDirectoryNames { get; init; } = DefaultExcludeNames;

    public int MaxResults { get; init; } = DefaultMaxResults;
}
