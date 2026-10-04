using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TableViewSample.Probe;

/// <summary>
/// Probe stand-in for the table's row panel. Measures to a fixed total width far
/// wider than any viewport and subtracts a table-owned horizontal offset at arrange
/// time, exactly as lib/TableView/docs/archive/tableview-design.md section 3 describes.
/// </summary>
public sealed class RowPanel : Panel
{
    public const double ColumnWidth = 200;
    public const int ColumnCount = 10;          // 10 * 200 = 2000 DIP total
    public const double TotalWidth = ColumnWidth * ColumnCount;

    /// <summary>Table-owned horizontal offset, shared by every realized row.</summary>
    public static double SharedOffset;

    public static readonly List<RowPanel> Live = new();

    public Size LastMeasureAvailable;
    public Size LastMeasureReturned;
    public Size LastArrangeFinal;
    public readonly List<Rect> LastArrangeRects = new();
    public int MeasureCount;
    public int ArrangeCount;

    public RowPanel()
    {
        Loaded += (_, _) => { if (!Live.Contains(this)) Live.Add(this); };
        Unloaded += (_, _) => Live.Remove(this);
    }

    public static void InvalidateAll()
    {
        foreach (var p in Live)
        {
            p.InvalidateMeasure();
            p.InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureCount++;
        LastMeasureAvailable = availableSize;
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(ColumnWidth, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        if (height <= 0) height = 28;
        LastMeasureReturned = new Size(TotalWidth, height);
        return LastMeasureReturned;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangeCount++;
        LastArrangeFinal = finalSize;
        LastArrangeRects.Clear();
        for (int i = 0; i < Children.Count; i++)
        {
            var r = new Rect(i * ColumnWidth - SharedOffset, 0, ColumnWidth, finalSize.Height);
            Children[i].Arrange(r);
            LastArrangeRects.Add(r);
        }
        // The table owns the horizontal axis: the row occupies exactly the viewport
        // width it was given and moves its cells inside it.
        return finalSize;
    }
}

public sealed class Item
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public override string ToString() => Name;
}
