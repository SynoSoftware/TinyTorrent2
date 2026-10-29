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
        if (FrameworkElementAutomationPeer.FromElement(this) is Peer peer)
            peer.RefreshHierarchy();
    }

    /// <summary>A UIA client listens for selection events, so they are worth raising.</summary>
    internal static bool SelectionListens =>
        AutomationPeer.ListenerExists(AutomationEvents.SelectionPatternOnInvalidated)
        || AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected)
        || AutomationPeer.ListenerExists(
            AutomationEvents.SelectionItemPatternOnElementAddedToSelection
        )
        || AutomationPeer.ListenerExists(
            AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection
        );

    /// <summary>
    /// Raise the UIA selection events the list raises for a selection it keeps itself. With the
    /// view answering selection, the list's own <c>SelectionChanged</c> carries no added or removed
    /// rows, so it raises none.
    /// </summary>
    internal void RaiseSelectionEvents(
        IReadOnlyList<object> added,
        IReadOnlyList<object> removed,
        int selected
    )
    {
        if (
            added.Count + removed.Count > 0
            && FrameworkElementAutomationPeer.CreatePeerForElement(this) is Peer peer
        )
            peer.RaiseSelectionEvents(added, removed, selected);
    }

    private sealed class Peer(Surface owner) : ListViewAutomationPeer(owner), ISelectionProvider
    {
        /// <summary>Beyond this many changed rows the list reports the selection as invalidated.</summary>
        private const int BulkChildrenLimit = 20;

        private List<WeakReference<ItemPeer>>? _itemPeers;
        internal Table? Table => Row.FindOwner(owner);

        protected override ItemAutomationPeer OnCreateItemAutomationPeer(object item)
        {
            ItemPeer peer = new(item, this);
            if (Table?.Hierarchy is not null)
            {
                (_itemPeers ??= []).Add(new(peer));
                peer.RefreshHierarchy();
            }
            return peer;
        }

        internal void RefreshHierarchy()
        {
            if (_itemPeers is null)
                return;
            for (int i = _itemPeers.Count - 1; i >= 0; i--)
            {
                if (_itemPeers[i].TryGetTarget(out var peer))
                    peer.RefreshHierarchy();
                else
                    _itemPeers.RemoveAt(i);
            }
        }

        /// <summary>
        /// The list's own rules: a row that is newly the whole selection is "selected", and the
        /// one row it replaced is "removed"; past the bulk limit the selection is reported
        /// invalidated as a whole; otherwise each row is reported added or removed.
        /// </summary>
        internal void RaiseSelectionEvents(
            IReadOnlyList<object> added,
            IReadOnlyList<object> removed,
            int selected
        )
        {
            if (added.Count == 1 && selected == 1)
            {
                Raise(added[0], AutomationEvents.SelectionItemPatternOnElementSelected);
                if (removed.Count == 1)
                    Raise(
                        removed[0],
                        AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection
                    );
                return;
            }
            if (added.Count + removed.Count > BulkChildrenLimit)
            {
                RaiseAutomationEvent(AutomationEvents.SelectionPatternOnInvalidated);
                return;
            }
            foreach (object item in added)
                Raise(item, AutomationEvents.SelectionItemPatternOnElementAddedToSelection);
            foreach (object item in removed)
                Raise(item, AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection);
        }

        private void Raise(object item, AutomationEvents automationEvent) =>
            CreateItemAutomationPeer(item)?.RaiseAutomationEvent(automationEvent);

        protected override object? GetPatternCore(PatternInterface pattern) =>
            pattern == PatternInterface.Selection
                ? Table is { SelectionMode: not ListViewSelectionMode.None }
                    ? this
                    : null
                : base.GetPatternCore(pattern);

        bool ISelectionProvider.CanSelectMultiple =>
            Table?.SelectionMode
                is ListViewSelectionMode.Multiple
                    or ListViewSelectionMode.Extended;
        bool ISelectionProvider.IsSelectionRequired => false;

        // The list's own provider reports the selection the list keeps, and this list keeps none:
        // the view answers from the table.
        IRawElementProviderSimple[] ISelectionProvider.GetSelection() =>
            Table is { } table
                ? table
                    .SelectedItems.Select(item => ProviderFromPeer(CreateItemAutomationPeer(item)))
                    .ToArray()
                : [];
    }

    private sealed class ItemPeer(object item, Peer parent)
        : ListViewItemDataAutomationPeer(item, parent),
            ISelectionItemProvider,
            IExpandCollapseProvider
    {
        private ExpandCollapseState? _expanded;
        private object? Current => parent.Table?.ResolveItem(Item);
        private bool CanSelect =>
            parent.Table is { SelectionMode: not ListViewSelectionMode.None } table
            && Current is { } current
            && table.IsInteractive(current);

        protected override object? GetPatternCore(PatternInterface pattern) =>
            pattern switch
            {
                PatternInterface.SelectionItem => CanSelect ? this : null,
                PatternInterface.ExpandCollapse when parent.Table?.Hierarchy is not null => this,
                _ => base.GetPatternCore(pattern),
            };

        protected override int GetLevelCore() =>
            parent.Table?.Hierarchy?.Find(Item) is { } node ? node.Depth + 1 : base.GetLevelCore();

        protected override int GetPositionInSetCore() =>
            parent.Table?.Hierarchy is not null
                ? parent.Table.HierarchyPosition(Item)
                : base.GetPositionInSetCore();

        protected override int GetSizeOfSetCore() =>
            parent.Table?.Hierarchy is not null
                ? parent.Table.HierarchyCount(Item)
                : base.GetSizeOfSetCore();

        private ExpandCollapseState Expansion
        {
            get
            {
                var hierarchy = parent.Table?.Hierarchy;
                if (hierarchy?.Find(Item) is not { } node || !hierarchy.IsExpandable(node))
                    return ExpandCollapseState.LeafNode;
                return hierarchy.IsExpanded(node.Item)
                    ? ExpandCollapseState.Expanded
                    : ExpandCollapseState.Collapsed;
            }
        }

        ExpandCollapseState IExpandCollapseProvider.ExpandCollapseState => Expansion;

        void IExpandCollapseProvider.Expand() => Expand(true);

        void IExpandCollapseProvider.Collapse() => Expand(false);

        private void Expand(bool value)
        {
            if (parent.Table is not { } table || Current is not { } current)
                throw new ElementNotAvailableException();
            if (!IsEnabled())
                throw new ElementNotEnabledException();
            if (Expansion == ExpandCollapseState.LeafNode)
                throw new InvalidOperationException();
            table.Expand(current, value);
        }

        internal void RefreshHierarchy()
        {
            if (Current is null)
                return;
            var next = Expansion;
            if (_expanded is { } previous && previous != next)
                RaisePropertyChangedEvent(
                    ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                    previous,
                    next
                );
            _expanded = next;
        }

        protected override bool IsEnabledCore() =>
            parent.IsEnabled()
            && Current is { } current
            && parent.Table?.IsInteractive(current) != false;

        bool ISelectionItemProvider.IsSelected => parent.Table?.IsRowSelected(Item) == true;
        IRawElementProviderSimple ISelectionItemProvider.SelectionContainer =>
            ProviderFromPeer(parent);

        void ISelectionItemProvider.Select()
        {
            var (table, current) = RequireSelection();
            table.Selection = new Selection([current], current);
        }

        void ISelectionItemProvider.AddToSelection()
        {
            var (table, current) = RequireSelection();
            if (table.IsRowSelected(current))
                return;
            if (
                table.SelectionMode == ListViewSelectionMode.Single
                && table.Selection.Items.Count != 0
            )
                throw new InvalidOperationException();
            table.Selection = new Selection(table.Selection.Items.Append(current), current);
        }

        void ISelectionItemProvider.RemoveFromSelection()
        {
            var (table, current) = RequireSelection();
            if (!table.IsRowSelected(current))
                return;
            table.Selection = new Selection(
                table.Selection.Items.Where(selected => !ReferenceEquals(selected, current)),
                current
            );
        }

        private (Table Table, object Item) RequireSelection()
        {
            if (parent.Table is not { } table || Current is not { } current)
                throw new ElementNotAvailableException();
            if (!IsEnabled())
                throw new ElementNotEnabledException();
            if (table.SelectionMode == ListViewSelectionMode.None)
                throw new InvalidOperationException();
            return (table, current);
        }
    }
}
