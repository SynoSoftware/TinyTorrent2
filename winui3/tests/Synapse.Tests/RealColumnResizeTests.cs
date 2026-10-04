using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// Section 10's direct resize, driven with real mouse messages through the window. The separator
/// has no element of its own — the strip owns a hit zone on each trailing edge — so the press,
/// the capture, the clamp, Escape and the double-click fit can only be exercised by real input.
/// </summary>
/// <remarks>
/// Three 200 DIP columns, so the separators sit at x = 200 and x = 400.
/// </remarks>
[TestClass]
// Interactive: injects real mouse messages and takes the foreground window. Excluded from the
// default run by .runsettings; ask for it deliberately with --filter TestCategory=Interactive.
[TestCategory("Interactive")]
public class RealColumnResizeTests
{
    private const double FirstSeparator = 200;

    [TestMethod]
    public Task ARealSeparatorDragResizesOnlyThatColumn() => TestHost.RunAsync(async () =>
    {
        DragHarness h = await DragHarness.LoadAsync(Bounded);
        int events = 0;
        h.Table.LayoutChanged += (_, kind) =>
        {
            Assert.AreEqual(TableLayoutChangeKind.ColumnResize, kind);
            events++;
        };

        await h.MoveAsync(FirstSeparator);
        await h.PressAsync(FirstSeparator);
        await h.MoveAsync(FirstSeparator + 40);

        Assert.AreEqual(240, Width(h, "a"), 2, "the width tracks the pointer");
        Assert.AreEqual(0, events, "and a pointer move is not a persistence event");

        await h.MoveAsync(FirstSeparator + 80);
        await h.ReleaseAsync(FirstSeparator + 80);

        Assert.AreEqual(280, Width(h, "a"), 2);
        Assert.AreEqual(200, Width(h, "b"), 0.01, "the next column keeps its own width");
        Assert.AreEqual(1, events, "one gesture, one notification");
    });

    [TestMethod]
    public Task ARealDragIsClampedToTheColumnsOwnLimits() => TestHost.RunAsync(async () =>
    {
        DragHarness h = await DragHarness.LoadAsync(Bounded);

        await h.MoveAsync(FirstSeparator);
        await h.PressAsync(FirstSeparator);
        await h.MoveAsync(FirstSeparator - 150);

        Assert.AreEqual(120, Width(h, "a"), 0.01, "it stops at MinWidth");

        await h.MoveAsync(FirstSeparator + 200);

        Assert.AreEqual(300, Width(h, "a"), 0.01, "and at MaxWidth");

        await h.ReleaseAsync(FirstSeparator + 200);
    });

    [TestMethod]
    public Task EscapeRestoresTheWidthTheDragStartedFrom() => TestHost.RunAsync(async () =>
    {
        DragHarness h = await DragHarness.LoadAsync(Bounded);
        int events = 0;
        h.Table.LayoutChanged += (_, _) => events++;

        await h.MoveAsync(FirstSeparator);
        await h.PressAsync(FirstSeparator);
        await h.MoveAsync(FirstSeparator + 60);

        Assert.AreEqual(260, Width(h, "a"), 2);

        await h.EscapeAsync();

        Assert.AreEqual(200, Width(h, "a"), 0.01, "the width captured at the press comes back");

        await h.ReleaseAsync(FirstSeparator + 60);

        Assert.AreEqual(200, Width(h, "a"), 0.01, "and the release after the cancel changes nothing");
        Assert.AreEqual(0, events, "a cancelled gesture changed no layout");
    });

    [TestMethod]
    public Task ARealDoubleClickOnTheSeparatorFitsThatColumn() => TestHost.RunAsync(async () =>
    {
        DragHarness h = await DragHarness.LoadAsync(Bounded);
        int fits = 0;
        h.Table.LayoutChanged += (_, kind) =>
        {
            Assert.AreEqual(TableLayoutChangeKind.AutoFit, kind);
            fits++;
        };

        await h.MoveAsync(FirstSeparator);
        await h.PressAsync(FirstSeparator);
        await h.ReleaseAsync(FirstSeparator);
        await h.PressAsync(FirstSeparator);
        await h.ReleaseAsync(FirstSeparator);

        Assert.AreEqual(120, Width(h, "a"), 0.01,
            "the header label is narrower than MinWidth, so the fit clamps there");
        Assert.AreEqual(200, Width(h, "b"), 0.01, "and no other column was fitted");
        Assert.AreEqual(1, fits);
    });

    /// <summary>The first column takes bounds, so the clamp and the fit have known numbers.</summary>
    private static void Bounded(TableView table)
    {
        table.Columns[0].MinWidth = 120;
        table.Columns[0].MaxWidth = 300;
    }

    private static double Width(DragHarness h, string id) => TableHarness.ResolvedWidth(h.Table, id);
}
