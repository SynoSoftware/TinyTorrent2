using Microsoft.UI.Xaml;
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
            int replaced = 0;
            VectorChangedEventHandler<object> check = (_, args) =>
            {
                changes++;
                if (args.CollectionChange == CollectionChange.ItemChanged)
                {
                    replaced++;
                }
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

            Assert.AreEqual(1, changes, "only the first row's removal");
            Assert.AreEqual(0, replaced, "a new instance keeps its row's container");
            h.Table.UpdateLayout();
            Assert.AreSame(replacement, ((ListViewItem)h.Surface().ContainerFromIndex(0)).Content);
            Assert.IsTrue(parent.CreateItemAutomationPeer(replacement).GetBoundingRectangle().Height > 0,
                "the new instance's peer finds the row's container");
            Assert.AreSame(replacement, h.Table.Selection.Items.Single());
        });

    /// <summary>
    /// The list fills a container from the instance it recorded when it prepared it: after its
    /// preparation event, or, for a placeholder shown while scrolling, in a later pass. A new
    /// instance of that row published in between, here from the preparation event itself, must
    /// still be what the row shows.
    /// </summary>
    [TestMethod]
    public Task AContainerFilledAfterAPublishShowsTheCurrentInstance() =>
        TestHost.RunAsync(async () =>
        {
            var h = await SelectionHarness.LoadAsync(300);
            ListView list = h.Surface();
            Row? replacement = null;
            Row? prepared = null;
            List<object?> contents = new();
            Windows.Foundation.TypedEventHandler<ListViewBase, ContainerContentChangingEventArgs> publish =
                (_, args) =>
                {
                    if (args.InRecycleQueue || replacement is not null
                        || ReferenceEquals(((ContentControl)args.ItemContainer).Content, args.Item))
                    {
                        return;
                    }
                    var container = (ContentControl)args.ItemContainer;
                    container.RegisterPropertyChangedCallback(ContentControl.ContentProperty,
                        (_, _) => contents.Add(container.Content));
                    prepared = (Row)args.Item;
                    replacement = new Row(prepared.Key);
                    h.Rows[h.Rows.IndexOf(prepared)] = replacement;
                };
            list.ContainerContentChanging += publish;
            try
            {
                ScrollViewer viewer = SelectionHarness.Descendant<ScrollViewer>(list)!;
                for (int step = 1; step <= 20 && replacement is null; step++)
                {
                    viewer.ChangeView(null, step * 300, null, disableAnimation: true);
                    list.UpdateLayout();
                    await Task.Delay(30);
                }
                await Task.Delay(500);
            }
            finally
            {
                list.ContainerContentChanging -= publish;
            }
            if (replacement is null)
            {
                Assert.Inconclusive("the list prepared no container before filling it");
            }
            string sequence = string.Join(", ", contents.Select(content =>
                ReferenceEquals(content, prepared) ? "prepared"
                : ReferenceEquals(content, replacement) ? "replacement"
                : content?.ToString() ?? "null"));
            int filled = contents.IndexOf(prepared);
            Assert.IsTrue(filled >= 0,
                $"the list fills the placeholder with the instance it prepared it for: [{sequence}]");
            Assert.IsTrue(contents.Skip(filled).Contains(replacement),
                $"the table puts the current instance back: [{sequence}]");

            var shown = (ContentControl?)list.ContainerFromIndex(h.Rows.IndexOf(replacement));
            Assert.IsNotNull(shown, "the replaced row stays realized");
            Assert.AreSame(replacement, shown.Content);
            Assert.AreSame(replacement, ((FrameworkElement)shown.ContentTemplateRoot).DataContext);
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
