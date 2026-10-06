using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView.Body;

public sealed partial class Surface : ListView
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    internal void RefreshHierarchy()
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is Peer peer) peer.RefreshHierarchy();
    }

    private sealed class Peer(Surface owner) : ListViewAutomationPeer(owner), ISelectionProvider
    {
        private List<WeakReference<ItemPeer>>? _branches;
        internal Table? Table => Row.FindOwner(owner);

        protected override ItemAutomationPeer OnCreateItemAutomationPeer(object item)
        {
            ItemPeer peer = new(item, this);
            if (Table?.Hierarchy is not null)
            {
                (_branches ??= []).Add(new(peer));
                peer.RefreshHierarchy();
            }
            return peer;
        }

        internal void RefreshHierarchy()
        {
            if (_branches is null) return;
            for (int i = _branches.Count - 1; i >= 0; i--)
            {
                if (_branches[i].TryGetTarget(out var peer)) peer.RefreshHierarchy();
                else _branches.RemoveAt(i);
            }
        }

        protected override object? GetPatternCore(PatternInterface pattern) =>
            pattern == PatternInterface.Selection ?
                Table is { SelectionMode: not ListViewSelectionMode.None } ? this : null : base.GetPatternCore(pattern);

        bool ISelectionProvider.CanSelectMultiple => Table?.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended;
        bool ISelectionProvider.IsSelectionRequired => false;
        IRawElementProviderSimple[] ISelectionProvider.GetSelection() => GetSelection();
    }

    private sealed class ItemPeer(object item, Peer parent) : ListViewItemDataAutomationPeer(item, parent), ISelectionItemProvider, IExpandCollapseProvider
    {
        private ExpandCollapseState? _expanded;
        private object? Current => parent.Table?.ResolveItem(Item);
        private bool CanSelect => parent.Table is { SelectionMode: not ListViewSelectionMode.None } table &&
            Current is { } current && table.CanInteract?.Invoke(current) != false;

        protected override object? GetPatternCore(PatternInterface pattern) => pattern switch
        {
            PatternInterface.SelectionItem => CanSelect ? this : null,
            PatternInterface.ExpandCollapse when parent.Table?.Hierarchy is not null => this,
            _ => base.GetPatternCore(pattern)
        };

        protected override int GetLevelCore() => parent.Table?.Hierarchy?.Find(Item) is { } node
            ? node.Depth + 1 : base.GetLevelCore();
        protected override int GetPositionInSetCore() => parent.Table?.Hierarchy is not null
            ? parent.Table.HierarchyPosition(Item) : base.GetPositionInSetCore();
        protected override int GetSizeOfSetCore() => parent.Table?.Hierarchy is not null
            ? parent.Table.HierarchyCount(Item) : base.GetSizeOfSetCore();

        private ExpandCollapseState Expansion
        {
            get
            {
                var hierarchy = parent.Table?.Hierarchy;
                if (hierarchy?.Find(Item) is not { Children.Count: > 0 } node)
                    return ExpandCollapseState.LeafNode;
                return hierarchy.IsExpanded(node.Item) ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
            }
        }

        ExpandCollapseState IExpandCollapseProvider.ExpandCollapseState => Expansion;
        void IExpandCollapseProvider.Expand() => Expand(true);
        void IExpandCollapseProvider.Collapse() => Expand(false);

        private void Expand(bool value)
        {
            if (parent.Table is not { } table || Current is not { } current) throw new ElementNotAvailableException();
            if (!IsEnabled()) throw new ElementNotEnabledException();
            if (Expansion == ExpandCollapseState.LeafNode) throw new InvalidOperationException();
            table.Expand(current, value);
        }

        internal void RefreshHierarchy()
        {
            if (Current is null) return;
            var next = Expansion;
            if (_expanded is { } previous && previous != next)
                RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, previous, next);
            _expanded = next;
        }

        protected override bool IsEnabledCore() => parent.IsEnabled() && Current is { } current &&
            parent.Table?.CanInteract?.Invoke(current) != false;

        bool ISelectionItemProvider.IsSelected => parent.Table?.IsRowSelected(Item) == true;
        IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => ProviderFromPeer(parent);

        void ISelectionItemProvider.Select()
        {
            var (table, current) = RequireSelection();
            table.Selection = new Selection([current], current);
        }

        void ISelectionItemProvider.AddToSelection()
        {
            var (table, current) = RequireSelection();
            if (table.IsRowSelected(current)) return;
            if (table.SelectionMode == ListViewSelectionMode.Single && table.Selection.Items.Count != 0)
                throw new InvalidOperationException();
            table.Selection = new Selection(table.Selection.Items.Append(current), current);
        }

        void ISelectionItemProvider.RemoveFromSelection()
        {
            var (table, current) = RequireSelection();
            if (!table.IsRowSelected(current)) return;
            table.Selection = new Selection(table.Selection.Items.Where(selected => !ReferenceEquals(selected, current)), current);
        }

        private (Table Table, object Item) RequireSelection()
        {
            if (parent.Table is not { } table || Current is not { } current) throw new ElementNotAvailableException();
            if (!IsEnabled()) throw new ElementNotEnabledException();
            if (table.SelectionMode == ListViewSelectionMode.None) throw new InvalidOperationException();
            return (table, current);
        }
    }
}
