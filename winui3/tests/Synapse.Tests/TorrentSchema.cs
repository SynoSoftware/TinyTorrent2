using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

/// <summary>One torrent row, with the fields the eleven columns show and sort on.</summary>
public sealed class TorrentRow
{
    public TorrentRow(string id, string name, int queue, double progress, long size)
    {
        Id = id;
        Name = name;
        Queue = queue;
        Progress = progress;
        Size = size;
    }

    public string Id { get; }

    public string Name { get; }

    public int Queue { get; }

    public double Progress { get; }

    public long Size { get; }

    public override string ToString() => Name;
}

/// <summary>
/// The torrent host's own column schema — the eleven columns of Appendix A.1 at their declared
/// widths and minimums, with the same four hidden on first run — rebuilt here so the control can be
/// exercised against it without the sample application.
/// </summary>
internal static class TorrentSchema
{
    /// <summary>id, Width, MinWidth. Appendix A.1.</summary>
    internal static readonly (string Id, double Width, double Min)[] Declared =
    {
        ("name", 150, 90),
        ("progress", 220, 110),
        ("status", 110, 95),
        ("queue", 80, 48),
        ("eta", 110, 48),
        ("speed", 180, 160),
        ("peers", 88, 48),
        ("size", 100, 48),
        ("ratio", 90, 48),
        ("added", 100, 48),
        ("completedOn", 110, 48),
    };

    private static readonly HashSet<string> HiddenOnFirstRun =
        new(StringComparer.Ordinal) { "eta", "ratio", "added", "completedOn" };

    private static readonly (string Id, string Display)[] Names =
    {
        ("name", "Name"), ("progress", "Progress"), ("status", "Status"), ("queue", "Queue"),
        ("eta", "ETA"), ("speed", "Speed"), ("peers", "Peers"), ("size", "Size"),
        ("ratio", "Ratio"), ("added", "Added"), ("completedOn", "Completed on"),
    };

    internal static string[] Ids => Declared.Select(d => d.Id).ToArray();

    /// <summary>Six rows whose queue positions are deliberately not their source order.</summary>
    internal static ObservableCollection<TorrentRow> Rows() => new()
    {
        new TorrentRow("t0", "ubuntu.iso", 4, 94.8, 6_400_000_000),
        new TorrentRow("t1", "debian.iso", 1, 31.4, 2_100_000_000),
        new TorrentRow("t2", "fedora.iso", 6, 63.7, 3_300_000_000),
        new TorrentRow("t3", "arch.iso", 2, 75.5, 900_000_000),
        new TorrentRow("t4", "alpine.iso", 5, 12.0, 150_000_000),
        new TorrentRow("t5", "gentoo.iso", 3, 100.0, 4_800_000_000),
    };

    internal static string[] NaturalNames => new[]
    {
        "ubuntu.iso", "debian.iso", "fedora.iso", "arch.iso", "alpine.iso", "gentoo.iso",
    };

    /// <summary>The same six rows in queue order: 1,2,3,4,5,6.</summary>
    internal static string[] QueueAscending => new[]
    {
        "debian.iso", "arch.iso", "gentoo.iso", "ubuntu.iso", "alpine.iso", "fedora.iso",
    };

    internal static TableView Build()
    {
        TableView table = new() { Width = 1100, Height = 420 };
        TableSchema<TorrentRow> schema = table.Schema<TorrentRow>().Key(row => row.Id);

        foreach ((string id, double width, double min) in Declared)
        {
            TableColumn column = new()
            {
                Id = id,
                DisplayName = Names.Single(n => n.Id == id).Display,
                Width = width,
                MinWidth = min,
                IsVisible = !HiddenOnFirstRun.Contains(id),
                CellTemplate = CellTemplate(id),
            };

            table.Columns.Add(column);
            Sort(schema, column, id);
        }

        return table;
    }

    internal static async Task<TableView> LoadAsync()
    {
        TableView table = Build();
        table.ItemsSource = Rows();

        await TableHarness.LoadAsync(table);
        table.UpdateLayout();

        // A row container's cells panel fills itself on its own Loaded, which is one turn later.
        await Task.Delay(120);
        table.UpdateLayout();
        return table;
    }

    // ---------------------------------------------------------------- reading the live tree

    internal static TableHeaderStrip Strip(TableView table) =>
        Proof.Descendant<TableHeaderStrip>(table)
        ?? throw new AssertFailedException("The table realized no header strip.");

    internal static List<TableHeaderCell> HeaderCells(TableView table)
    {
        table.UpdateLayout();
        return Proof.Descendants<TableHeaderCell>(Strip(table));
    }

    /// <summary>The cells panel of each realized row container, in view order.</summary>
    internal static List<TableCellsPanel> RowPanels(TableView table)
    {
        ListView list = Proof.Descendant<ListView>(table)
            ?? throw new AssertFailedException("The table realized no hosted list.");
        list.UpdateLayout();

        List<TableCellsPanel> panels = new();
        foreach (object item in ((System.Collections.IEnumerable)list.ItemsSource!))
        {
            if (list.ContainerFromItem(item) is DependencyObject container
                && Proof.Descendant<TableCellsPanel>(container) is TableCellsPanel panel)
            {
                panels.Add(panel);
            }
        }

        return panels;
    }

    internal static string[] ViewNameArray(TableView table)
    {
        ListView list = Proof.Descendant<ListView>(table)!;
        return ((System.Collections.IEnumerable)list.ItemsSource!)
            .Cast<TorrentRow>()
            .Select(r => r.Name)
            .ToArray();
    }

    internal static string ViewNames(TableView table) => string.Join(", ", ViewNameArray(table));

    // ---------------------------------------------------------------- schema parts

    /// <summary>Every column sorts, each by the field its cell shows.</summary>
    private static void Sort(TableSchema<TorrentRow> schema, TableColumn column, string id)
    {
        switch (id)
        {
            case "name":
                schema.Sort(column, row => row.Name);
                break;
            case "progress":
                schema.Sort(column, row => row.Progress);
                break;
            case "size":
                schema.Sort(column, row => row.Size);
                break;
            default:
                schema.Sort(column, row => row.Queue);
                break;
        }
    }

    /// <summary>
    /// A cell that renders something wide enough for a fit to be measurable, from the same field
    /// the column sorts on.
    /// </summary>
    private static DataTemplate CellTemplate(string id)
    {
        string path = id switch
        {
            "name" => "Name",
            "queue" => "Queue",
            "progress" => "Progress",
            "size" => "Size",
            _ => "Id",
        };

        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            $"<TextBlock Text='{{Binding {path}}}' Margin='6,0' VerticalAlignment='Center'/>" +
            "</DataTemplate>");
    }
}
