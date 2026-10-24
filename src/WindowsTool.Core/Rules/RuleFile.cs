using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsTool.Core.Rules;

/// <summary>規則檔格式：內建規則（<c>rules/builtin/*.json</c>）與 <c>rules.user.json</c> 共用。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuleFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public IReadOnlyList<Rule> Rules { get; set; } = [];

    /// <summary>使用者停用的內建分類規則 id（內建保護規則不能停用）。</summary>
    public IReadOnlyList<string>? DisabledBuiltin { get; set; }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    RespectNullableAnnotations = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RuleFile))]
[JsonSerializable(typeof(Rule))]
internal sealed partial class RulesJsonContext : JsonSerializerContext;

public static class RuleJson
{
    /// <summary>寫檔用：中文不跳脫成 \uXXXX，方便直接編輯。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new(RulesJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = RulesJsonContext.Default,
    };

    public static RuleFile ParseFile(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, RulesJsonContext.Default.RuleFile)
            ?? throw new JsonException("規則檔是空的。");
    }

    public static string Serialize(RuleFile file) =>
        JsonSerializer.Serialize(file, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<RuleFile>)WriteOptions.GetTypeInfo(typeof(RuleFile)));

    public static string Serialize(Rule rule) =>
        JsonSerializer.Serialize(rule, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<Rule>)WriteOptions.GetTypeInfo(typeof(Rule)));
}

/// <summary>打包在程式內的內建規則（<c>rules/builtin/*.json</c>）。</summary>
public static class BuiltinRules
{
    private const string Prefix = "WindowsTool.Rules.Builtin.";

    private static readonly Lazy<IReadOnlyList<Rule>> Cached = new(Load);

    /// <summary>所有平台的內建規則（依平台過濾在 <see cref="RuleSet"/> 中進行）。</summary>
    public static IReadOnlyList<Rule> All => Cached.Value;

    private static List<Rule> Load()
    {
        var assembly = typeof(BuiltinRules).Assembly;
        var rules = new List<Rule>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var file = RuleJson.ParseFile(reader.ReadToEnd());
            rules.AddRange(file.Rules.Select(r => r with { Source = RuleSource.Builtin }));
        }

        return rules;
    }
}
