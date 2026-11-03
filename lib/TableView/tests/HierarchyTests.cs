using System.Collections;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

[TestClass]
public class HierarchyTests
{
    private sealed class Node(string name, params Node[] children)
    {
        internal string Name { get; } = name;
        internal ObservableCollection<Node> Children { get; } = new(children);
        internal bool IsExpanded { get; set; } = true;
        internal bool HasUnrealizedChildren { get; set; }

        public override string ToString() => Name;
    }

    [TestMethod]
    public Task ANewRootArrayPublishesPlainChildrenChangedInPlace() =>
        TestHost.RunAsync(async () =>
        {
            Node root = new("root"), first = new("first"), added = new("added");
            List<Node> children = new() { first };
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table.Schema<Node>().Key(row => row.Name)
                .Hierarchy(name, row => ReferenceEquals(row, root) ? children : row.Children,
                    row => row.IsExpanded, (row, expanded) => row.IsExpanded = expanded);
            table.ItemsSource = new[] { root };
            await TableHarness.LoadAsync(table);

            children.Add(added);
            table.RefreshView();
            CollectionAssert.AreEqual(new[] { "root", "first" }, Names(table));
            table.ItemsSource = new[] { root };
            CollectionAssert.AreEqual(new[] { "root", "first", "added" }, Names(table));
        });

    /// <summary>
    /// A row the host gives a new child list, published with a new root array, shows that list.
    /// The accepted node once kept the old list, so the publish threw while it showed the old
    /// child, which had left.
    /// </summary>
    [TestMethod]
    public Task ANewRootArrayPublishesAReplacedChildList() =>
        TestHost.RunAsync(async () =>
        {
            Node root = new("root");
            IEnumerable<Node> children = new[] { new Node("old") };
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table.Schema<Node>().Key(row => row.Name)
                .Hierarchy(name, row => ReferenceEquals(row, root) ? children : row.Children,
                    row => row.IsExpanded, (row, expanded) => row.IsExpanded = expanded);
            table.ItemsSource = new[] { root };
            await TableHarness.LoadAsync(table);

            children = new[] { new Node("loaded") };
            table.ItemsSource = new[] { root };
            CollectionAssert.AreEqual(new[] { "root", "loaded" }, Names(table));
        });

    /// <summary>
    /// A rejected child change can leave a cycle among accepted rows in the host's lists. The
    /// next root assignment rejects it; it once walked the cycle without end on the UI thread.
    /// </summary>
    [TestMethod]
    public Task ACycleAmongAcceptedRowsIsRejected() =>
        TestHost.RunAsync(async () =>
        {
            Node b = new("b"), a = new("a", b), root = new("root", a);
            int reads = 0;
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table.Schema<Node>().Key(row => row.Name)
                .Hierarchy(name, row => ++reads > 100 ? throw new TimeoutException("The walk did not end.") : row.Children,
                    row => row.IsExpanded, (row, expanded) => row.IsExpanded = expanded);
            table.ItemsSource = new[] { root };
            await TableHarness.LoadAsync(table);

            Expect.Throws<InvalidOperationException>(() => b.Children.Add(a));
            Expect.Throws<InvalidOperationException>(() => table.ItemsSource = new[] { root });
            CollectionAssert.AreEqual(new[] { "root", "a", "b" }, Names(table));
        });

    /// <summary>
    /// A publish while the table is unloaded captures child lists that the table
    /// does not observe. After reload the table reads them again and observes
    /// them, so a change made while unloaded shows and a later child change
    /// applies to the list the host holds.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ChildChangesStayObservedAfterAPublishWhileUnloaded(bool notifyingRoot) =>
        TestHost.RunAsync(async () =>
        {
            Node root = new("root", new Node("first"));
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table.Schema<Node>().Key(row => row.Name)
                .Hierarchy(name, row => row.Children, row => row.IsExpanded, (row, expanded) => row.IsExpanded = expanded);
            IEnumerable<Node> Roots() => notifyingRoot ? new ObservableCollection<Node> { root } : new[] { root };
            table.ItemsSource = Roots();
            await TableHarness.LoadAsync(table);

            TestHost.RootPanel.Children.Remove(table);
            await TableHarness.WaitUntilAsync(() => Proof.Field<bool>(table, "_detached"), "the unload");
            table.ItemsSource = Roots();
            root.Children.Add(new Node("second"));
            await TableHarness.LoadAsync(table);
            CollectionAssert.AreEqual(new[] { "root", "first", "second" }, Names(table));

            root.Children.Add(new Node("third"));
            CollectionAssert.AreEqual(new[] { "root", "first", "second", "third" }, Names(table));
            root.Children.RemoveAt(0);
            CollectionAssert.AreEqual(new[] { "root", "second", "third" }, Names(table));
        });

