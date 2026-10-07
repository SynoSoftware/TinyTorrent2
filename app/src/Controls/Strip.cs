using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TinyTorrent.Controls;

// Places children in rows of aligned columns: one row when they fit, else two
// rows, else one column. Each column is as wide as its widest child and the free
// width is shared equally between columns, so each row spans the panel. Grid's
// Auto columns cannot do this, because they never shrink and a row that does not
// fit runs past the panel's edge.
public sealed partial class Strip : Panel
{
    // Read during layout only; set them in markup. ColumnSpacing is the
    // smallest gap between two columns.
    public double ColumnSpacing { get; set; }
    public double RowSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        var children = Visible();
        var columns = Columns(children, availableSize.Width);
        var rows = children.Chunk(columns).ToArray();
        var width = Math.Min(RequiredWidth(children, columns), availableSize.Width);
        var height = rows.Sum(RowHeight) + RowSpacing * Math.Max(0, rows.Length - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Visible();
        var columns = Columns(children, finalSize.Width);
        var widths = ColumnWidths(children, columns);
        var gap = columns > 1 ? (finalSize.Width - widths.Sum()) / (columns - 1) : 0;
        var y = 0.0;
        foreach (var row in children.Chunk(columns))
        {
            var height = RowHeight(row);
            var x = 0.0;
            for (var column = 0; column < row.Length; column++)
            {
                row[column].Arrange(new Rect(x, y, widths[column], height));
                x += widths[column] + gap;
            }
            y += height + RowSpacing;
        }
        return finalSize;
    }

    private UIElement[] Visible() => Children.Where(child => child.Visibility == Visibility.Visible).ToArray();

    private int Columns(UIElement[] children, double width)
    {
        var oneRow = Math.Max(children.Length, 1);
        var twoRows = (oneRow + 1) / 2;
        if (RequiredWidth(children, oneRow) <= width)
        {
            return oneRow;
        }
        if (RequiredWidth(children, twoRows) <= width)
        {
            return twoRows;
        }
        return 1;
    }

    private double RequiredWidth(UIElement[] children, int columns) =>
        ColumnWidths(children, columns).Sum() + ColumnSpacing * (columns - 1);

    private static double[] ColumnWidths(UIElement[] children, int columns)
    {
        var widths = new double[columns];
        for (var index = 0; index < children.Length; index++)
        {
            var column = index % columns;
            widths[column] = Math.Max(widths[column], children[index].DesiredSize.Width);
        }
        return widths;
    }

    private static double RowHeight(UIElement[] row) => row.Max(child => child.DesiredSize.Height);
}
