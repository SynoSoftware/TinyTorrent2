using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation.Collections;

namespace Syno.TableView.Tests;

[TestClass]
public class AutomationTests
{
    [TestMethod]
    public Task ReplacementPeerResolvesDuringTheVectorChangeAfterAnEarlierRemoval() =>
        TestHost.RunAsync(async () =>
        {
            var h = await SelectionHarness.LoadAsync(2, table =>
                table.Schema<Row>().Hierarchy(table.Columns[0], _ => Array.Empty<Row>(),
                    _ => false, (_, _) => { }));
            h.Click(h[1]);
            Row replacement = new(h[1].Key);
            var parent = (ItemsControlAutomationPeer)
                FrameworkElementAutomationPeer.CreatePeerForElement(h.Surface());
            var vector = (IObservableVector<object>)h.Surface().ItemsSource;
            int changes = 0;
            VectorChangedEventHandler<object> check = (_, args) =>
            {
                if (args.CollectionChange != CollectionChange.ItemChanged)
                {
                    return;
                }
                changes++;
                var peer = parent.CreateItemAutomationPeer(replacement);
                Assert.IsTrue(peer.IsEnabled());
            };
            vector.VectorChanged += check;
            try
            {
                h.Table.ItemsSource = new[] { replacement };
            }
            finally
            {
                vector.VectorChanged -= check;
            }

            Assert.AreEqual(1, changes);
            Assert.AreSame(replacement, h.Table.Selection.Items.Single());
        });

    [TestMethod]
    public Task UnrealizedItemSelectionReachesTheTableAndSurvivesReplacement() =>
        TestHost.RunAsync(async () =>
        {
            var h = await SelectionHarness.LoadAsync(300);
            var list = h.Surface();
            Assert.IsNull(list.ContainerFromItem(h[299]));
            var parent = (ItemsControlAutomationPeer)
                FrameworkElementAutomationPeer.CreatePeerForElement(list);
            var peer = parent.CreateItemAutomationPeer(h[299]);
            var selection =
                peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider;
            Assert.IsNotNull(selection);

            selection.Select();
            CollectionAssert.AreEqual(new[] { "k299" }, h.SelectedKeys());
            Assert.AreSame(h[299], h.Table.Selection.Current);
            Assert.IsTrue(selection.IsSelected);

            h.Rows[299] = new Row("k299");
            h.Table.UpdateLayout();
            selection.RemoveFromSelection();
            Assert.AreEqual(0, h.Table.Selection.Items.Count);
            selection.AddToSelection();
            Assert.AreSame(h[299], h.Table.Selection.Items.Single());
        });

    [TestMethod]
    public Task SingleSelectionAutomationReportsAndEnforcesTheSameMode() =>
        TestHost.RunAsync(async () =>
        {
            var h = await SelectionHarness.LoadAsync(
                3,
                table => table.SelectionMode = ListViewSelectionMode.Single
            );
            var parent = (ItemsControlAutomationPeer)
                FrameworkElementAutomationPeer.CreatePeerForElement(h.Surface());
            var container = parent.GetPattern(PatternInterface.Selection) as ISelectionProvider;
            Assert.IsNotNull(container);
            Assert.IsFalse(container.CanSelectMultiple);
            var first = (ISelectionItemProvider)
                parent.CreateItemAutomationPeer(h[0]).GetPattern(PatternInterface.SelectionItem);
            var second = (ISelectionItemProvider)
                parent.CreateItemAutomationPeer(h[1]).GetPattern(PatternInterface.SelectionItem);

            first.Select();
            Assert.ThrowsExactly<InvalidOperationException>(() => second.AddToSelection());
            CollectionAssert.AreEqual(new[] { "k0" }, h.SelectedKeys());
            second.Select();
            CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys());
        });

    [TestMethod]
    public Task DisplayOnlyTableExposesNoSelectionPatterns() =>
        TestHost.RunAsync(async () =>
        {
            var h = await SelectionHarness.LoadAsync(
                3,
                table => table.SelectionMode = ListViewSelectionMode.None
            );
            var parent = (ItemsControlAutomationPeer)
                FrameworkElementAutomationPeer.CreatePeerForElement(h.Surface());
            Assert.IsNull(parent.GetPattern(PatternInterface.Selection));
            Assert.IsNull(
                parent.CreateItemAutomationPeer(h[0]).GetPattern(PatternInterface.SelectionItem)
            );
        });

    [TestMethod]
    public Task CachedAutomationSelectionCannotSelectAnUnavailableRow() =>
        TestHost.RunAsync(async () =>
        {
            var h = await SelectionHarness.LoadAsync(
                3,
                table => table.Schema<Row>().CanInteract(row => row.Interactive)
            );
            var parent = (ItemsControlAutomationPeer)
                FrameworkElementAutomationPeer.CreatePeerForElement(h.Surface());
            var peer = parent.CreateItemAutomationPeer(h[0]);
            var selection = (ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem);
            h[0].Interactive = false;
            h.Table.RefreshView();

            Assert.IsFalse(peer.IsEnabled());
            Assert.IsNull(peer.GetPattern(PatternInterface.SelectionItem));
            Assert.ThrowsExactly<ElementNotEnabledException>(() => selection.Select());
            Assert.AreEqual(0, h.Table.Selection.Items.Count);
        });
}
