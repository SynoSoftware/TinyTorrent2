using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView.Body;

public sealed partial class Surface : ListView
{
    protected override DependencyObject GetContainerForItemOverride() => new Container();

    private sealed partial class Container : ListViewItem
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed class Peer(Container container) : ListViewItemAutomationPeer(container), ISelectionItemProvider
        {
            protected override object GetPatternCore(PatternInterface pattern) =>
                pattern == PatternInterface.SelectionItem ? this : base.GetPatternCore(pattern);

            private Table? Table => Row.FindOwner(container);

            public bool IsSelected => Table?.IsRowSelected(container.Content) == true;

            public IRawElementProviderSimple SelectionContainer =>
                ProviderFromPeer(FrameworkElementAutomationPeer.CreatePeerForElement(
                    ItemsControl.ItemsControlFromItemContainer(container)));

            public void Select() => Change(new[] { container.Content });

            public void AddToSelection()
            {
                if (Table is Table table && !IsSelected)
                {
                    if (table.SelectionMode == ListViewSelectionMode.Single && table.Selection.Items.Count != 0)
                        throw new InvalidOperationException();
                    Change(table.Selection.Items.Append(container.Content));
                }
            }

            public void RemoveFromSelection()
            {
                if (Table is Table table && IsSelected)
                    Change(table.Selection.Items.Where(item => !ReferenceEquals(item, container.Content)));
            }

            private void Change(IEnumerable<object> items)
            {
                if (!IsEnabled()) throw new ElementNotEnabledException();
                if (Table is Table table && container.Content is object item)
                {
                    if (table.SelectionMode == ListViewSelectionMode.None || table.CanInteract?.Invoke(item) == false)
                        return;
                    table.Selection = new Selection(items, item);
                }
            }
        }
    }
}
