using System.Collections.ObjectModel;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView.Tests;

/// <summary>One row item with a stable key, so identity reconciliation has something to reconcile.</summary>
internal sealed class Row
{
    internal Row(string key) => Key = key;

    internal string Key { get; }

    /// <summary>
    /// The number in the key: "k3" is 3. A second orderable field, so a test can give one column
    /// an order that is genuinely not the row order.
    /// </summary>
    internal int Rank => int.TryParse(Key.AsSpan(1), out int rank) ? rank : 0;

    internal bool Interactive { get; set; } = true;

    public override string ToString() => Key;
}

/// <summary>
/// Builds a loaded table over a live row collection and reaches the table's private input entry
/// points. The control grants no <c>InternalsVisibleTo</c>, so reflection is the only way to
/// exercise handlers that a real pointer or key message would otherwise reach.
/// </summary>
internal sealed class SelectionHarness
{
    internal SelectionHarness(Table table, ObservableCollection<Row> rows)
    {
        Table = table;
        Rows = rows;
        table.SelectionChanged += (_, selection) =>
        {
            Events++;
            LastSelected = selection.Items;
            LastCurrent = selection.Current;
        };
        table.ItemInvoked += (_, e) => Invoked.Add(e.Item);
    }

    internal Table Table { get; }

    internal ObservableCollection<Row> Rows { get; }

    internal int Events { get; set; }

    internal IReadOnlyList<object>? LastSelected { get; private set; }

    internal object? LastCurrent { get; private set; }

    internal List<object> Invoked { get; } = new();

    internal static async Task<SelectionHarness> LoadAsync(
        int rowCount,
        Action<Table>? configure = null,
        double height = 220
    )
    {
        ObservableCollection<Row> rows = new();
        for (int i = 0; i < rowCount; i++)
        {
            rows.Add(new Row("k" + i));
        }

        Table table = TestData.Table(TestData.Column("a", 120), TestData.Column("b", 120));
        table.Schema<Row>().Key(row => row.Key);
        table.Width = 320;
        table.Height = height;

        // Declared widths rather than the first fill, so the rows leave surface beside them.
        table.Layout = TestData.DeclaredWidths(table);
        configure?.Invoke(table);
        table.ItemsSource = rows;

        await TableHarness.LoadAsync(table);
        table.UpdateLayout();

        return new SelectionHarness(table, rows);
    }

    internal string[] SelectedKeys() =>
        Table.Selection.Items.Cast<Row>().Select(r => r.Key).ToArray();

    internal string? CurrentKey() => (Table.Selection.Current as Row)?.Key;

    internal Row this[int index] => Rows[index];

    /// <summary>The selection the row surface actually shows, which the table must own.</summary>
    internal string[] ContainerSelectedKeys()
    {
        ListView list = Surface();
        return list
            .SelectedItems.Cast<Row>()
            .Select(r => r.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
    }

    internal ListView Surface() =>
        Descendant<ListView>(Table) ?? throw new InvalidOperationException("No row surface.");

    // ------------------------------------------------------------------ private entry points

    /// <summary>What the pointer arbiter calls once it has resolved a row and its modifiers.</summary>
    internal void Click(Row row, bool ctrl = false, bool shift = false) =>
        Invoke("SelectItem", row, ctrl, shift);

    internal bool MoveBy(int delta, bool extend, bool ctrl = false) =>
        (bool)Invoke("MoveCurrentBy", delta, extend, ctrl)!;

    internal bool MoveToEdge(bool first, bool extend, bool ctrl = false) =>
        (bool)Invoke("MoveCurrentToEdge", first, extend, ctrl)!;

    internal bool Space(bool ctrl = false, bool shift = false) =>
        (bool)Invoke("SelectFocusedItem", ctrl, shift)!;

    internal bool Tap(DependencyObject source, bool ctrl = false, bool shift = false) =>
        (bool)Invoke("SelectFromTap", source, ctrl, shift)!;

    internal bool SelectAll() => (bool)Invoke("SelectAllFromKeyboard")!;

    internal bool InvokeCurrent() => (bool)Invoke("InvokeCurrentItem")!;

    // The table captures how the rows hold focus, not merely that they do, so that a reconcile can
    // restore the same state. This asks the same question the old bool did.
    internal bool RowSurfaceHasFocus() =>
        (FocusState)Invoke("RowSurfaceFocusState")! != FocusState.Unfocused;

    internal int RowsPerPage() => (int)Invoke("RowsPerPage")!;

    /// <summary>Runs the table's real hit test over a real element in a realized row.</summary>
    internal string HitTest(DependencyObject source)
    {
        MethodInfo method = typeof(Table).GetMethod(
            "HitTest",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        object?[] args = { source, null };
        object result = method.Invoke(Table, args)!;
        return result.ToString()!;
    }

    private object? Invoke(string name, params object?[] args)
    {
        MethodInfo method =
            typeof(Table).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Table", name);
        return method.Invoke(Table, args);
    }

    internal static T? Descendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (Descendant<T>(child) is T deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
