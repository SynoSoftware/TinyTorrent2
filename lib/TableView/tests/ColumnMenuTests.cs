using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// Section 12's generated header menu. The rules it exists for are the two invariants: a command is
/// enabled exactly when it can change the layout, and no sequence of its commands can leave the
/// table with nothing visible.
/// </summary>
[TestClass]
public class ColumnMenuTests
{
    // The three commands that act on one column name it, so their labels are built rather than
    // fixed: "this column" said nothing once the column list moved into the same menu, where every
    // other entry names itself.
    private static string Hide(string name) => $"Hide column “{name}”";

    private static string Show(string name) => $"Show column “{name}”";

    private static string FitThis(string name) => $"Fit column “{name}”";

    private const string FitVisible = "Fit columns";
    private const string FillVisible = "Fill width";
    private const string MoveLeft = "Move left";
    private const string MoveRight = "Move right";

    // ------------------------------------------------------------------ what the menu contains

    /// <summary>
    /// Each command runs the operation its name promises. Fit columns and Fill width are told apart
    /// by where the columns end: a fit leaves them at their content, a fill takes them to the edge of
    /// the strip.
    /// </summary>
    [TestMethod]
    public Task EachCommandInTheMenuOverAHeaderDoesItsOwnOperation() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "c");
            List<LayoutChange> reported = new();
            table.LayoutChanged += (_, kind) => reported.Add(kind);
            double edge = Strip(table).ActualWidth;
            await OpenMenuAsync(table, visibleIndex: 1);

            // The columns start at the declared 150 DIPs and the table has no rows, so a fit finds only
            // the header label, which is narrower.
            await InvokeAsync(table, FitThis("B"));

            Assert.IsTrue(TableHarness.EffectiveWidth(table, "b") < 150, "b was fitted");
            Assert.AreEqual(150d, TableHarness.EffectiveWidth(table, "a"), 0d, "and only b");
            Assert.AreEqual(150d, TableHarness.EffectiveWidth(table, "c"), 0d);
            AssertReported(reported, LayoutChange.Fit);

            await InvokeAsync(table, FitVisible);

            Assert.IsTrue(TableHarness.EffectiveWidth(table, "a") < 150, "a was fitted");
            Assert.IsTrue(TableHarness.EffectiveWidth(table, "c") < 150, "and c");
            Assert.IsTrue(TableHarness.TotalWidth(table) < edge - 1, "a fit leaves the edge empty");
            AssertReported(reported, LayoutChange.Fit);

            await InvokeAsync(table, FillVisible);

            Assert.AreEqual(
                edge,
                TableHarness.TotalWidth(table),
                0.5,
                "a fill takes the columns to the edge"
            );
            AssertReported(reported, LayoutChange.Fit);

            await InvokeAsync(table, MoveLeft);

            CollectionAssert.AreEqual(new[] { "b", "a", "c" }, TableHarness.Order(table));
            AssertReported(reported, LayoutChange.Move);

            await InvokeAsync(table, MoveRight);

            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(table));
            AssertReported(reported, LayoutChange.Move);

            await InvokeAsync(table, Hide("B"));

            CollectionAssert.AreEqual(new[] { "a", "c" }, VisibleIds(table));
            AssertReported(reported, LayoutChange.Visibility);
        });

    [TestMethod]
    public Task UnusedHeaderSpaceGetsTheColumnListWithoutPerColumnCommands() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b");

            string[] labels = Labels(Menu(table, null));

            foreach (
                string perColumn in new[]
                {
                    Hide("A"),
                    Hide("B"),
                    FitThis("A"),
                    FitThis("B"),
                    MoveLeft,
                    MoveRight,
                }
            )
            {
                CollectionAssert.DoesNotContain(labels, perColumn, "there is no column to act on");
            }

            List<LayoutChange> reported = new();
            table.LayoutChanged += (_, kind) => reported.Add(kind);
            await OpenMenuAsync(table, visibleIndex: null);

            await InvokeAsync(table, "A");

            Assert.IsFalse(TableHarness.IsVisible(table, "a"), "the entry for a toggled a");
            Assert.IsTrue(TableHarness.IsVisible(table, "b"));
            AssertReported(reported, LayoutChange.Visibility);
        });

    [TestMethod]
    public Task TheColumnListNamesEveryColumnIncludingTheHiddenOnes() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "c");
            SetVisibility(table, "b", false);

            MenuFlyoutItem[] toggles = ColumnEntries(Menu(table, "a"));

            CollectionAssert.AreEqual(
                new[] { "A", "B", "C" },
                toggles.Select(t => t.Text).ToArray(),
                "the host's DisplayName names each column, in the effective order"
            );
            CollectionAssert.AreEqual(
                new[] { true, false, true },
                toggles.Select(t => t.Icon is not null).ToArray(),
                "a shown column carries the check icon"
            );
        });

    // ------------------------------------------------------------ enabled only when it can change

    [TestMethod]
    public Task MoveLeftAndMoveRightAreEnabledOnlyWhereThereIsSomewhereToMove() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "c");

            Assert.IsFalse(Item(Menu(table, "a"), MoveLeft).IsEnabled, "a is already first");
            Assert.IsTrue(Item(Menu(table, "a"), MoveRight).IsEnabled);
            Assert.IsTrue(Item(Menu(table, "c"), MoveLeft).IsEnabled);
            Assert.IsFalse(Item(Menu(table, "c"), MoveRight).IsEnabled, "c is already last");
        });

    [TestMethod]
    public Task AColumnTheHostFixedOffersNoSizingCommand() =>
        TestHost.RunAsync(async () =>
        {
            Column fixedWidth = TestData.Column("a");
            fixedWidth.CanResize = false;

            Table table = await LoadAsync(fixedWidth, TestData.Column("b"));
            MenuFlyout menu = Menu(table, "a");

            Assert.IsFalse(Item(menu, FitThis("A")).IsEnabled);
            Assert.IsTrue(Item(menu, FitVisible).IsEnabled, "b can still be fitted");
        });

    [TestMethod]
    public Task FitVisibleColumnsIsDisabledWhenNoVisibleColumnCanBeResized() =>
        TestHost.RunAsync(async () =>
        {
            Column first = TestData.Column("a");
            Column second = TestData.Column("b");
            first.CanResize = false;
            second.CanResize = false;

            Table table = await LoadAsync(first, second);

            Assert.IsFalse(Item(Menu(table, "a"), FitVisible).IsEnabled);
        });

    [TestMethod]
    public Task AColumnTheHostRequiresCannotBeHidden() =>
        TestHost.RunAsync(async () =>
        {
            Column required = TestData.Column("a");
            required.CanHide = false;

            Table table = await LoadAsync(required, TestData.Column("b"));

            Assert.IsFalse(Item(Menu(table, "a"), Hide("A")).IsEnabled);
            Assert.IsFalse(Toggle(Menu(table, "a"), "A").IsEnabled);
            Assert.IsTrue(Item(Menu(table, "b"), Hide("B")).IsEnabled);
        });

    // ------------------------------------------------------------------ the zero-column invariant

    [TestMethod]
    public Task TheLastVisibleColumnCannotBeHidden() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b");
            SetVisibility(table, "b", false);

            MenuFlyout menu = Menu(table, "a");
            Assert.IsFalse(Item(menu, Hide("A")).IsEnabled, "hiding a would leave nothing visible");
            Assert.IsFalse(
                Toggle(menu, "A").IsEnabled,
                "and its toggle cannot be unchecked either"
            );
            Assert.IsTrue(Toggle(menu, "B").IsEnabled, "the hidden column can still come back");

            Func<int> events = LayoutChanges(table, LayoutChange.Visibility);
            SetVisibility(table, "a", false);

            Assert.IsTrue(
                TableHarness.IsVisible(table, "a"),
                "the operation itself refuses it too"
            );
            Assert.AreEqual(0, events());
        });

    // ------------------------------------------------------------------ what the commands do

    [TestMethod]
    public Task ShowingAColumnAgainKeepsItsPlaceAndItsWidth() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "c");
            table.Layout = TestData.Layout(widths: new Dictionary<string, double> { ["b"] = 220 });
            Func<int> events = LayoutChanges(table, LayoutChange.Visibility);

            SetVisibility(table, "b", false);

            Assert.AreEqual(
                220,
                TableHarness.EffectiveWidth(table, "b"),
                "a hidden column keeps its width"
            );
            CollectionAssert.AreEqual(new[] { "a", "c" }, VisibleIds(table));

            SetVisibility(table, "b", true);

            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, VisibleIds(table));
            Assert.AreEqual(220, TableHarness.EffectiveWidth(table, "b"));
            Assert.AreEqual(
                2,
                events(),
                "one notification for each completed change, and none for a no-op"
            );

            SetVisibility(table, "b", true);
            Assert.AreEqual(2, events());
        });

    /// <summary>
    /// The menu's move must be the drag's move, not a second implementation of it. The hidden
    /// column is what tells them apart: a move is chosen at a visible boundary and applied to the
    /// full order.
    /// </summary>
    [TestMethod]
    public Task MoveLeftFromTheMenuLandsWhereTheDragWouldLandIt() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "hidden", "c");
            SetVisibility(table, "hidden", false);
            table.UpdateLayout();

            await OpenMenuAsync(table, visibleIndex: 2);
            await InvokeAsync(table, MoveLeft);

            CollectionAssert.AreEqual(
                new[] { "a", "c", "b", "hidden" },
                TableHarness.Order(table),
                "the same order the equivalent drop produces"
            );
        });

    // ------------------------------------------------------------------ opening and closing

    /// <summary>
    /// The whole path a pointer or the Menu key takes, minus the platform event that starts it:
    /// the invoking header takes focus, the menu opens on it, invoking an item runs the operation,
    /// and the closure puts focus back on that header.
    /// </summary>
    [TestMethod]
    public Task TheMenuOpensOnTheInvokingHeaderRunsItsCommandAndStaysOpen() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b");
            await OpenMenuAsync(table, visibleIndex: 1);

            await InvokeAsync(table, MoveLeft);

            CollectionAssert.AreEqual(
                new[] { "b", "a" },
                TableHarness.Order(table),
                "the item ran the operation"
            );

            // Nothing in this menu closes it. Every item is repeated by nature — nudging a column
            // left until it sits where it should, showing one column and then another — or is
            // immediately worth undoing, which comes to the same thing. Closing after each one made
            // three columns cost three trips back through the header.
            Assert.AreEqual(1, OpenPopups(table).Count, "and the flyout stayed open behind it");

            // Which is why the item cannot be left saying what it said before it ran: "b" is now
            // first, so the command that moved it there has nowhere left to go.
            Assert.IsFalse(
                OpenItem(table, MoveLeft).IsEnabled,
                "and the item re-asked whether it is still legal"
            );
        });

    // ------------------------------------------------------------------ helpers

    private static Task<Table> LoadAsync(params string[] ids) =>
        LoadAsync(ids.Select(id => TestData.Column(id)).ToArray());

    /// <summary>A table that keeps its declared widths instead of filling its width.</summary>
    private static async Task<Table> LoadAsync(params Column[] columns)
    {
        Table table = TestData.Table(columns);
        table.Width = 700;
        table.Height = 200;
        table.Layout = TestData.DeclaredWidths(table);

        await TableHarness.LoadAsync(table);
        table.UpdateLayout();
        return table;
    }

    private static string[] Labels(MenuFlyout menu) =>
        menu
            .Items.Select(item =>
                item switch
                {
                    MenuFlyoutItem command => command.Text,
                    MenuFlyoutSubItem submenu => submenu.Text,
                    _ => "-",
                }
            )
            .ToArray();

    private static MenuFlyoutItem Item(MenuFlyout menu, string text) =>
        menu.Items.OfType<MenuFlyoutItem>().SingleOrDefault(item => item.Text == text)
        ?? throw new AssertFailedException(
            $"The menu has no '{text}' item: {string.Join(", ", Labels(menu))}"
        );

    /// <summary>
    /// The column entries: plain items sitting after the last command, told apart from the
    /// commands by name rather than by type, because they are the same type now.
    /// </summary>
    private static MenuFlyoutItem[] ColumnEntries(MenuFlyout menu)
    {
        // Everything after the last separator. They can no longer be told from the commands by
        // name, because three of the commands now carry a column's name themselves.
        int lastSeparator = menu.Items.ToList().FindLastIndex(item => item is MenuFlyoutSeparator);
        return menu.Items.Skip(lastSeparator + 1).OfType<MenuFlyoutItem>().ToArray();
    }

    private static MenuFlyoutItem Toggle(MenuFlyout menu, string displayName) =>
        ColumnEntries(menu).Single(item => item.Text == displayName);

    private static string[] VisibleIds(Table table) =>
        TableHarness.VisibleColumns(table).Select(column => column.Id).ToArray();

    /// <summary>Starts counting <c>LayoutChanged</c> now; call the result to read the count.</summary>
    private static Func<int> LayoutChanges(Table table, LayoutChange expected)
    {
        int count = 0;
        table.LayoutChanged += (_, kind) =>
        {
            Assert.AreEqual(expected, kind);
            count++;
        };
        return () => count;
    }

    private static Header.Strip Strip(Table table) => Descendants<Header.Strip>(table).First();

    private static Control HeaderCell(Header.Strip strip, int visibleIndex) =>
        (Control)((Panel)Field(strip, "_panel")!).Children[visibleIndex];

    private static IReadOnlyList<Popup> OpenPopups(Table table) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(table.XamlRoot);

    /// <summary>The realized item with this label inside the open flyout.</summary>
    private static MenuFlyoutItem OpenItem(Table table, string text)
    {
        foreach (Popup popup in OpenPopups(table))
        {
            if (popup.Child is not DependencyObject child)
            {
                continue;
            }

            MenuFlyoutItem? item = Descendants<MenuFlyoutItem>(child)
                .FirstOrDefault(candidate => candidate.Text == text);
            if (item is not null)
            {
                return item;
            }
        }

        throw new AssertFailedException($"No open flyout shows a '{text}' item.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    // The menu, the effective columns it acts on and the strip's show are all internal to TableView,
    // which grants no InternalsVisibleTo. Reflection is the only way to reach them without widening
    // the control's public surface for a test.

    private static MenuFlyout Menu(Table table, string? activeId)
    {
        Type type =
            typeof(Table).Assembly.GetType("Syno.TableView.Header.Menu")
            ?? throw new MissingMemberException("Syno.TableView.Header.Menu");

        object?[] arguments =
        {
            table,
            activeId is null ? null : TableHarness.EffectiveColumn(table, activeId),
        };
        return (MenuFlyout)
            type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, arguments)!;
    }

    /// <summary>
    /// Open the menu the way the strip opens it, on the header cell at this visible index, or on
    /// unused header space when there is none. Only the platform's own <c>ContextRequested</c> is
    /// left out; it cannot be raised from a test.
    /// </summary>
    private static async Task<Control?> OpenMenuAsync(Table table, int? visibleIndex)
    {
        Header.Strip strip = Strip(table);
        Control? cell = visibleIndex is int index ? HeaderCell(strip, index) : null;

        strip
            .GetType()
            .GetMethod("ShowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(strip, new object?[] { cell, null });

        await Task.Delay(250);
        return cell;
    }

    private static async Task InvokeAsync(Table table, string text)
    {
        MenuFlyoutItem item = OpenItem(table, text);
        Assert.IsTrue(item.IsEnabled, $"'{text}' is disabled.");

        (
            (IInvokeProvider)
                FrameworkElementAutomationPeer
                    .CreatePeerForElement(item)
                    .GetPattern(PatternInterface.Invoke)
        ).Invoke();

        await Task.Delay(250);
    }

    /// <summary>Checks what one invocation reported, then forgets it.</summary>
    private static void AssertReported(List<LayoutChange> reported, LayoutChange expected)
    {
        CollectionAssert.AreEqual(new[] { expected }, reported);
        reported.Clear();
    }

    private static void SetVisibility(Table table, string id, bool visible) =>
        Invoke(table, "SetColumnVisibility", TableHarness.EffectiveColumn(table, id), visible);

    private static object? Invoke(object target, string method, params object[] arguments) =>
        target
            .GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);

    private static object? Field(object target, string name) =>
        target
            .GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target);
}