    [TestMethod]
    public Task RejectedChildChangesKeepTheAcceptedTreeAndRecoverBeforeAnotherDelta() =>
        TestHost.RunAsync(async () =>
        {
            Node root = new("root", new Node("first")), outside = new("outside");
            int outsideReads = 0;
            bool fail = false;
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table.Schema<Node>().Key(row => row.Name)
                .Hierarchy(name, row =>
                {
                    outsideReads += ReferenceEquals(row, outside) ? 1 : 0;
                    return row.Children;
                }, row => row.IsExpanded, (row, expanded) => row.IsExpanded = expanded)
                .SortKey(name, row => row.Name, Comparer<string>.Create((left, right) =>
                    fail ? throw new InvalidOperationException("comparison") : StringComparer.Ordinal.Compare(left, right)));
            ObservableCollection<Node> roots = new() { root, outside };
            table.ItemsSource = roots;
            await TableHarness.LoadAsync(table);
            table.Sort = new(name);
            outsideReads = 0;

            Node added = new("second");
            root.Children.Add(added);
            Assert.AreEqual(0, outsideReads);
            CollectionAssert.AreEqual(new[] { "outside", "root", "first", "second" }, Names(table));

            Node duplicate = new("outside");
            Expect.Throws<InvalidOperationException>(() => root.Children.Add(duplicate));
            CollectionAssert.AreEqual(new[] { "outside", "root", "first", "second" }, Names(table));
            root.Children.Remove(duplicate);
            CollectionAssert.AreEqual(new[] { "outside", "root", "first", "second" }, Names(table));

            Node rejected = new("third");
            fail = true;
            Expect.Throws<InvalidOperationException>(() => root.Children.Add(rejected));
            CollectionAssert.AreEqual(new[] { "outside", "root", "first", "second" }, Names(table));
            fail = false;
            root.Children.Remove(rejected);
            CollectionAssert.AreEqual(new[] { "outside", "root", "first", "second" }, Names(table));

            Node rejectedRoot = new("outside"), observed = new("fourth");
            Expect.Throws<InvalidOperationException>(() => roots.Add(rejectedRoot));
            Expect.Throws<InvalidOperationException>(() => root.Children.Add(observed));
            CollectionAssert.AreEqual(new[] { "outside", "root", "first", "second" }, Names(table));
            roots.Remove(rejectedRoot);
            CollectionAssert.AreEqual(new[] { "outside", "root", "first", "fourth", "second" }, Names(table));
        });

    [TestMethod]
    public Task SettlingUsesVisibleMembershipWhenExpansionExchangesEqualCounts() =>
        TestHost.RunAsync(async () =>
        {
            Node first = new("a", new Node("c"));
            Node second = new("b", new Node("d")) { IsExpanded = false };
            bool reverse = false;
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table.Schema<Node>().Key(row => row.Name)
                .Hierarchy(name, row => row.Children, row => row.IsExpanded, (row, value) => row.IsExpanded = value)
                .SortKey(name, row => reverse ? -(int)row.Name[0] : row.Name[0]);
            table.SortInterval = TimeSpan.FromHours(1);
            table.ItemsSource = new[] { first, second };
            await TableHarness.LoadAsync(table);
            table.Sort = new(name);
            CollectionAssert.AreEqual(new[] { "a", "c", "b" }, Names(table));
            StartInterval(table);

            first.IsExpanded = false;
            second.IsExpanded = true;
            table.RefreshView();
            CollectionAssert.AreEqual(new[] { "a", "b", "d" }, Names(table));

            reverse = true;
            table.RefreshView();
            CollectionAssert.AreEqual(new[] { "a", "b", "d" }, Names(table));
        });

