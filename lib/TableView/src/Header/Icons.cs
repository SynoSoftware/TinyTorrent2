using Microsoft.UI.Xaml.Controls;

namespace Syno.TableView.Header;

/// <summary>
/// The icons the control draws in the menu it generates.
/// </summary>
/// <remarks>
/// Windows' icon font, referenced through Glyphs rather than taken from the host, so the generated
/// menu looks right in any application. The glyph for each idea is named once here,
/// so nothing else in the control carries a second opinion about which glyph means what.
/// </remarks>
internal static class Icons
{
    /// <summary>Fitting one column to what it holds: a measurement of this width.</summary>
    internal static IconElement FitColumn() => Glyph(Segoe.RulerDimensionLine);

    /// <summary>
    /// Fitting every visible column: the same action at a wider scope, so the glyph differs in
    /// scope rather than in tool — arrows spreading outward, not a second instrument.
    /// </summary>
    /// <remarks>
    /// The owner asked whether these two should instead be told apart by colour. They should not. A
    /// menu icon inherits the text foreground and is monochrome by design, so a coloured one reads
    /// as status rather than as category; High Contrast overrides icon colours outright, which
    /// would take the distinction away from the readers who most need it; and section 19 does not
    /// allow colour to be the only carrier. A different instrument — a drafting glyph, say — would
    /// be worse still, because it would claim the two commands do different things when they do the
    /// same thing to a different number of columns. The labels name the scope too, so this glyph
    /// reinforces rather than carries.
    /// </remarks>
    internal static IconElement FitColumns() => Glyph(Segoe.UnfoldHorizontal);

    /// <summary>
    /// Fitting the visible columns to the table's width: content pushed out to both edges.
    /// </summary>
    internal static IconElement FitWidth() => Glyph(Segoe.AlignHorizontalSpaceBetween);

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
