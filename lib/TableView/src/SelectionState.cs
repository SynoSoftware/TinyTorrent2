using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView;

/// <summary>The authoritative selection, current item, range anchor and logical focus.</summary>
internal sealed class SelectionState
{
    private IIdentity _identity = null!;
    private readonly Body.View _view;
    private IdentitySet _selected = null!;
    private IdentitySet _added = null!;
    private IdentitySet _removed = null!;
    private readonly SortedSet<int> _indices = new();
    private long _indicesVersion = -1;
    private RangeGesture? _range;
    private MarqueeGesture? _marquee;

    /// <remarks>The table installs the identity before first use (<c>SetIdentity</c>).</remarks>
    internal SelectionState(Body.View view)
    {
        _view = view;
    }

    internal ListViewSelectionMode Mode { get; set; } = ListViewSelectionMode.Extended;
    /// <summary>Section 5's interaction predicate. Null means every item is interactive.</summary>
    internal Func<object, bool>? CanInteract { get; set; }
    internal object? Current { get; private set; }
    internal object? Anchor { get; private set; }
    /// <summary>Logical row focus. Not the same thing as physical keyboard focus.</summary>
    internal object? Focus { get; private set; }
    internal long ItemsVersion { get; private set; }
    internal int Count => _selected.Count;
    internal IEnumerable<object> Items => _selected;
    internal IReadOnlyCollection<int> SelectedIndices
    {
        get
        {
            EnsureIndices();
            return _indices;
        }
    }

    /// <summary>The view index of the first selected row, or -1 when nothing is selected.</summary>
    internal int FirstSelectedIndex
    {
        get
        {
            EnsureIndices();
            return _indices.Count > 0 ? _indices.Min : -1;
        }
    }

    internal sealed record Checkpoint(
        IReadOnlyList<object> Items,
        object? Current,
        object? Anchor,
        object? Focus
    );

    private sealed record RangeGesture(
        object Anchor,
        int End,
        bool Add,
        long ViewVersion,
        IdentitySet? Baseline
    );

    private readonly record struct Span(int Low, int High);

    private sealed class MarqueeGesture(IdentitySet baseline, bool ctrl, bool shift)
    {
        internal IdentitySet Baseline { get; } = baseline;
        internal bool Ctrl { get; } = ctrl;
        internal bool Shift { get; } = shift;
        internal Span? Span { get; set; }
    }

    internal Checkpoint Capture(Selection packet) => new(packet.Items, Current, Anchor, Focus);

    internal void Restore(Checkpoint checkpoint, IReadOnlyList<object> view)
    {
        SetSelection(checkpoint.Items, checkpoint.Current, view);
        // A saved row that is gone keeps the first selected row that SetSelection chose.
        Locate(Resolve(checkpoint.Current) ?? Current, Resolve(checkpoint.Anchor) ?? Current,
            Resolve(checkpoint.Focus) ?? Current);
    }

    internal void SetIdentity(IIdentity identity)
    {
        _identity = identity;
        _selected = new(identity);
        _added = new(identity);
        _removed = new(identity);
        _indices.Clear();
        _indicesVersion = -1;
        _range = null;
        _marquee = null;
    }

    internal bool AllowsMultiple => Mode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended;
    internal bool IsSelected(object item) => _selected.Contains(item);
    internal bool IsInteractive(object item) => CanInteract?.Invoke(item) != false;
    internal bool IsSame(object? left, object? right) => _identity.IsSame(left, right);

    private void EnsureIndices()
    {
        if (_indicesVersion == _view.Version)
        {
            return;
        }
        _indices.Clear();
        foreach (object item in _selected)
        {
            int index = _view.IndexOfIdentity(item);
            if (index >= 0)
            {
                _indices.Add(index);
            }
        }
        _indicesVersion = _view.Version;
    }

    private bool Add(object item)
    {
        EnsureIndices();
        if (!_selected.Add(item))
        {
            return false;
        }
        _indices.Add(_view.Positions.IndexOf(item));
        if (!_removed.Remove(item))
        {
            _added.Add(item);
        }
        ItemsVersion++;
        return true;
    }