    /// <summary>
    /// Inside the interval a sibling group keeps the order it was last shown in. That kept order
    /// is no longer valid once the group stops being shown, so a parent collapsed and expanded
    /// again shows its children sorted by their current values.
    /// </summary>
    [TestMethod]
    public Task ExpandingAgainSortsTheChildrenByTheirCurrentValues() =>
        TestHost.RunAsync(async () =>
        {
            Node parent = new("p", new Node("a"), new Node("b"), new Node("c"));
            Dictionary<string, int> ranks = new() { ["p"] = 0, ["a"] = 1, ["b"] = 2, ["c"] = 3 };
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table.Schema<Node>().Key(row => row.Name)
                .Hierarchy(name, row => row.Children, row => row.IsExpanded, (row, value) => row.IsExpanded = value)
                .SortKey(name, row => ranks[row.Name]);
            table.SortInterval = TimeSpan.FromHours(1);
            table.ItemsSource = new[] { parent };
            await TableHarness.LoadAsync(table);
            table.Sort = new(name);
            CollectionAssert.AreEqual(new[] { "p", "a", "b", "c" }, Names(table));
            StartInterval(table);

            ranks["a"] = 9;
            table.RefreshView();
            CollectionAssert.AreEqual(new[] { "p", "a", "b", "c" }, Names(table), "the shown group keeps its order");

            parent.IsExpanded = false;
            table.RefreshView();
            parent.IsExpanded = true;
            table.RefreshView();
            CollectionAssert.AreEqual(new[] { "p", "b", "c", "a" }, Names(table));
        });

    [TestMethod]
    public Task RejectedRootsDoNotRecaptureAcceptedOneShotChildren() =>
        TestHost.RunAsync(async () =>
        {
            Node child = new("child"), root = new("root");
            int reads = 0;
            IEnumerable<Node> Children()
            {
                if (++reads > 1)
                {
                    throw new InvalidOperationException("Children were enumerated again");
                }
                yield return child;
            }
            IEnumerable<Node> children = Children();
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table.Schema<Node>().Key(row => row.Name)
                .Hierarchy(name, row => ReferenceEquals(row, root) ? children : row.Children,
                    row => row.IsExpanded, (row, value) => row.IsExpanded = value);
            table.ItemsSource = new[] { root };
            await TableHarness.LoadAsync(table);

            Expect.Throws<InvalidOperationException>(() => table.ItemsSource = new[] { new Node("invalid"), new Node("invalid") });
            table.RefreshView();

            CollectionAssert.AreEqual(new[] { "root", "child" }, Names(table));
            Assert.AreEqual(1, reads);
        });

    [TestMethod]
    public Task SiblingSortingAndCollapseKeepCommandsOnVisibleRows() =>
        TestHost.RunAsync(async () =>
        {
            Node first = new("a"),
                last = new("z");
            Node folder = new("folder", first, last);
            Node leaf = new("root");
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table
                .Schema<Node>()
                .Key(row => row.Name)
                .Hierarchy(
                    name,
                    row => row.Children,
                    row => row.IsExpanded,
                    (row, value) => row.IsExpanded = value
                )
                .SortKey(name, row => row.Name, StringComparer.Ordinal);
            table.ItemsSource = new[] { folder, leaf };
            await TableHarness.LoadAsync(table);
            table.Sort = new(name, SortDirection.Descending);
            CollectionAssert.AreEqual(new[] { "root", "folder", "z", "a" }, Names(table));
            table.Selection = new(new object[] { first, leaf }, first);
            int changes = 0;
            table.SelectionChanged += (_, _) => changes++;

            table.UpdateLayout();
            var list = SelectionHarness.Descendant<ListView>(table)!;
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(list);
            var branch = peer.GetChildren().Single(child => child.GetName() == "folder");
            var expansion = (IExpandCollapseProvider)
                branch.GetPattern(PatternInterface.ExpandCollapse);
            expansion.Collapse();

            CollectionAssert.AreEqual(new[] { "root", "folder" }, Names(table));
            CollectionAssert.AreEqual(new object[] { leaf }, table.Selection.Items.ToArray());
            Assert.AreSame(folder, table.Selection.Current);
            Assert.AreEqual(1, changes);
            expansion.Expand();
            CollectionAssert.AreEqual(new[] { "root", "folder", "z", "a" }, Names(table));
            CollectionAssert.AreEqual(new object[] { leaf }, table.Selection.Items.ToArray());
            Assert.AreEqual(1, branch.GetLevel());
            Assert.AreEqual(2, branch.GetPositionInSet());
            Assert.AreEqual(2, branch.GetSizeOfSet());
        });

