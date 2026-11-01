using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView;

/// <summary>The authoritative selection, current item, range anchor and logical focus.</summary>
internal sealed class SelectionState
{
    private IIdentity _identity;
    private readonly Body.View _view;
    private IdentitySet _selected;
    private IdentityMap<byte> _tracked;
    private IdentitySet _added;
    private IdentitySet _removed;
    private readonly SortedSet<int> _indices = new();
    private long _indicesVersion = -1;
    private RangeGesture? _range;

    internal SelectionState(IIdentity identity, Body.View view)
    {
        _identity = identity;
        _view = view;
        _selected = new(identity, keepsKeys: true);
        _tracked = identity.CreateMap<byte>(keepsKeys: true);
        _added = new(identity, keepsKeys: true);
        _removed = new(identity, keepsKeys: true);
    }

    internal ListViewSelectionMode Mode { get; set; } = ListViewSelectionMode.Extended;
    internal Func<object, bool>? CanInteract { get; set; }
    internal object? Current { get; private set; }
    internal object? Anchor { get; private set; }
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

    internal Checkpoint Capture(Selection packet) => new(packet.Items, Current, Anchor, Focus);

    internal void Restore(Checkpoint checkpoint, IReadOnlyList<object> view)
    {
        SetSelection(checkpoint.Items, checkpoint.Current, view);
        Locate(Resolve(checkpoint.Current), Resolve(checkpoint.Anchor), Resolve(checkpoint.Focus));
    }

    internal void SetIdentity(IIdentity identity)
    {
        _identity = identity;
        _selected = new(identity, keepsKeys: true);
        _tracked = identity.CreateMap<byte>(keepsKeys: true);
        _added = new(identity, keepsKeys: true);
        _removed = new(identity, keepsKeys: true);
        _indices.Clear();
        _indicesVersion = -1;
        _range = null;
    }

    internal bool AllowsMultiple => Mode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended;
    internal bool IsSelected(object item) => _view.KeysFor(item) is { } keys
        ? _selected.Contains(item, keys) : _selected.Contains(item);
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
            int index = _view.Positions.IndexOf(item, _selected);
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
        if (!_selected.Add(item, _view.Positions.Map))
        {
            return false;
        }
        _indices.Add(_view.Positions.IndexOf(item));
        if (!_removed.Remove(item, _view.Positions))
        {
            _added.Add(item, _view.Positions.Map);
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
        if (!_added.Remove(item, _selected))
        {
            _removed.Add(item, _selected.Map);
        }
        int index = _view.Positions.IndexOf(item, _selected);
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
        _tracked.Clear();
        if (current is not null)
        {
            _tracked.TryAdd(current, 0, _view.Positions.Map);
        }
        if (anchor is not null)
        {
            _tracked.TryAdd(anchor, 0, _view.Positions.Map);
        }
        if (focus is not null)
        {
            _tracked.TryAdd(focus, 0, _view.Positions.Map);
        }
    }

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
            int oldLow = Math.Min(from, previous.End);
            int oldHigh = Math.Max(from, previous.End);
            RemoveSpan(oldLow, Math.Min(oldHigh, low - 1), previous.Baseline);
            RemoveSpan(Math.Max(oldLow, high + 1), oldHigh, previous.Baseline);
            AddSpan(low, Math.Min(high, oldLow - 1));
            AddSpan(Math.Max(low, oldHigh + 1), high);
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
                    baseline.Add(selected, _selected.Map);
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
            if (!_added.Remove(item, _selected))
            {
                _removed.Add(item, _selected.Map);
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

    internal void SetMarqueeSelection(List<object> items, IReadOnlyList<object> view) =>
        Apply(ResolveSelection(items), Current, Anchor, Focus);

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

    internal void Reconcile(IReadOnlyList<object> view, object? currentFallback = null, object? focusFallback = null)
    {
        _range = null;
        bool moved = _indicesVersion != _view.Version;
        List<object>? replace = null;
        List<object>? remove = null;
        foreach (object item in _selected)
        {
            int index = _view.Positions.IndexOf(item, _selected);
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
                _selected.Map.Set(item, 0, _view.Positions.Map);
            }
            ItemsVersion++;
        }
        object? current = Retain(Current) ?? currentFallback;
        object? anchor = Retain(Anchor);
        object? focus = Retain(Focus) ?? focusFallback;
        EnsureIndices();
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
            int index = item is null ? -1 : _view.Positions.IndexOf(item, _tracked);
            return index >= 0 && IsInteractive(_view[index]) ? _view[index] : null;
        }
    }

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
                if (_selected.Contains(item, _view.Positions.Map))
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

    internal void AcceptChanges()
    {
        _added.Clear();
        _removed.Clear();
    }
}
