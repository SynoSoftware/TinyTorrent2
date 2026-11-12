using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Syno.TinyTorrent.Controls;

/// <summary>
/// The colours a custom-drawn control paints with: its theme brushes as XAML
/// paints them, and colours faded or mixed from them, each channel rounded.
/// </summary>
internal static class Colour
{
    /// <summary>The colour XAML paints with a solid brush: its colour at its opacity.</summary>
    internal static Color Read(Brush brush)
    {
        var solid = (SolidColorBrush)brush;
        return Alpha(solid.Color, solid.Opacity);
    }

    /// <summary>The colour with its alpha multiplied by <paramref name="opacity"/>, which may exceed 1.</summary>
    internal static Color Alpha(Color colour, double opacity) =>
        Color.FromArgb(
            (byte)Math.Round(Math.Clamp(colour.A * opacity, 0, 255)),
            colour.R,
            colour.G,
            colour.B
        );

    /// <summary>The colour <paramref name="amount"/> of the way from <paramref name="under"/> to <paramref name="over"/>.</summary>
    internal static Color Mix(Color under, Color over, double amount) =>
        // Most pixels of the Pieces map mix nothing.
        amount <= 0
            ? under
            : Color.FromArgb(
                (byte)Math.Round(under.A + (over.A - under.A) * amount),
                (byte)Math.Round(under.R + (over.R - under.R) * amount),
                (byte)Math.Round(under.G + (over.G - under.G) * amount),
                (byte)Math.Round(under.B + (over.B - under.B) * amount)
            );
}