    [TestMethod]
    public Task UnrealizedChildrenExpandOnRequestOnlyAndAppearWhenSupplied() =>
        TestHost.RunAsync(async () =>
        {
            Node folder = new("folder") { IsExpanded = false, HasUnrealizedChildren = true },
                leaf = new("leaf");
            List<bool> requests = new();
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table
                .Schema<Node>()
                .Key(row => row.Name)
                .Hierarchy(
                    name,
                    row => row.Children,
                    row => row.IsExpanded,
                    (row, value) =>
                    {
                        row.IsExpanded = value;
                        requests.Add(value);
                    },
                    row => row.HasUnrealizedChildren
                );
            table.ItemsSource = new[] { folder, leaf };
            await TableHarness.LoadAsync(table);
            table.UpdateLayout();
            var list = SelectionHarness.Descendant<ListView>(table)!;
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(list);
            var rows = peer.GetChildren();
            var branch = (IExpandCollapseProvider)
                rows.Single(row => row.GetName() == "folder")
                    .GetPattern(PatternInterface.ExpandCollapse);
            var file = (IExpandCollapseProvider)
                rows.Single(row => row.GetName() == "leaf")
                    .GetPattern(PatternInterface.ExpandCollapse);
            Assert.AreEqual(ExpandCollapseState.Collapsed, branch.ExpandCollapseState);
            Assert.AreEqual(ExpandCollapseState.LeafNode, file.ExpandCollapseState);

            folder.IsExpanded = true;
            table.RefreshView();
            folder.IsExpanded = false;
            table.RefreshView();
            Assert.AreEqual(0, requests.Count);

            branch.Expand();
            CollectionAssert.AreEqual(new[] { true }, requests);
            Assert.AreEqual(ExpandCollapseState.Expanded, branch.ExpandCollapseState);
            CollectionAssert.AreEqual(new[] { "folder", "leaf" }, Names(table));

            folder.Children.Add(new("child"));
            folder.HasUnrealizedChildren = false;
            CollectionAssert.AreEqual(new[] { "folder", "child", "leaf" }, Names(table));
        });

    [TestMethod]
    public Task RefreshUsesCapturedChildrenAndRejectsInvalidHiddenMembership() =>
        TestHost.RunAsync(async () =>
        {
            Node child = new("child"),
                root = new("root");
            root.Children.Add(child);
            root.IsExpanded = false;
            int reads = 0;
            IEnumerable<Node> Children(Node node)
            {
                reads++;
                return node.Children;
            }
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table
                .Schema<Node>()
                .Key(row => row.Name)
                .Hierarchy(
                    name,
                    Children,
                    row => row.IsExpanded,
                    (row, value) => row.IsExpanded = value
                );
            table.ItemsSource = new[] { root };
            await TableHarness.LoadAsync(table);
            int captured = reads;
            root.IsExpanded = true;
            table.RefreshView();
            root.IsExpanded = false;
            table.RefreshView();
            Assert.AreEqual(captured, reads);

            Node addition = new("addition");
            root.Children.Add(addition);
            CollectionAssert.AreEqual(new[] { "root" }, Names(table));
            root.IsExpanded = true;
            table.RefreshView();
            CollectionAssert.AreEqual(new[] { "root", "child", "addition" }, Names(table));

            Node invalid = new("invalid", new("duplicate"), new("duplicate"))
            {
                IsExpanded = false,
            };
            Expect.Throws<InvalidOperationException>(() => table.ItemsSource = new[] { invalid });
            CollectionAssert.AreEqual(new[] { "root", "child", "addition" }, Names(table));
        });

