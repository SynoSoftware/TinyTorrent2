using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView;

/// <summary>
/// Section 5's identity rule: two items are one row when their keys are equal. Without a key
/// selector an item is its own key and keys compare by reference; with one the key is the
/// selector's, compared by the schema's comparer. The selector is installed once, so the instance
/// is created empty and filled at schema capture.
/// </summary>
internal sealed class ItemIdentity : IEqualityComparer<object>
{
    private Func<object, object>? _key;

    internal IEqualityComparer<object> KeyComparer { get; private set; } =
        ReferenceEqualityComparer.Instance;

    internal void Install(Func<object, object>? key, IEqualityComparer<object> comparer)
    {
        _key = key;
        KeyComparer = key is null ? ReferenceEqualityComparer.Instance : comparer;
    }

    /// <remarks>
    /// Null only from a selector that breaks section 5. Where rows enter, the table refuses such a
    /// row: <see cref="Body.Slots.Number"/> and <see cref="Hierarchy.Capture"/>.
    /// </remarks>
    internal object KeyOf(object item) => _key is { } key ? key(item) : item;

    public new bool Equals(object? x, object? y) =>
        ReferenceEquals(x, y)
        || (x is not null && y is not null && KeyComparer.Equals(KeyOf(x), KeyOf(y)));

    public int GetHashCode(object obj) => KeyComparer.GetHashCode(KeyOf(obj));
}

/// <summary>
/// The table's authoritative selection state as pure logic: no input, no visual, no XAML tree.
/// Visual order belongs to the view, so the model stores an unordered identity set and every
/// operation that needs order takes the current ordered view as an argument.
/// </summary>
/// <remarks>
/// Table owns the ordered published snapshot and change notifications.
/// </remarks>
internal sealed class SelectionState
{
    private readonly ItemIdentity _identity;
    private readonly Func<object, int> _indexOf;
    private HashSet<object> _selected;

    internal SelectionState(ItemIdentity identity, Func<object, int> indexOf)
    {
        _identity = identity;
        _indexOf = indexOf;
        _selected = new HashSet<object>(identity);
    }

    internal ListViewSelectionMode Mode { get; set; } = ListViewSelectionMode.Extended;

    /// <summary>Section 5's interaction predicate. Null means every item is interactive.</summary>
    internal Func<object, bool>? CanInteract { get; set; }

    internal object? Current { get; private set; }

    /// <summary>The row a range selection extends from.</summary>
    internal object? Anchor { get; private set; }

    /// <summary>Logical row focus. Not the same thing as physical keyboard focus.</summary>
    internal object? Focus { get; private set; }

    internal sealed record Checkpoint(
        object[] Items,
        object? Current,
        object? Anchor,
        object? Focus
    );

    internal Checkpoint Capture() => new(_selected.ToArray(), Current, Anchor, Focus);

    internal void Restore(Checkpoint checkpoint, IReadOnlyList<object> view)
    {
        Apply(
            new List<object>(checkpoint.Items),
            checkpoint.Current,
            checkpoint.Anchor,
            checkpoint.Focus
        );
        Reconcile(view);
    }

    internal bool AllowsMultiple =>
        Mode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended;

    internal bool IsSelected(object item) => _selected.Contains(item);

    internal IEnumerable<object> Items => _selected;

    internal bool IsInteractive(object item) => CanInteract is null || CanInteract(item);

    internal bool IsSame(object? a, object? b) =>
        a is null || b is null ? a is null && b is null : _identity.Equals(a, b);

    /// <summary>
    /// Re-bucket the selected set after schema capture supplies the key selector. Reference hashes
    /// taken before capture are stale once identity becomes key-based.
    /// </summary>
    internal void RehashIdentity() => _selected = new HashSet<object>(_selected, _identity);

    // ------------------------------------------------------------------ pointer and keyboard

    /// <summary>
    /// Section 13's click, tap and Space rules. The mode's semantics live here so every input
    /// path reports the same selection action.
    /// </summary>
    internal void Select(object item, bool ctrl, bool shift, IReadOnlyList<object> view)
    {
        if (!IsInteractive(item))
        {
            return;
        }

        if (Mode == ListViewSelectionMode.None)
        {
            Apply(new List<object>(), item, item, item);
            return;
        }

        if (shift)
        {
            Range(item, add: ctrl, view);
            return;
        }

        if (ctrl || Mode == ListViewSelectionMode.Multiple)
        {
            Toggle(item);
            return;
        }

        Replace(item);
    }

    /// <summary>Plain replace: one row selected, and it becomes current, anchor, and focus.</summary>
    internal void Replace(object item)
    {
        if (!IsInteractive(item))
        {
            return;
        }

        Apply(Limit(new List<object> { item }), item, item, item);
    }

    internal void Toggle(object item)
    {
        if (!IsInteractive(item))
        {
            return;
        }

        List<object> next = Mode == ListViewSelectionMode.Single ? new() : new(_selected);
        if (_selected.Contains(item))
        {
            next.RemoveAll(candidate => _identity.Equals(candidate, item));
        }
        else
        {
            next.Add(item);
        }

        Apply(Limit(next), item, item, item);
    }

