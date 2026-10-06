using System.Collections;
using System.Collections.ObjectModel;
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
        public override string ToString() => Name;
    }

    [TestMethod]
    public Task SiblingSortingAndCollapseKeepCommandsOnVisibleRows() => TestHost.RunAsync(async () =>
    {
        Node first = new("a"), last = new("z");
        Node folder = new("folder", first, last);
        Node leaf = new("root");
        Column name = TestData.Column("name");
        Table table = TestData.Table(name);
        table.Schema<Node>().Key(row => row.Name)
            .Hierarchy(name, row => row.Children, row => row.IsExpanded, (row, value) => row.IsExpanded = value)
            .SortKey(name, row => row.Name, StringComparer.Ordinal);
        table.ItemsSource = new[] { folder, leaf };
        await TableHarness.LoadAsync(table);
        table.Sort = new(name, SortDirection.Descending);
        CollectionAssert.AreEqual(new[] { "root", "folder", "z", "a" }, Names(table));
        table.Selection = new(new object[] { first, leaf }, first);
        int changes = 0;
        table.SelectionChanged += (_, _) => changes++;

        var list = SelectionHarness.Descendant<ListView>(table)!;
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(list);
        var branch = peer.GetChildren().Single(child => child.GetName() == "folder");
        var expansion = (IExpandCollapseProvider)branch.GetPattern(PatternInterface.ExpandCollapse);
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
    public Task RefreshUsesCapturedChildrenAndRejectsInvalidHiddenMembership() => TestHost.RunAsync(async () =>
    {
        Node child = new("child"), root = new("root");
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
        table.Schema<Node>().Key(row => row.Name)
            .Hierarchy(name, Children, row => row.IsExpanded, (row, value) => row.IsExpanded = value);
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

        Node invalid = new("invalid", new("duplicate"), new("duplicate")) { IsExpanded = false };
        Expect.Throws<InvalidOperationException>(() => table.ItemsSource = new[] { invalid });
        CollectionAssert.AreEqual(new[] { "root", "child", "addition" }, Names(table));
    });

    [TestMethod]
    public Task RestoredLayoutCannotHideOrMoveHierarchyBehindOtherColumns() => TestHost.RunAsync(async () =>
    {
        Column other = TestData.Column("other"), name = TestData.Column("name");
        name.IsVisible = false;
        Table table = TestData.Table(other, name);
        table.Schema<Node>().Hierarchy(name, row => row.Children, row => row.IsExpanded,
            (row, value) => row.IsExpanded = value);
        table.ItemsSource = new[] { new Node("root") };
        await TableHarness.LoadAsync(table);
        table.Layout = TestData.Layout(order: new[] { "other", "name" },
            visibility: new Dictionary<string, bool> { ["name"] = false });
        CollectionAssert.AreEqual(new[] { "name", "other" }, table.Layout.Order.ToArray());
        Assert.IsFalse(table.Layout.Visibility.TryGetValue("name", out bool shown) && !shown);
        int changes = 0;
        table.LayoutChanged += (_, _) => changes++;
        table.ResetLayout();
        Assert.AreEqual(0, changes);
    });

    [TestMethod]
    public Task HierarchyRejectsRowReorderingEvenWhenEnabled() => TestHost.RunAsync(async () =>
    {
        Node child = new("child"), folder = new("folder", child), leaf = new("leaf");
        Column name = TestData.Column("name");
        Table table = TestData.Table(name);
        table.Schema<Node>().Hierarchy(name, row => row.Children, row => row.IsExpanded,
            (row, value) => row.IsExpanded = value);
        table.ItemsSource = new[] { folder, leaf };
        table.CanReorder = true;
        int requests = 0;
        table.ReorderRequested += (_, _) => requests++;
        await TableHarness.LoadAsync(table);

        Assert.IsFalse((bool)Proof.Call(table, "CanBeginRowDrag", folder)!);
        Assert.IsFalse((bool)Proof.Call(table, "CanBeginRowDrag", child)!);
        Proof.Call(table, "RequestReorder", new object[] { child }, 3);

        Assert.AreEqual(0, requests);
        CollectionAssert.AreEqual(new[] { "folder", "child", "leaf" }, Names(table));
    });

    private static string[] Names(Table table) =>
        ((IEnumerable)SelectionHarness.Descendant<ListView>(table)!.ItemsSource).Cast<Node>()
            .Select(row => row.Name).ToArray();
}