    private bool Remove(object item)
    {
        EnsureIndices();
        if (!_selected.Contains(item))
        {
            return false;
        }
        if (!_added.Remove(item))
        {
            _removed.Add(item);
        }
        int index = _view.Positions.IndexOf(item);
        _selected.Remove(item);
        _indices.Remove(index);
        ItemsVersion++;
        return true;
    }

    private void Locate(object? current, object? anchor, object? focus)
    {
        Current = current;
        Anchor = anchor;
        Focus = focus;
    }

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
            ClearItems();
            Locate(item, item, item);
        }
        else if (shift)
        {
            Range(item, ctrl, view);
        }
        else if (ctrl || Mode == ListViewSelectionMode.Multiple)
        {
            Toggle(item);
        }
        else
        {
            Replace(item);
        }
    }

    internal void Replace(object item)
    {
        if (!IsInteractive(item))
        {
            return;
        }
        Apply(Mode == ListViewSelectionMode.None ? new() : new() { item }, item, item, item);
    }

    internal void Toggle(object item)
    {
        if (!IsInteractive(item))
        {
            return;
        }
        _range = null;
        if (IsSelected(item))
        {
            Remove(item);
        }
        else if (Mode != ListViewSelectionMode.None)
        {
            if (Mode == ListViewSelectionMode.Single)
            {
                ClearItems();
            }
            Add(item);
        }
        Locate(item, item, item);
    }

    internal void Navigate(object item, bool ctrl, bool shift, IReadOnlyList<object> view)
    {
        if (!IsInteractive(item))
        {
            return;
        }
        if (shift && AllowsMultiple)
        {
            Range(item, ctrl, view);
        }
        else if (ctrl || Mode == ListViewSelectionMode.Multiple)
        {
            _range = null;
            Locate(item, Anchor, item);
        }
        else
        {
            Replace(item);
        }
    }

    /// <summary>
    /// The inclusive range from the anchor to <paramref name="item"/>. The anchor does not move,
    /// so a second Shift click re-projects the range instead of growing it.
    /// </summary>
    /// <remarks>
    /// While the anchor, the add mode and the view stay the same, the next step changes only the
    /// rows between the old and new ends. Any other selection action, or a view change, starts
    /// the next range from scratch.
    /// </remarks>
    internal void Range(object item, bool add, IReadOnlyList<object> view)
    {
        if (!IsInteractive(item))
        {
            return;
        }
        if (!AllowsMultiple)
        {
            Replace(item);
            return;
        }
        int to = _view.Positions.IndexOf(item);
        if (to < 0)
        {
            return;
        }
        object anchor = Anchor ?? item;
        int from = _view.Positions.IndexOf(anchor);
        if (from < 0)
        {
            anchor = item;
            from = to;
        }
        int low = Math.Min(from, to);
        int high = Math.Max(from, to);
        RangeGesture? previous = _range;
        if (previous is not null && ReferenceEquals(previous.Anchor, anchor)
            && previous.Add == add && previous.ViewVersion == _view.Version)
        {
            ChangeSpan(new(Math.Min(from, previous.End), Math.Max(from, previous.End)),
                new(low, high), previous.Baseline);
            _range = previous with { End = to };
        }
        else
        {
            IdentitySet? baseline = null;
            if (add)
            {
                baseline = new(_identity);
                foreach (object selected in _selected)
                {
                    baseline.Add(selected);
                }
            }
            else
            {
                ClearItems();
            }
            AddSpan(low, high);
            _range = new(anchor, to, add, _view.Version, baseline);
        }
        Locate(item, anchor, item);
    }

    private void AddSpan(int low, int high)
    {
        for (int index = low; index <= high; index++)
        {
            if (IsInteractive(_view[index]))
            {
                Add(_view[index]);
            }
        }
    }

    private void ChangeSpan(Span before, Span after, IdentitySet? baseline = null)
    {
        RemoveSpan(before.Low, Math.Min(before.High, after.Low - 1), baseline);
        RemoveSpan(Math.Max(before.Low, after.High + 1), before.High, baseline);
        AddSpan(after.Low, Math.Min(after.High, before.Low - 1));
        AddSpan(Math.Max(after.Low, before.High + 1), after.High);
    }

    private void RemoveSpan(int low, int high, IdentitySet? baseline)
    {
        for (int index = low; index <= high; index++)
        {
            object item = _view[index];
            if (baseline?.Contains(item) != true)
            {
                Remove(item);
            }
        }
    }

    internal void SelectAll(IReadOnlyList<object> view)
    {
        if (!AllowsMultiple)
        {
            return;
        }
        _range = null;
        foreach (object item in view)
        {
            if (IsInteractive(item))
            {
                Add(item);
            }
        }
        object? current = Current ?? (_indices.Count > 0 ? _view[_indices.Min] : null);
        Locate(current, Anchor ?? current, Focus ?? current);
    }

    private void ClearItems()
    {
        foreach (object item in _selected)
        {
            if (!_added.Remove(item))
            {
                _removed.Add(item);
            }
        }
        if (_selected.Count > 0)
        {
            ItemsVersion++;
        }
        _selected.Clear();
        _indices.Clear();
        _indicesVersion = _view.Version;
    }

    internal void Clear()
    {
        _range = null;
        ClearItems();
        Locate(null, null, null);
    }

    internal void Context(object item)
    {
        if (!IsSelected(item))
        {
            Replace(item);
        }
        else
        {
            _range = null;
            Locate(item, item, item);
        }
    }

    /// <summary>
    /// Section 14's three compositions. The gesture's modifiers are the ones read at press, so a
    /// key pressed or released mid-drag does not change the rule the user started under.
    /// </summary>
    /// <remarks>
    /// The marquee's result replaces the selected packet and moves nothing else. The anchor
    /// especially must not move: a Shift marquee re-projects its range from that anchor on every
    /// pointer move, and an anchor that followed the result would walk with it.
    /// </remarks>
    internal void BeginMarquee(Checkpoint checkpoint, bool ctrl, bool shift)
    {
        _range = null;
        IdentitySet baseline = new(_identity);
        foreach (object item in checkpoint.Items)
        {
            if (Resolve(item) is { } current)
            {
                baseline.Add(current);
            }
        }
        _marquee = new(baseline, ctrl, shift);
        ResetMarquee();
    }

    internal void EndMarquee()
    {
        _marquee?.Baseline.Clear();
        _marquee = null;
    }

    private void ResetMarquee()
    {
        MarqueeGesture gesture = _marquee!;
        ClearItems();
        gesture.Span = null;
        if (gesture.Ctrl || gesture.Shift)
        {
            foreach (object item in gesture.Baseline)
            {
                int index = _view.Positions.IndexOf(item);
                if (index >= 0 && IsInteractive(_view[index]))
                {
                    Add(_view[index]);
                }
            }
        }
    }

    internal void RebaseMarquee(IReadOnlyCollection<int> covered, (int Low, int High)? bounds)
    {
        ResetMarquee();
        UpdateMarquee(covered, Array.Empty<int>(), bounds);
    }

    internal void UpdateMarquee(IReadOnlyCollection<int> added, IReadOnlyCollection<int> removed,
        (int Low, int High)? covered)
    {
        MarqueeGesture gesture = _marquee!;
        if (gesture.Shift)
        {
            int anchor = _view.IndexOfIdentity(Anchor);
            if (anchor < 0 || covered is null)
            {
                if (gesture.Span is not null)
                {
                    ResetMarquee();
                }
                return;
            }
            Span span = new(Math.Min(anchor, covered.Value.Low), Math.Max(anchor, covered.Value.High));
            if (gesture.Span is { } previous)
            {
                ChangeSpan(previous, span);
            }
            else
            {
                ClearItems();
                AddSpan(span.Low, span.High);
            }
            gesture.Span = span;
            return;
        }
        foreach (int index in removed)
        {
            object item = _view[index];
            if (gesture.Ctrl && gesture.Baseline.Contains(item) && IsInteractive(item))
            {
                Add(item);
            }
            else
            {
                Remove(item);
            }
        }
        foreach (int index in added)
        {
            object item = _view[index];
            if (IsInteractive(item) && (!gesture.Ctrl || !gesture.Baseline.Contains(item)))
            {
                Add(item);
            }
            else
            {
                Remove(item);
            }
        }
    }

    internal void SetSelection(IEnumerable<object> items, object? currentItem, IReadOnlyList<object> view)
    {
        List<object> resolved = ResolveSelection(items);
        object? current = Resolve(currentItem) ?? (resolved.Count > 0 ? resolved[0] : null);
        Apply(resolved, current, current, current);
    }

    private object? Resolve(object? item) =>
        item is not null && _view.Positions.Resolve(item) is { } current && IsInteractive(current)
            ? current : null;

    private List<object> ResolveSelection(IEnumerable<object> items)
    {
        SortedSet<int> positions = new();
        foreach (object item in items)
        {
            int index = item is null ? -1 : _view.Positions.IndexOf(item);
            if (index >= 0 && IsInteractive(_view[index]))
            {
                positions.Add(index);
            }
        }
        List<object> resolved = new();
        foreach (int index in positions)
        {
            if (Mode == ListViewSelectionMode.None)
            {
                break;
            }
            resolved.Add(_view[index]);
            if (Mode == ListViewSelectionMode.Single)
            {
                break;
            }
        }
        return resolved;
    }

    /// <summary>
    /// Section 5.3's reconciliation. Rehydrates tracked identities onto the new instances, prunes
    /// what left or became non-interactive, and repairs current. A change that names its rows
    /// limits the pass to them.
    /// </summary>
    internal void Reconcile(IReadOnlyList<object> view, object? currentFallback = null, object? focusFallback = null,
        Body.Source.Preparation? preparation = null)
    {
        _range = null;
        bool moved = _indicesVersion != _view.Version;
        List<object>? replace = null;
        List<object>? remove = null;
        IEnumerable<object> affected = preparation is null ? _selected : Affected(preparation);
        foreach (object named in affected)
        {
            object? item = _selected.Map.Resolve(named);
            if (item is null)
            {
                continue;
            }
            int index = _view.Positions.IndexOf(item);
            if (index < 0 || !IsInteractive(_view[index]))
            {
                (remove ??= new()).Add(item);
            }
            else if (!ReferenceEquals(item, _view[index]))
            {
                (replace ??= new()).Add(_view[index]);
            }
        }
        if (remove is not null)
        {
            foreach (object item in remove)
            {
                Remove(item);
            }
        }
        if (replace is not null)
        {
            foreach (object item in replace)
            {
                _selected.Map.Set(item, 0);
            }
            ItemsVersion++;
        }
        object? current = Retain(Current) ?? currentFallback;
        object? anchor = Retain(Anchor);
        object? focus = Retain(Focus) ?? focusFallback;
        EnsureIndices();
        // A lost current falls back to the first retained selected item in current visual order.
        if (Current is not null && current is null && _indices.Count > 0)
        {
            current = _view[_indices.Min];
        }
        if (moved)
        {
            ItemsVersion++;
        }
        Locate(current, anchor, focus);

        object? Retain(object? item)
        {
            int index = item is null ? -1 : _view.Positions.IndexOf(item);
            bool inspect = item is not null
                && (preparation is null || Affected(preparation).Any(row => IsSame(row, item)));
            return index >= 0 && (!inspect || IsInteractive(_view[index])) ? _view[index] : null;
        }
    }

    private static IEnumerable<object> Affected(Body.Source.Preparation preparation) =>
        preparation.Leavers.Concat(preparation.Arrivals.Select(arrival => arrival.Item)).Concat(preparation.Reread);

    private void Apply(List<object> items, object? current, object? anchor, object? focus)
    {
        _range = null;
        ClearItems();
        foreach (object item in items)
        {
            Add(item);
        }
        Locate(current, anchor, focus);
    }

    internal object[] BuildPacket()
    {
        EnsureIndices();
        object[] packet = new object[_selected.Count];
        int next = 0;
        if (_selected.Count > _view.Count / 2)
        {
            foreach (object item in _view)
            {
                if (_selected.Contains(item))
                {
                    packet[next++] = item;
                }
            }
        }
        else
        {
            foreach (int index in _indices)
            {
                packet[next++] = _view[index];
            }
        }
        return packet;
    }

    internal object[] Added => _added.ToArray();
    internal object[] Removed => _removed.ToArray();
    internal bool MembershipChanged => _added.Count > 0 || _removed.Count > 0;

    internal void AcceptChanges()
    {
        _added.Clear();
        _removed.Clear();
    }
}
