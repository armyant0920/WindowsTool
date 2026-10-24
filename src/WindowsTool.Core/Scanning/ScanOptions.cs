namespace WindowsTool.Core.Scanning;

public sealed class ScanOptions
{
    public const long DefaultKeepFileThreshold = 1024 * 1024; // 1 MB

    public static readonly IReadOnlyList<string> DefaultExcludeNames = ["System Volume Information"];

    /// <summary>預設包含隱藏檔：AppData 這類大量佔用空間的位置本身就是隱藏的。</summary>
    public bool IncludeHidden { get; init; } = true;

    public bool IncludeSystem { get; init; } = true;

    public IReadOnlyCollection<string> ExcludeDirectoryNames { get; init; } = DefaultExcludeNames;

    /// <summary>大於等於此大小的檔案一律個別保留；更小的只保留每個目錄前幾名。</summary>
    public long KeepFileThreshold { get; init; } = DefaultKeepFileThreshold;

    /// <summary>每個目錄額外保留的「小檔案」數量（依大小）。</summary>
    public int TopFilesPerDirectory { get; init; } = 3;

    /// <summary>0 = 自動。</summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>最多記錄幾個無法讀取的路徑（數量一律完整計算）。</summary>
    public int MaxDeniedPathsRecorded { get; init; } = 200;
}
