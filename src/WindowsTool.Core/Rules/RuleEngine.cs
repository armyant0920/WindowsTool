using System.Text.Json.Serialization;
using WindowsTool.Core.Scanning;

namespace WindowsTool.Core.Rules;

/// <summary>某條規則和某個項目的關係（給 UI 顯示「命中的所有規則」與「最後生效的是哪一條」）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuleHitRole>))]
public enum RuleHitRole
{
    /// <summary>最後生效的規則。</summary>
    [JsonStringEnumMemberName("effective")]
    Effective,

    /// <summary>提醒規則：提高了風險下限。</summary>
    [JsonStringEnumMemberName("caution")]
    Caution,

    /// <summary>有命中，但被優先序更高的規則蓋過。</summary>
    [JsonStringEnumMemberName("overridden")]
    Overridden,
}

public sealed record RuleHit(Rule Rule, bool Inherited, RuleHitRole Role);

/// <summary>對任意路徑評估的結果（不需要掃描結果，例如驗證規則時檢查路徑是否受保護）。</summary>
public sealed record PathVerdict(Classification? Classification)
{
    public bool IsProtected => Classification?.Risk == RiskLevel.Protected;
}

/// <summary>
/// 規則引擎（PLAN 8.3 優先序）：
/// <list type="number">
/// <item>保護規則：命中就是 protected；保護會延伸到所有子孫（規則的 excludeGlob 可以開放特定子路徑）。</item>
/// <item>分類規則：使用者 → AI → 內建；同一層級取最具體的（條件數 + glob 字面長度），再取最新建立的。</item>
/// <item>提醒規則：把風險至少提高到規則指定的等級，並取消預設勾選；同樣延伸到所有子孫。</item>
/// </list>
/// </summary>
public static class RuleEngine
{
    /// <summary>評估整個掃描結果。</summary>
    public static RuleEvaluation Evaluate(
        ScanResult scan, RuleSet rules, IEntryProbe probe, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(probe);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var context = new MatchContext(probe, nowUtc.Ticks);
        var builder = new ClassificationFactory();
        var byId = new Classification?[scan.ItemCount + 1];
        var summary = new SummaryBuilder();

        var rootSegments = RulePath.Split(scan.Root.Name);
        var above = InheritFromAncestors(rules, rootSegments, context);

        var buffer = new string[rootSegments.Length + 64];
        rootSegments.CopyTo(buffer, 0);

        var stack = new Stack<Frame>();
        stack.Push(new Frame(scan.Root, rootSegments.Length, above, null));
        var processed = 0;

        while (stack.Count > 0)
        {
            if ((++processed & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var frame = stack.Pop();
            var dir = frame.Dir;
            if (frame.Depth + 1 > buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            if (dir != scan.Root)
            {
                buffer[frame.Depth - 1] = dir.Name;
            }

            context.Node = dir;
            var decision = Decide(rules, buffer.AsSpan(0, frame.Depth), Facts(dir), frame.Inherit, context, null);
            var classification = builder.Build(decision);
            byId[dir.Id] = classification;
            var chain = summary.Add(classification, dir, frame.Chain);

            var inherit = new Inherit(decision.Protect, decision.Caution);
            foreach (var file in dir.Files)
            {
                buffer[frame.Depth] = file.Name;
                context.Node = file;
                var fileDecision = Decide(rules, buffer.AsSpan(0, frame.Depth + 1), Facts(file), inherit, context, null);
                var fileClassification = builder.Build(fileDecision);
                byId[file.Id] = fileClassification;
                summary.Add(fileClassification, file, chain);
            }

            for (var i = dir.Dirs.Count - 1; i >= 0; i--)
            {
                stack.Push(new Frame(dir.Dirs[i], frame.Depth + 1, inherit, chain));
            }
        }

        // 由下往上標記「裡面有受保護的項目」：id 依 BFS 配發，子項目的 id 一定比父目錄大
        var containsProtected = new bool[byId.Length];
        for (var id = byId.Length - 1; id > 1; id--)
        {
            if (byId[id]?.Risk == RiskLevel.Protected || containsProtected[id])
            {
                var parent = scan.Find(id)?.Parent;
                if (parent is not null)
                {
                    containsProtected[parent.Id] = true;
                }
            }
        }

        stopwatch.Stop();
        return new RuleEvaluation(rules, byId, containsProtected, summary.Build(), stopwatch.Elapsed);
    }

    /// <summary>列出命中某個項目的所有規則，並標示哪一條最後生效。</summary>
    public static IReadOnlyList<RuleHit> Explain(ScanResult scan, RuleSet rules, IEntryProbe probe, DateTime nowUtc, int itemId)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(rules);

        var node = scan.Find(itemId) ?? throw new KeyNotFoundException($"找不到項目：{itemId}");

        var chain = new List<ScanNode>();
        for (ScanNode? current = node; current is not null; current = current.Parent)
        {
            chain.Insert(0, current);
        }

        var context = new MatchContext(probe, nowUtc.Ticks);
        var segments = new List<string>(RulePath.Split(scan.Root.Name));
        var inherit = InheritFromAncestors(rules, [.. segments], context);

        for (var i = 0; i < chain.Count; i++)
        {
            if (i > 0)
            {
                segments.Add(chain[i].Name);
            }

            context.Node = chain[i];
            var path = segments.ToArray();
            if (i < chain.Count - 1)
            {
                var decision = Decide(rules, path, Facts(chain[i]), inherit, context, null);
                inherit = new Inherit(decision.Protect, decision.Caution);
                continue;
            }

            var hits = new List<RuleHit>();
            Decide(rules, path, Facts(chain[i]), inherit, context, hits);
            return hits;
        }

        return [];
    }

    /// <summary>
    /// 評估任意路徑（不需要掃描結果）。上層資料夾只用路徑條件判斷是否受保護或需要注意。
    /// </summary>
    public static PathVerdict EvaluatePath(RuleSet rules, string path, ItemFacts facts, IEntryProbe probe, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(path);

        var segments = RulePath.Split(path);
        var context = new MatchContext(probe, nowUtc.Ticks);
        var inherit = InheritFromAncestors(rules, segments, context);
        var decision = Decide(rules, segments, facts, inherit, context, null);
        return new PathVerdict(new ClassificationFactory().Build(decision));
    }

    internal delegate void NodeVisitor(ScanNode node, ReadOnlySpan<string> path);

    /// <summary>依序走訪掃描結果的每個項目（深度優先），同時提供它的路徑段落。</summary>
    internal static void Walk(ScanResult scan, NodeVisitor visitor, CancellationToken cancellationToken = default)
    {
        var rootSegments = RulePath.Split(scan.Root.Name);
        var buffer = new string[rootSegments.Length + 64];
        rootSegments.CopyTo(buffer, 0);

        var stack = new Stack<(DirNode Dir, int Depth)>();
        stack.Push((scan.Root, rootSegments.Length));
        var processed = 0;
        while (stack.Count > 0)
        {
            if ((++processed & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var (dir, depth) = stack.Pop();
            if (depth + 1 > buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            if (dir != scan.Root)
            {
                buffer[depth - 1] = dir.Name;
            }

            visitor(dir, buffer.AsSpan(0, depth));
            foreach (var file in dir.Files)
            {
                buffer[depth] = file.Name;
                visitor(file, buffer.AsSpan(0, depth + 1));
            }

            for (var i = dir.Dirs.Count - 1; i >= 0; i--)
            {
                stack.Push((dir.Dirs[i], depth + 1));
            }
        }
    }

    /// <summary>計算掃描根目錄「上面」的祖先帶下來的保護 / 提醒（例如直接掃描 C:\Windows\System32）。</summary>
    private static Inherit InheritFromAncestors(RuleSet rules, string[] segments, MatchContext context)
    {
        var inherit = default(Inherit);
        context.Node = null;
        for (var length = 1; length < segments.Length; length++)
        {
            // 祖先的大小、時間未知：用「最寬鬆」的值，讓保護 / 提醒規則只看路徑條件（寧可多保護）
            var facts = new ItemFacts(segments[length - 1], ScanItemKind.Dir, long.MaxValue, 0);
            var decision = Decide(rules, segments.AsSpan(0, length), facts, inherit, context, null, protectOnly: true);
            inherit = new Inherit(decision.Protect, decision.Caution);
        }

        return inherit;
    }

    private static ItemFacts Facts(ScanNode node) => new(node.Name, node.Kind, node.Size, node.NewestWriteTicks);

    private static Decision Decide(
        RuleSet rules, ReadOnlySpan<string> path, in ItemFacts item, Inherit inherit, MatchContext context,
        List<RuleHit>? hits, bool protectOnly = false)
    {
        var decision = default(Decision);

        // 1. 保護
        CompiledRule? protect = null;
        var protectScore = -1;
        foreach (var rule in rules.Protect)
        {
            if (rule.Matches(path, item, context, out var score))
            {
                hits?.Add(new RuleHit(rule.Rule, false, RuleHitRole.Overridden));
                if (protect is null || CompiledRule.IsPreferred(rule, score, protect, protectScore))
                {
                    protect = rule;
                    protectScore = score;
                }
            }
        }

        if (protect is not null)
        {
            decision.Protect = protect;
        }
        else if (inherit.Protect is not null && !inherit.Protect.IsExcluded(path))
        {
            decision.Protect = inherit.Protect;
            decision.ProtectInherited = true;
            hits?.Add(new RuleHit(inherit.Protect.Rule, true, RuleHitRole.Overridden));
        }

        // 2. 提醒（取風險最高的，再取最具體的）
        CompiledRule? caution = null;
        var cautionScore = -1;
        foreach (var rule in rules.Caution)
        {
            if (rule.Matches(path, item, context, out var score))
            {
                hits?.Add(new RuleHit(rule.Rule, false, RuleHitRole.Overridden));
                if (caution is null
                    || rule.CautionRisk > caution.CautionRisk
                    || (rule.CautionRisk == caution.CautionRisk && CompiledRule.IsPreferred(rule, score, caution, cautionScore)))
                {
                    caution = rule;
                    cautionScore = score;
                }
            }
        }

        if (inherit.Caution is not null && !inherit.Caution.IsExcluded(path)
            && (caution is null || inherit.Caution.CautionRisk > caution.CautionRisk))
        {
            caution = inherit.Caution;
            decision.CautionInherited = true;
            hits?.Add(new RuleHit(inherit.Caution.Rule, true, RuleHitRole.Overridden));
        }

        decision.Caution = caution;

        if (protectOnly)
        {
            return decision;
        }

        // 3. 分類
        CompiledRule? classify = null;
        var classifyScore = -1;
        foreach (var rule in rules.Classify)
        {
            if (rule.Matches(path, item, context, out var score))
            {
                hits?.Add(new RuleHit(rule.Rule, false, RuleHitRole.Overridden));
                if (classify is null || CompiledRule.IsPreferred(rule, score, classify, classifyScore))
                {
                    classify = rule;
                    classifyScore = score;
                }
            }
        }

        decision.Classify = classify;

        if (hits is not null)
        {
            MarkRoles(hits, decision);
        }

        return decision;
    }

    private static void MarkRoles(List<RuleHit> hits, Decision decision)
    {
        var effective = decision.Protect ?? decision.Classify ?? decision.Caution;
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            if (effective is not null && ReferenceEquals(hit.Rule, effective.Rule))
            {
                hits[i] = hit with { Role = RuleHitRole.Effective };
            }
            else if (decision.Protect is null && decision.Caution is not null && ReferenceEquals(hit.Rule, decision.Caution.Rule))
            {
                hits[i] = hit with { Role = RuleHitRole.Caution };
            }
        }

        // 同一條規則可能同時以「直接命中」與「繼承」出現，只保留一筆（直接命中優先）
        var seen = new HashSet<Rule>(ReferenceEqualityComparer.Instance);
        hits.RemoveAll(h => !seen.Add(h.Rule));
    }

    private readonly record struct Inherit(CompiledRule? Protect, CompiledRule? Caution);

    private readonly record struct Frame(DirNode Dir, int Depth, Inherit Inherit, SummaryBuilder.Chain? Chain);

    private struct Decision
    {
        public CompiledRule? Protect;
        public bool ProtectInherited;
        public CompiledRule? Caution;
        public bool CautionInherited;
        public CompiledRule? Classify;
    }

    /// <summary>產生 <see cref="Classification"/>；同樣的組合共用同一個物件，節省記憶體。</summary>
    private sealed class ClassificationFactory
    {
        private readonly Dictionary<(CompiledRule Main, bool Inherited, CompiledRule? Caution), Classification> _cache = [];

        public Classification? Build(in Decision decision)
        {
            if (decision.Protect is { } protect)
            {
                return Get(protect, decision.ProtectInherited, null);
            }

            if (decision.Classify is { } classify)
            {
                return Get(classify, false, decision.Caution);
            }

            return decision.Caution is { } caution ? Get(caution, decision.CautionInherited, null) : null;
        }

        private Classification Get(CompiledRule main, bool inherited, CompiledRule? caution)
        {
            var key = (main, inherited, caution);
            if (_cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var rule = main.Rule;
            var result = rule.Result;
            var created = rule.Kind switch
            {
                RuleKind.Protect => new Classification(
                    string.IsNullOrWhiteSpace(result.Category) ? "受保護" : result.Category,
                    RiskLevel.Protected, SuggestedAction.Keep, null, result.Reason,
                    rule.Source, rule.Id, rule.Name, rule.Kind, AutoSelect: false, inherited, null, null),
                RuleKind.Caution => new Classification(
                    string.IsNullOrWhiteSpace(result.Category) ? "需要注意" : result.Category,
                    main.CautionRisk, SuggestedAction.Review, null, result.Reason,
                    rule.Source, rule.Id, rule.Name, rule.Kind, AutoSelect: false, inherited, null, null),
                _ => new Classification(
                    result.Category ?? rule.Name,
                    caution is null ? result.Risk : (RiskLevel)Math.Max((int)result.Risk, (int)caution.CautionRisk),
                    result.SuggestedAction, result.Method, result.Reason,
                    rule.Source, rule.Id, rule.Name, rule.Kind,
                    AutoSelect: result.AutoSelect && caution is null, inherited,
                    caution?.Rule.Id, caution?.Rule.Result.Reason),
            };

            _cache[key] = created;
            return created;
        }
    }

    /// <summary>
    /// 統計各類別的大小：只計算「上層沒有同類別」的項目，避免同一類別重複計算
    /// （例如 Temp 底下的子資料夾已經算在它的上層裡）。不同類別之間仍可能重疊。
    /// </summary>
    private sealed class SummaryBuilder
    {
        private readonly Dictionary<(string Category, RiskLevel Risk), CategoryTotals> _totals = [];

        public Chain? Add(Classification? classification, ScanNode node, Chain? chain)
        {
            if (classification is null)
            {
                return chain;
            }

            if (Chain.Contains(chain, classification.Category))
            {
                return chain;
            }

            var key = (classification.Category, classification.Risk);
            if (!_totals.TryGetValue(key, out var totals))
            {
                totals = new CategoryTotals();
                _totals[key] = totals;
            }

            totals.Count++;
            totals.Size += node.Size;
            if (classification.AutoSelect)
            {
                totals.AutoSelectCount++;
                totals.AutoSelectSize += node.Size;
            }

            return new Chain(classification.Category, chain);
        }

        public IReadOnlyList<CategorySummary> Build() =>
        [
            .. _totals
                .Select(kv => new CategorySummary(kv.Key.Category, kv.Key.Risk, kv.Value.Count, kv.Value.Size, kv.Value.AutoSelectCount, kv.Value.AutoSelectSize))
                .OrderByDescending(c => c.Size)
                .ThenBy(c => c.Category, StringComparer.Ordinal),
        ];

        public sealed class Chain(string category, Chain? parent)
        {
            public static bool Contains(Chain? chain, string category)
            {
                for (var current = chain; current is not null; current = current.Parent)
                {
                    if (current.Category == category)
                    {
                        return true;
                    }
                }

                return false;
            }

            public string Category { get; } = category;

            public Chain? Parent { get; } = parent;
        }

        private sealed class CategoryTotals
        {
            public int Count;
            public long Size;
            public int AutoSelectCount;
            public long AutoSelectSize;
        }
    }
}
