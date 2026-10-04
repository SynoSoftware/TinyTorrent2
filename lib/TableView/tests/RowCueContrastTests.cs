using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Syno.TableView.Tests;

/// <summary>
/// Section 19: table-owned non-text information — here the selected-row cue, which is the only
/// cue this control paints on a row — must reach 3:1 in every supported theme. A resource name is
/// not proof, so these tests render the control and measure the drawn pixels.
/// </summary>
[TestClass]
public class RowCueContrastTests
{
    private const double Required = 3.0;

    [TestMethod]
    public Task TheSelectedRowCueMeetsThreeToOneInLight() => MeasureAsync(ElementTheme.Light);

    [TestMethod]
    public Task TheSelectedRowCueMeetsThreeToOneInDark() => MeasureAsync(ElementTheme.Dark);

    [TestMethod]
    public Task TheSelectedRowCueDoesNotDependOnTheAccentBrush() =>
        MeasureAsync(ElementTheme.Light, lowContrastAccent: true);

    // The two current-row cases are gone, not disabled. The control no longer draws a current-row
    // cue, so there is no table-owned non-text information left in that band to measure: WinUI
    // paints none either, and Fluent gives "where am I" to the focus visual, which is measured
    // separately and passes at 15.68:1 Light and 16.29:1 Dark. Section 19 lists "current" among
    // the cues requiring 3:1, so that enumeration is now wrong and is raised as an amendment.

    private static Task MeasureAsync(ElementTheme theme, bool lowContrastAccent = false) => TestHost.RunAsync(async () =>
    {
        Color backdrop = theme == ElementTheme.Light
            ? Color.FromArgb(255, 243, 243, 243)
            : Color.FromArgb(255, 32, 32, 32);

        // Blank cells. The only thing drawn over the row background is the table's own cue, so the
        // measurement cannot be flattered by the host's text.
        DataTemplate blank = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border Height="28" />
            </DataTemplate>
            """);

        SelectionHarness h = await SelectionHarness.LoadAsync(
            6,
            t =>
            {
                t.RequestedTheme = theme;
                t.Background = new SolidColorBrush(backdrop);
                if (lowContrastAccent)
                {
                    t.Resources["ListViewItemSelectionIndicatorBrush"] =
                        new SolidColorBrush(Color.FromArgb(255, 255, 185, 0));
                }
                foreach (Column column in t.Columns)
                {
                    column.CellTemplate = blank;
                }
            },
            height: 320);

        // Row 1 is selected. Row 3 is current and not selected, which now draws nothing at all.
        // Row 5 is untouched and supplies the background the cue is measured against.
        h.Table.Selection = new(new object[] { h[1] }, h[3]);
        h.Table.UpdateLayout();
        await Task.Delay(250);

        Shot shot = await Shot.TakeAsync(h.Table);
        (double top, double height) reference = RowBand(h, 5);
        uint background = shot.At(shot.Width / 2, shot.Y(reference.top + (reference.height / 2)));

        (double top, double height) band = RowBand(h, 1);
        double best = shot.MaxContrast(
            background,
            0,
            shot.Width - 1,
            shot.Y(band.top),
            shot.Y(band.top + band.height));

        Assert.IsTrue(
            best >= Required,
            $"{theme} selected-row cue: measured {best:0.00}:1 against the row background " +
            $"#{background:X6}; section 19 requires {Required:0.0}:1.");
    });

    private static (double Top, double Height) RowBand(SelectionHarness h, int index)
    {
        ListView list = h.HostedList();
        FrameworkElement container = list.ContainerFromItem(h[index]) as FrameworkElement
            ?? throw new AssertFailedException($"Row {index} was not realized.");

        double top = container.TransformToVisual(h.Table).TransformPoint(new Point(0, 0)).Y;
        return (top, container.ActualHeight);
    }

    /// <summary>One rendered frame of the control, addressed in pixels.</summary>
    internal sealed class Shot
    {
        private byte[] _pixels = Array.Empty<byte>();

        internal int Width { get; private set; }

        internal int Height { get; private set; }

        internal double Scale { get; private set; } = 1;

        internal static async Task<Shot> TakeAsync(FrameworkElement element)
        {
            RenderTargetBitmap bitmap = new();
            await bitmap.RenderAsync(element);
            IBuffer buffer = await bitmap.GetPixelsAsync();
            byte[] pixels = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(pixels);

            Shot shot = new()
            {
                _pixels = pixels,
                Width = bitmap.PixelWidth,
                Height = bitmap.PixelHeight,
                Scale = element.XamlRoot?.RasterizationScale ?? 1,
            };

            Assert.IsTrue(shot.Width > 0 && shot.Height > 0, "The control rendered an empty bitmap.");
            return shot;
        }

        internal int Y(double dips) => Math.Clamp((int)Math.Round(dips * Scale), 0, Height - 1);

        internal uint At(int x, int y)
        {
            int i = (((y * Width) + x) * 4);
            return (uint)(_pixels[i] | (_pixels[i + 1] << 8) | (_pixels[i + 2] << 16));
        }

        internal double MaxContrast(uint reference, int x0, int x1, int y0, int y1)
        {
            double best = 0;
            for (int y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
            {
                for (int x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++)
                {
                    best = Math.Max(best, Contrast(At(x, y), reference));
                }
            }

            return best;
        }

        private static double Contrast(uint a, uint b)
        {
            double first = Luminance(a);
            double second = Luminance(b);
            return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        }

        private static double Luminance(uint bgr) =>
            (0.2126 * Linear((bgr >> 16) & 0xFF))
            + (0.7152 * Linear((bgr >> 8) & 0xFF))
            + (0.0722 * Linear(bgr & 0xFF));

        private static double Linear(uint channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }
}
