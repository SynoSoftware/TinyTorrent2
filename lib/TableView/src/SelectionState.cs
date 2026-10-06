using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView;

/// <summary>
/// Section 5's identity rule. Without a key selector identity is object reference; with one it is
/// the configured key using its default equality comparer. The selector is captured once, so the
/// instance is created empty and filled at schema capture.
/// </summary>
internal sealed class ItemIdentity : IEqualityComparer<object>
{
    internal Func<object, object>? KeySelector { get; set; }

    internal IEqualityComparer<object> KeyComparer { get; set; } = EqualityComparer<object>.Default;

    public new bool Equals(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return KeySelector is not null
            && KeyComparer.Equals(KeySelector(x), KeySelector(y));
    }

    public int GetHashCode(object obj) => KeySelector is null
        ? RuntimeHelpers.GetHashCode(obj)
        : KeyComparer.GetHashCode(KeySelector(obj));
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
    private HashSet<object> _selected;

    internal SelectionState(ItemIdentity identity)
    {
        _identity = identity;
        _selected = new HashSet<object>(identity);
    }

    internal ListViewSelectionMode Mode { get; set; } = ListViewSelectionMode.Extended;

    /// <summary>Section 5's interaction predicate. Null means every item is interactive.</summary>
    internal Func<object, bool>? Eligible { get; set; }

    internal object? Current { get; private set; }

    /// <summary>The row a range selection extends from.</summary>
    internal object? Anchor { get; private set; }

    /// <summary>Logical row focus. Not the same thing as physical keyboard focus.</summary>
    internal object? Focus { get; private set; }

    internal sealed record Checkpoint(object[] Items, object? Current, object? Anchor, object? Focus);

    internal Checkpoint Capture() => new(_selected.ToArray(), Current, Anchor, Focus);

    internal void Restore(Checkpoint checkpoint, IReadOnlyList<object> view)
    {
        Apply(new List<object>(checkpoint.Items), checkpoint.Current, checkpoint.Anchor, checkpoint.Focus);
        Reconcile(view);
    }

    internal bool AllowsMultiple =>
        Mode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended;

    internal bool IsSelected(object item) => _selected.Contains(item);

    internal bool IsEligible(object item) => Eligible is null || Eligible(item);

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
        if (!IsEligible(item))
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
        if (!IsEligible(item))
        {
            return;
        }

        Apply(Limit(new List<object> { item }), item, item, item);
    }

    internal void Toggle(object item)
    {
        if (!IsEligible(item))
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
        if (!IsEligible(item))
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
        if (!IsEligible(item))
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

        int to = IndexOf(view, item);
        if (to < 0)
        {
            return;
        }

        object anchor = Anchor ?? item;
        int from = IndexOf(view, anchor);
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
            if (IsEligible(view[i]) && seen.Add(view[i]))
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
            if (IsEligible(item))
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
    internal void SetSelection(IEnumerable<object> items, object? currentItem, IReadOnlyList<object> view)
    {
        List<object> resolved = ResolveSelection(items, currentItem, view, out object? current);
        current ??= resolved.Count > 0 ? resolved[0] : null;
        Apply(resolved, current, current, current);
    }

    private List<object> ResolveSelection(
        IEnumerable<object> items, object? currentItem, IReadOnlyList<object> view, out object? current)
    {
        HashSet<object> requested = new(_identity);
        foreach (object item in items)
        {
            if (item is not null)
            {
                requested.Add(item);
            }
        }

        List<object> resolved = new();
        current = null;
        foreach (object item in view)
        {
            if (!IsEligible(item))
            {
                continue;
            }

            if (requested.Contains(item))
            {
                resolved.Add(item);
            }

            if (currentItem is not null && _identity.Equals(item, currentItem))
            {
                current = item;
            }
        }

        return Limit(resolved);
    }

    /// <summary>
    /// Section 5.3's reconciliation. One pass over the new view rehydrates every tracked identity
    /// onto the new instances, prunes what left or became non-interactive, and repairs current.
    /// </summary>
    internal void Reconcile(IReadOnlyList<object> view)
    {
        List<object> kept = new();
        object? current = null;
        object? anchor = null;
        object? focus = null;

        foreach (object item in view)
        {
            if (!IsEligible(item))
            {
                continue;
            }

            if (_selected.Contains(item))
            {
                kept.Add(item);
            }

            if (Current is not null && _identity.Equals(item, Current))
            {
                current = item;
            }

            if (Anchor is not null && _identity.Equals(item, Anchor))
            {
                anchor = item;
            }

            if (Focus is not null && _identity.Equals(item, Focus))
            {
                focus = item;
            }
        }

        kept = Limit(kept);

        // A lost current falls back to the first retained selected item in current visual order.
        if (Current is not null && current is null && kept.Count > 0)
        {
            current = kept[0];
        }

        Apply(kept, current, anchor, focus);
    }

    // ------------------------------------------------------------------ internals

    private List<object> Limit(List<object> items) => Mode switch
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

    private int IndexOf(IReadOnlyList<object> view, object item)
    {
        for (int i = 0; i < view.Count; i++)
        {
            if (_identity.Equals(view[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