    internal void Navigate(object item, bool ctrl, bool shift, IReadOnlyList<object> view)
    {
        if (!IsInteractive(item))
        {
            return;
        }

        if (shift && AllowsMultiple)
        {
            Range(item, add: ctrl, view);
            return;
        }

        if (ctrl || Mode == ListViewSelectionMode.Multiple)
        {
            Apply(new List<object>(_selected), item, Anchor, item);
            return;
        }

        Replace(item);
    }

    /// <summary>
    /// The inclusive range from the anchor to <paramref name="item"/>. The anchor does not move,
    /// so a second Shift click re-projects the range instead of growing it.
    /// </summary>
    internal void Range(object item, bool add, IReadOnlyList<object> view)
    {
        if (!IsInteractive(item))
        {
            return;
        }

        if (!AllowsMultiple)
        {
            if (Mode == ListViewSelectionMode.None)
            {
                Apply(new List<object>(), item, item, item);
            }
            else
            {
                Replace(item);
            }
            return;
        }

        int to = _indexOf(item);
        if (to < 0)
        {
            return;
        }

        object anchor = Anchor ?? item;
        int from = _indexOf(anchor);
        if (from < 0)
        {
            from = to;
            anchor = item;
        }

        (int low, int high) = from <= to ? (from, to) : (to, from);

        List<object> next = add ? new List<object>(_selected) : new List<object>();
        HashSet<object> seen = new(next, _identity);
        for (int i = low; i <= high; i++)
        {
            if (IsInteractive(view[i]) && seen.Add(view[i]))
            {
                next.Add(view[i]);
            }
        }

        Apply(next, item, anchor, item);
    }

    /// <summary>Ctrl+A. Only multiple selection can express it, and it leaves the anchor alone.</summary>
    internal void SelectAll(IReadOnlyList<object> view)
    {
        if (!AllowsMultiple)
        {
            return;
        }

        List<object> next = new();
        foreach (object item in view)
        {
            if (IsInteractive(item))
            {
                next.Add(item);
            }
        }

        object? current = Current ?? (next.Count > 0 ? next[0] : null);
        Apply(next, current, Anchor ?? current, Focus ?? current);
    }

    internal void Clear() => Apply(new List<object>(), null, null, null);

    /// <summary>
    /// Section 14. The marquee's result replaces the selected packet and moves nothing else. The
    /// anchor especially must not move: a Shift marquee re-projects its range from that anchor on
    /// every pointer move, and an anchor that followed the result would walk with it.
    /// </summary>
    internal void SetMarqueeSelection(List<object> items, IReadOnlyList<object> view) =>
        Apply(ResolveSelection(items, null, view, out _), Current, Anchor, Focus);

    // ------------------------------------------------------------------ programmatic and source

    /// <summary>
    /// Resolve requested identities against the view, excluding duplicate,
    /// unavailable and non-interactive items, then apply the mode limit in one step.
    /// </summary>
    internal void SetSelection(
        IEnumerable<object> items,
        object? currentItem,
        IReadOnlyList<object> view
    )
    {
        List<object> resolved = ResolveSelection(items, currentItem, view, out object? current);
        current ??= resolved.Count > 0 ? resolved[0] : null;
        Apply(resolved, current, current, current);
    }

    private List<object> ResolveSelection(
        IEnumerable<object> items,
        object? currentItem,
        IReadOnlyList<object> view,
        out object? current
    )
    {
        HashSet<int> positions = new();
        foreach (object item in items)
        {
            if (item is not null)
            {
                int index = _indexOf(item);
                if (index >= 0 && IsInteractive(view[index]))
                    positions.Add(index);
            }
        }

        List<object> resolved = positions.Order().Select(index => view[index]).ToList();
        int currentIndex = currentItem is null ? -1 : _indexOf(currentItem);
        current =
            currentIndex >= 0 && IsInteractive(view[currentIndex]) ? view[currentIndex] : null;
        return Limit(resolved);
    }

    /// <summary>
    /// Rehydrate tracked identities through the prepared index, prune what left or became
    /// non-interactive, and repair current without scanning every displayed row.
    /// </summary>
    internal void Reconcile(
        IReadOnlyList<object> view,
        object? currentFallback = null,
        object? focusFallback = null
    )
    {
        var kept = Limit(_selected.Select(Resolve).OfType<object>().OrderBy(_indexOf).ToList());
        var current = Resolve(Current);
        var anchor = Resolve(Anchor);
        var focus = Resolve(Focus);

        current ??= currentFallback;
        focus ??= focusFallback;

        // A lost current falls back to the first retained selected item in current visual order.
        if (Current is not null && current is null && kept.Count > 0)
        {
            current = kept[0];
        }

        Apply(kept, current, anchor, focus);

        object? Resolve(object? item)
        {
            var index = item is null ? -1 : _indexOf(item);
            return index >= 0 && index < view.Count && IsInteractive(view[index]) ? view[index] : null;
        }
    }

    // ------------------------------------------------------------------ internals

    private List<object> Limit(List<object> items) =>
        Mode switch
        {
            ListViewSelectionMode.None => new List<object>(),
            ListViewSelectionMode.Single when items.Count > 1 => new List<object> { items[0] },
            _ => items,
        };

    /// <summary>
    /// Always adopt the resolved instances, even when their identities are unchanged.
    /// </summary>
    private void Apply(List<object> selected, object? current, object? anchor, object? focus)
    {
        _selected.Clear();
        foreach (object item in selected)
        {
            _selected.Add(item);
        }

        Current = current;
        Anchor = anchor;
        Focus = focus;
    }
}