    [TestMethod]
    public Task RestoredLayoutCannotHideOrMoveHierarchyBehindOtherColumns() =>
        TestHost.RunAsync(async () =>
        {
            Column other = TestData.Column("other"),
                name = TestData.Column("name");
            name.IsVisible = false;
            Table table = TestData.Table(other, name);
            table
                .Schema<Node>()
                .Hierarchy(
                    name,
                    row => row.Children,
                    row => row.IsExpanded,
                    (row, value) => row.IsExpanded = value
                );
            table.ItemsSource = new[] { new Node("root") };
            await TableHarness.LoadAsync(table);
            table.Layout = TestData.Layout(
                order: new[] { "other", "name" },
                visibility: new Dictionary<string, bool> { ["name"] = false }
            );
            CollectionAssert.AreEqual(new[] { "name", "other" }, table.Layout.Order.ToArray());
            Assert.IsFalse(
                table.Layout.VisibilityOverrides.TryGetValue("name", out bool shown) && !shown
            );
        });

    /// <summary>
    /// A child list under a collapsed row changes no shown row, so the view stays as it is, but
    /// the row's expander follows whether it has children: a person could not open it otherwise.
    /// </summary>
    [TestMethod]
    public Task AChildChangeUnderACollapsedRowUpdatesOnlyItsExpander() =>
        TestHost.RunAsync(async () =>
        {
            Node folder = new("folder") { IsExpanded = false }, leaf = new("leaf");
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table.Schema<Node>().Key(row => row.Name)
                .Hierarchy(name, row => row.Children, row => row.IsExpanded, (row, value) => row.IsExpanded = value);
            table.ItemsSource = new[] { folder, leaf };
            await TableHarness.LoadAsync(table);
            object hierarchy = Proof.Field<object>(table, "_hierarchy");
            bool HasChildren() => (bool)hierarchy.GetType()
                .GetProperty("HasChildren", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!.GetValue(hierarchy)!;
            Assert.IsFalse(HasChildren());

            folder.Children.Add(new("child"));
            CollectionAssert.AreEqual(new[] { "folder", "leaf" }, Names(table));
            Assert.IsTrue(HasChildren(), "the collapsed row shows an expander");

            folder.Children.RemoveAt(0);
            CollectionAssert.AreEqual(new[] { "folder", "leaf" }, Names(table));
            Assert.IsFalse(HasChildren(), "the row without children shows none");

            folder.Children.Add(new("again"));
            folder.IsExpanded = true;
            table.RefreshView();
            CollectionAssert.AreEqual(new[] { "folder", "again", "leaf" }, Names(table));
        });

    [TestMethod]
    public Task HierarchyRejectsRowReorderingEvenWhenEnabled() =>
        TestHost.RunAsync(async () =>
        {
            Node child = new("child"),
                folder = new("folder", child),
                leaf = new("leaf");
            Column name = TestData.Column("name");
            Table table = TestData.Table(name);
            table
                .Schema<Node>()
                .Order(
                    name,
                    row =>
                        row == folder ? 1
                        : row == child ? 2
                        : 3,
                    () => new[] { folder, child, leaf }
                )
                .Hierarchy(
                    name,
                    row => row.Children,
                    row => row.IsExpanded,
                    (row, value) => row.IsExpanded = value
                );
            table.ItemsSource = new[] { folder, leaf };
            table.CanReorder = true;
            await TableHarness.LoadAsync(table);

            Assert.IsFalse((bool)Proof.Call(table, "CanStartDrag", folder)!);
            Assert.IsFalse((bool)Proof.Call(table, "CanStartDrag", child)!);
            CollectionAssert.AreEqual(new[] { "folder", "child", "leaf" }, Names(table));
        });

    private static string[] Names(Table table) =>
        ((IEnumerable)SelectionHarness.Descendant<ListView>(table)!.ItemsSource)
            .Cast<Node>()
            .Select(row => row.Name)
            .ToArray();

    /// <summary>Start the settle interval now, as a frame that shows a settle does.</summary>
    private static void StartInterval(Table table)
    {
        Proof.SetField(table, "_orderSettledAt", System.Diagnostics.Stopwatch.GetTimestamp());
        Proof.SetField(table, "_unshownSettle", null);
    }
}
