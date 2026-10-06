using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView.Body;

public sealed partial class Surface : ListView
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(Surface owner) : ListViewAutomationPeer(owner), ISelectionProvider
    {
        internal Table? Table => Row.FindOwner(owner);

        protected override ItemAutomationPeer OnCreateItemAutomationPeer(object item) => new ItemPeer(item, this);

        protected override object? GetPatternCore(PatternInterface pattern) =>
            pattern == PatternInterface.Selection ?
                Table is { SelectionMode: not ListViewSelectionMode.None } ? this : null : base.GetPatternCore(pattern);

        bool ISelectionProvider.CanSelectMultiple => Table?.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended;
        bool ISelectionProvider.IsSelectionRequired => false;
        IRawElementProviderSimple[] ISelectionProvider.GetSelection() => GetSelection();
    }

    private sealed class ItemPeer(object item, Peer parent) : ListViewItemDataAutomationPeer(item, parent), ISelectionItemProvider
    {
        private object? Current => parent.Table?.ResolveItem(Item);
        private bool CanSelect => parent.Table is { SelectionMode: not ListViewSelectionMode.None } table &&
            Current is { } current && table.CanInteract?.Invoke(current) != false;

        protected override object? GetPatternCore(PatternInterface pattern) =>
            pattern == PatternInterface.SelectionItem ? CanSelect ? this : null : base.GetPatternCore(pattern);

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
