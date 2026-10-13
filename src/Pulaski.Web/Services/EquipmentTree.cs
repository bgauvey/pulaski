using Pulaski.Contracts.EquipmentModel;

namespace Pulaski.Web.Services;

/// <summary>One visible row of the equipment tree grid.</summary>
public sealed record TreeRow(EquipmentDto Item, int Depth, bool HasChildren, bool IsExpanded, bool IsMatch);

/// <summary>What a key press in the tree grid does: select a row, expand one, or collapse one.</summary>
public readonly record struct TreeMove(Guid? Select = null, Guid? Expand = null, Guid? Collapse = null);

/// <summary>
/// The equipment hierarchy shaped for the tree grid: children per node, the rows visible for a
/// set of expanded nodes and a filter, and keyboard movement between rows. Sibling order is the
/// order the API returned (by code); nothing here sorts.
/// </summary>
public sealed class EquipmentTree
{
    private readonly Dictionary<Guid, EquipmentDto> _byId = [];
    private readonly Dictionary<Guid, List<EquipmentDto>> _children = [];
    private readonly List<EquipmentDto> _roots = [];

    /// <param name="items">Equipment in tree order: every row after its parent.</param>
    public EquipmentTree(IEnumerable<EquipmentDto> items)
    {
        foreach (var item in items)
        {
            _byId[item.Id] = item;
            if (item.ParentId is { } parentId && _byId.ContainsKey(parentId))
            {
                if (!_children.TryGetValue(parentId, out var siblings))
                {
                    _children[parentId] = siblings = [];
                }

                siblings.Add(item);
            }
            else
            {
                // A row whose parent is outside the list (e.g. a site-scoped load) shows as a root.
                _roots.Add(item);
            }
        }
    }

    public IReadOnlyList<EquipmentDto> Roots => _roots;

    public int Count => _byId.Count;

    public EquipmentDto? Find(Guid id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<EquipmentDto> ChildrenOf(Guid id) =>
        _children.TryGetValue(id, out var children) ? children : [];

    /// <summary>Parent first, up to the root.</summary>
    public IEnumerable<EquipmentDto> AncestorsOf(Guid id)
    {
        var current = Find(id);
        while (current?.ParentId is { } parentId && Find(parentId) is { } parent)
        {
            yield return parent;
            current = parent;
        }
    }

    /// <summary>Every node with children; for "expand all".</summary>
    public IEnumerable<Guid> Branches() => _children.Keys;

    /// <summary>
    /// Rows to draw, depth first. With a filter, rows whose code or name contain it are shown with
    /// their ancestors, all expanded, and <see cref="TreeRow.IsMatch"/> marks the hits.
    /// </summary>
    public IReadOnlyList<TreeRow> VisibleRows(IReadOnlySet<Guid> expanded, string? filter = null)
    {
        var rows = new List<TreeRow>();
        var text = filter?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            foreach (var root in _roots)
            {
                AddExpanded(root, 0);
            }

            return rows;
        }

        var matches = _byId.Values.Where(e => Matches(e, text)).Select(e => e.Id).ToHashSet();
        var shown = new HashSet<Guid>(matches);
        foreach (var id in matches)
        {
            shown.UnionWith(AncestorsOf(id).Select(a => a.Id));
        }

        foreach (var root in _roots.Where(r => shown.Contains(r.Id)))
        {
            AddFiltered(root, 0);
        }

        return rows;

        void AddExpanded(EquipmentDto item, int depth)
        {
            var children = ChildrenOf(item.Id);
            var isExpanded = children.Count > 0 && expanded.Contains(item.Id);
            rows.Add(new TreeRow(item, depth, children.Count > 0, isExpanded, IsMatch: false));
            if (isExpanded)
            {
                foreach (var child in children)
                {
                    AddExpanded(child, depth + 1);
                }
            }
        }

        void AddFiltered(EquipmentDto item, int depth)
        {
            var children = ChildrenOf(item.Id).Where(c => shown.Contains(c.Id)).ToList();
            rows.Add(new TreeRow(item, depth, children.Count > 0, children.Count > 0, matches.Contains(item.Id)));
            foreach (var child in children)
            {
                AddFiltered(child, depth + 1);
            }
        }
    }

    /// <summary>
    /// Tree grid keys (WAI-ARIA treegrid pattern): Up/Down move, Home/End jump, Right expands or
    /// steps into the first child, Left collapses or steps out to the parent.
    /// </summary>
    public static TreeMove Navigate(IReadOnlyList<TreeRow> rows, Guid? selectedId, string key)
    {
        if (rows.Count == 0)
        {
            return default;
        }

        var index = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Item.Id == selectedId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return key is "ArrowDown" or "ArrowUp" or "Home" or "End" ? new TreeMove(Select: rows[0].Item.Id) : default;
        }

        var row = rows[index];
        return key switch
        {
            "ArrowDown" when index < rows.Count - 1 => new TreeMove(Select: rows[index + 1].Item.Id),
            "ArrowUp" when index > 0 => new TreeMove(Select: rows[index - 1].Item.Id),
            "Home" => new TreeMove(Select: rows[0].Item.Id),
            "End" => new TreeMove(Select: rows[^1].Item.Id),
            "ArrowRight" when row.HasChildren && !row.IsExpanded => new TreeMove(Expand: row.Item.Id),
            "ArrowRight" when row.IsExpanded && index < rows.Count - 1 => new TreeMove(Select: rows[index + 1].Item.Id),
            "ArrowLeft" when row.IsExpanded => new TreeMove(Collapse: row.Item.Id),
            "ArrowLeft" => ParentRow(rows, index) is { } parent ? new TreeMove(Select: parent) : default,
            _ => default,
        };
    }

    private static Guid? ParentRow(IReadOnlyList<TreeRow> rows, int index)
    {
        var depth = rows[index].Depth;
        for (var i = index - 1; i >= 0; i--)
        {
            if (rows[i].Depth < depth)
            {
                return rows[i].Item.Id;
            }
        }

        return null;
    }

    private static bool Matches(EquipmentDto item, string text) =>
        item.Code.Contains(text, StringComparison.OrdinalIgnoreCase)
        || item.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
}
