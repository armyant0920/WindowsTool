using WindowsTool.Core.Rules;
using WindowsTool.Core.Scanning;

namespace WindowsTool.App.Scans;

/// <summary>
/// 一次掃描結果中「使用者勾選了哪些項目」，以及哪些已經處理過（移到資源回收筒、隔離區…）。
/// 存在伺服器端：切換資料夾、篩選、重新整理頁面都不會遺失；建立執行計畫時直接使用。
/// 掃描完成時不會自動勾選任何項目，由使用者自己勾選（或按「勾選建議清理的項目」）。
/// </summary>
public sealed class ScanSelection
{
    private readonly Lock _lock = new();
    private readonly HashSet<int> _selected = [];
    private readonly HashSet<int> _done = [];

    public bool IsSelected(int id)
    {
        lock (_lock)
        {
            return _selected.Contains(id);
        }
    }

    /// <summary>項目本身或它的上層已經處理過。</summary>
    public bool IsDone(ScanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_lock)
        {
            if (_done.Count == 0)
            {
                return false;
            }

            for (ScanNode? current = node; current is not null; current = current.Parent)
            {
                if (_done.Contains(current.Id))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>上層資料夾已經勾選（這個項目已經包含在上層裡）。</summary>
    public bool HasSelectedAncestor(ScanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (_lock)
        {
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                if (_selected.Contains(parent.Id))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Add(IEnumerable<int> ids)
    {
        lock (_lock)
        {
            _selected.UnionWith(ids);
        }
    }

    public void Remove(IEnumerable<int> ids)
    {
        lock (_lock)
        {
            _selected.ExceptWith(ids);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _selected.Clear();
        }
    }

    public void MarkDone(IEnumerable<int> ids)
    {
        lock (_lock)
        {
            foreach (var id in ids)
            {
                _done.Add(id);
                _selected.Remove(id);
            }
        }
    }

    /// <summary>目前有效的勾選：可以勾選（不受保護）、而且還沒處理過的。</summary>
    public IReadOnlyList<ScanNode> Effective(ScanResult result, RuleEvaluation? evaluation)
    {
        ArgumentNullException.ThrowIfNull(result);
        int[] ids;
        lock (_lock)
        {
            ids = [.. _selected];
        }

        var nodes = new List<ScanNode>(ids.Length);
        foreach (var id in ids)
        {
            if (result.Find(id) is { } node && (evaluation?.IsSelectable(id) ?? true) && !IsDone(node))
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    /// <summary>數量與大小，只算最上層：資料夾和它裡面的項目都勾選時，只算資料夾（裡面的已經包含在內）。</summary>
    public static (int Count, long Size) Summarize(IReadOnlyCollection<ScanNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var ids = nodes.Select(n => n.Id).ToHashSet();
        long size = 0;
        var count = 0;
        foreach (var node in nodes)
        {
            var nested = false;
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                if (ids.Contains(parent.Id))
                {
                    nested = true;
                    break;
                }
            }

            if (!nested)
            {
                size += node.Size;
                count++;
            }
        }

        return (count, size);
    }

    /// <summary>規則建議清理（autoSelect）的項目，不含已處理的。</summary>
    public IReadOnlyList<ScanNode> Suggested(ScanResult result, RuleEvaluation? evaluation)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (evaluation is null)
        {
            return [];
        }

        var nodes = new List<ScanNode>();
        foreach (var node in result.Items)
        {
            if (evaluation.IsAutoSelected(node.Id) && !IsDone(node))
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }
}
