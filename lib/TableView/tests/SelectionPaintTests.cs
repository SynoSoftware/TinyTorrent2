using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation;

namespace Syno.TableView.Tests;

/// <summary>
/// What the rows paint, not what <see cref="ListViewItem.IsSelected"/> reports. With the view as
/// the list's <c>ISelectionInfo</c> that getter answers from the table's model, so it agrees with
/// the model even while a row still shows the old state. A suite that read only the getter passed
/// while every click left the old row highlighted until the list's own release handling.
/// </summary>
[TestClass]
public class SelectionPaintTests
{
    /// <summary>
    /// Every selection change repaints the visible rows to match the model, and none of them
    /// scrolls the list. The rows hold keyboard focus for the scrolled steps, because the list
    /// scrolls to a row it focuses for a selection only while it has focus or remembers a focused row.
    /// </summary>
    /// <remarks>
    /// Light theme, because its selected fill differs from the background only in alpha, so a
    /// check that passes there also passes in Dark.
    /// </remarks>
    [TestMethod]
    public Task EverySelectionChangePaintsTheModelWithoutScrolling() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(60,
                table => table.RequestedTheme = ElementTheme.Light, height: 300);
            ListView list = h.Surface();
            ScrollViewer scroller = (ScrollViewer)Proof.Call(h.Table, "InnerScrollViewer")!;
            double rowHeight = ((FrameworkElement)list.ContainerFromItem(h[0])).ActualHeight;

            async Task Step(string name, Action change)
            {
                double offset = scroller.VerticalOffset;
                change();
                h.Table.UpdateLayout();
                (string painted, string model) = await PaintedAsync(h);
                Assert.AreEqual(model, painted, $"{name}: the visible rows paint the model");
                Assert.AreEqual(offset, scroller.VerticalOffset, 0.5, $"{name}: the list did not scroll");
            }

            await Step("click", () => h.Click(h[1]));
            await Step("Ctrl+click", () => h.Click(h[3], ctrl: true));
            await Step("Shift+click", () => h.Click(h[5], shift: true));
            await Step("marquee", () =>
            {
                // From the surface beside the rows, across the first three.
                FeatureProofTests.Gesture.PressEmpty(h, new Point(list.ActualWidth - 10, rowHeight * 0.5));
                Proof.Call(h.Table, "BeginMarquee", list);
                Proof.Call(Proof.Field<object>(h.Table, "_marquee"), "Track", new Point(20, rowHeight * 2.5));
                Proof.Call(h.Table, "ClearGesture");
            });
            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.SelectedKeys(), "the marquee covered three rows");
            await Step("host clear", () => h.Table.Selection = new Selection(Array.Empty<object>()));

            scroller.ChangeView(null, rowHeight * 20, null, disableAnimation: true);
            h.Table.UpdateLayout();
            await TableHarness.WaitUntilAsync(() => scroller.VerticalOffset > rowHeight * 19, "the scroll");
            // A visible row, because the list scrolls a focused row back into view after a scroll.
            Assert.IsTrue(((Control)list.ContainerFromItem(h[21])).Focus(FocusState.Keyboard), "a row took keyboard focus");
            await Step("Ctrl+A from an empty selection", () => Assert.IsTrue(h.SelectAll()));

            await Step("host clear after scrolling", () => h.Table.Selection = new Selection(Array.Empty<object>()));
            await Step("host assignment from empty to a row above the view",
                () => h.Table.Selection = new Selection(new object[] { h[0] }));

            await Step("host assignment of two visible rows",
                () => h.Table.Selection = new Selection(new object[] { h[22], h[24] }));
            await Step("a publish removes a selected row", () => h.Rows.RemoveAt(22));
            CollectionAssert.AreEqual(new[] { "k24" }, h.SelectedKeys(), "the publish dropped the removed row");
        });

    /// <summary>
    /// The painted and the modelled keys once they agree, or as they stand after ten seconds. The
    /// selection fill changes through a brush transition, so a read inside it still shows the old
    /// state; reading again until the rows agree waits for exactly that transition and no longer.
    /// </summary>
    private static async Task<(string Painted, string Model)> PaintedAsync(SelectionHarness h)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        (string Painted, string Model) read = await ReadPaintedAsync(h);
        while (read.Painted != read.Model && DateTime.UtcNow < deadline)
        {
            await Task.Delay(16);
            read = await ReadPaintedAsync(h);
        }
        return read;
    }

    /// <summary>
    /// The keys of the fully visible rows whose fill shows selected, and of those the model
    /// selects, both in view order. A row is painted selected when its pixel differs from the list
    /// background beside the rows, because an unselected row is transparent. All four bytes count:
    /// the Light theme's selected fill is nearly transparent black, which only its alpha shows.
    /// </summary>
    private static async Task<(string Painted, string Model)> ReadPaintedAsync(SelectionHarness h)
    {
        ListView list = h.Surface();
        RenderTargetBitmap bitmap = new();
        await bitmap.RenderAsync(list);
        byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
        double scale = bitmap.PixelWidth / list.ActualWidth;

        List<(double Top, string Key, bool Painted, bool Selected)> rows = new();
        foreach (UIElement child in list.ItemsPanelRoot.Children)
        {
            if (child is not ListViewItem container || list.ItemFromContainer(container) is not Row row)
            {
                continue;
            }
            Rect bounds = container.TransformToVisual(list)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Top < 0 || bounds.Bottom > list.ActualHeight || bounds.Right > list.ActualWidth - 20)
            {
                continue;
            }
            double y = (bounds.Top + bounds.Bottom) / 2;
            rows.Add((bounds.Top, row.Key, Difference(bounds.Right - 12, list.ActualWidth - 8, y) > 3,
                h.Table.Selection.Items.Contains(row)));
        }
        Assert.IsTrue(rows.Count >= 4, $"only {rows.Count} visible rows could be read");
        rows.Sort((a, b) => a.Top.CompareTo(b.Top));
        return (string.Join(",", rows.Where(r => r.Painted).Select(r => r.Key)),
            string.Join(",", rows.Where(r => r.Selected).Select(r => r.Key)));

        // Premultiplied BGRA, so a translucent fill changes the alpha byte even over black.
        int Difference(double x, double other, double y)
        {
            int a = Offset(x, y), b = Offset(other, y), sum = 0;
            for (int i = 0; i < 4; i++)
            {
                sum += Math.Abs(pixels[a + i] - pixels[b + i]);
            }
            return sum;
        }

        int Offset(double x, double y) => ((int)(y * scale) * bitmap.PixelWidth + (int)(x * scale)) * 4;
    }
}
