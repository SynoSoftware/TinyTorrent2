using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Syno.TableView.Header;

/// <summary>
/// The icons the control draws in the menu it generates.
/// </summary>
/// <remarks>
/// Native glyphs and horizontal sizing diagrams belong to the control rather
/// than its host, so every table uses the same icon for each operation.
/// </remarks>
internal static class Icons
{
    internal static IconElement FitColumn() => Shape(
        [new(3, 3), new(17, 3), new(17, 17), new(3, 17)],
        [new(4, 4), new(4, 16), new(16, 16), new(16, 4)],
        [new(5, 10), new(8, 7), new(9, 8), new(7.5, 9.5), new(12.5, 9.5), new(11, 8), new(12, 7),
            new(15, 10), new(12, 13), new(11, 12), new(12.5, 10.5), new(7.5, 10.5), new(9, 12), new(8, 13)]
    );

    internal static IconElement FitColumns() => Shape(
        [new(1, 3), new(9, 3), new(9, 17), new(1, 17)],
        [new(2, 4), new(2, 16), new(8, 16), new(8, 4)],
        [new(2, 10), new(4, 8), new(4.75, 8.75), new(4, 9.5), new(6, 9.5), new(5.25, 8.75), new(6, 8),
            new(8, 10), new(6, 12), new(5.25, 11.25), new(6, 10.5), new(4, 10.5), new(4.75, 11.25), new(4, 12)],
        [new(11, 3), new(19, 3), new(19, 17), new(11, 17)],
        [new(12, 4), new(12, 16), new(18, 16), new(18, 4)],
        [new(12, 10), new(14, 8), new(14.75, 8.75), new(14, 9.5), new(16, 9.5), new(15.25, 8.75), new(16, 8),
            new(18, 10), new(16, 12), new(15.25, 11.25), new(16, 10.5), new(14, 10.5), new(14.75, 11.25), new(14, 12)]
    );

    internal static IconElement FitWidth() => Shape(
        [new(0, 4), new(1.5, 4), new(1.5, 16), new(0, 16)],
        [new(18.5, 4), new(20, 4), new(20, 16), new(18.5, 16)],
        [new(2, 10), new(5.5, 6.5), new(6.5, 7.5), new(4.75, 9.25), new(15.25, 9.25), new(13.5, 7.5), new(14.5, 6.5),
            new(18, 10), new(14.5, 13.5), new(13.5, 12.5), new(15.25, 10.75), new(4.75, 10.75), new(6.5, 12.5), new(5.5, 13.5)]
    );

    internal static IconElement HideColumn() => Glyph(Segoe.EyeOff);

    /// <summary>Showing a column again, when the item the owner clicked hid the one it names.</summary>
    internal static IconElement ShowColumn() => Glyph(Segoe.Eye);

    /// <summary>
    /// A shown column, drawn in the icon slot rather than by a toggle's own check. A
    /// <c>ToggleMenuFlyoutItem</c> keeps its check in a column of its own that holds its width
    /// even while the check is invisible, so one in a menu beside items carrying icons produces
    /// two glyph columns and indents every row past both. A <c>MenuFlyoutItem</c> has the icon
    /// column only.
    /// </summary>
    internal static IconElement Shown() => Glyph(Segoe.SquareCheck);

    internal static IconElement Hidden() => Glyph(Segoe.Square);

    internal static IconElement MoveLeft() => Glyph(Segoe.ArrowLeft);

    internal static IconElement MoveRight() => Glyph(Segoe.ArrowRight);

    private static IconElement Shape(params Point[][] contours)
    {
        // Opposite contour windings leave the column interiors open.
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (var contour in contours)
        {
            var points = new PointCollection();
            foreach (var point in contour.AsSpan(1))
                points.Add(point);
            geometry.Figures.Add(new PathFigure
            {
                StartPoint = contour[0],
                IsClosed = true,
                Segments = { new PolyLineSegment { Points = points } },
            });
        }
        return new PathIcon { Data = geometry, Width = 20, Height = 20 };
    }

    /// <summary>
    /// A new element per call. An icon is a <c>UIElement</c> and accepts exactly one parent, so a
    /// shared instance would throw the second time a menu was built.
    /// </summary>
    /// <remarks>
    /// Larger than the 16 a menu icon slot is built for, so the mark reads larger than the item's
    /// text. The slot does not clip and the row's height comes from its text, so the glyph grows
    /// into the slot's padding rather than making the row taller.
    /// </remarks>
    private static IconElement Glyph(string glyph) =>
        new FontIcon
        {
            FontFamily = Segoe.Font,
            Glyph = glyph,
            FontSize = 20,
        };
}
