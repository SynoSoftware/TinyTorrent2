using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Syno.TableView.Tests.Measurement;

namespace Syno.TableView.Tests;

/// <summary>
/// Measures what a click's selection change costs the UI thread, in CPU cycles so that other
/// load on the machine does not count. It asserts no limit, because the cycles depend on the
/// machine; compare runs on one machine.
/// </summary>
[TestClass]
public class SelectionCostTests
{
    private const int Reps = 40;

    public TestContext TestContext { get; set; } = null!;

    private static double Mega(ulong from, ulong to) => (to - from) / 1e6;

    /// <summary>
    /// A click in a 200-row table: the model operation a press applies and
    /// the next three frames, then <c>RefreshContainers</c> alone with nothing to change, which is
    /// its own share of the click.
    /// </summary>
    [TestMethod]
    [TestCategory("Measurement")]
    public Task ClickSelectionCycles() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(200, table =>
            {
                table.Width = 900;
                foreach (string id in new[] { "c", "d", "e", "f", "g", "h" })
                {
                    table.Columns.Add(TestData.Column(id, 100));
                }
            }, height: 600);

            MethodInfo refresh =
                typeof(Table).GetMethod("RefreshContainers", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("Table", "RefreshContainers");
            ItemsStackPanel panel = (ItemsStackPanel)h.Surface().ItemsPanelRoot;

            // The list grows its cache of containers over the first frames; measure once it has
            // stopped, so the first click does not pay for that growth.
            (int First, int Last) cache = (-1, -1);
            int unchanged = 0;
            await TableHarness.WaitUntilAsync(() =>
            {
                (int First, int Last) now = (panel.FirstCacheIndex, panel.LastCacheIndex);
                unchanged = now == cache ? unchanged + 1 : 0;
                cache = now;
                return unchanged >= 20;
            }, "the list's cache of containers to stop growing");

            List<double> clicks = new(), frames = new(), refreshes = new();

            for (int rep = 0; rep < Reps; rep++)
            {
                List<(ulong Render, ulong Done)> ticks = new();
                void OnRendering(object? sender, object args)
                {
                    if (ticks.Count >= 3)
                    {
                        return;
                    }
                    int index = ticks.Count;
                    ticks.Add((Cycles(), 0));
                    // Runs once this frame's layout and render walk have finished.
                    TestHost.Dispatcher.TryEnqueue(() => ticks[index] = ticks[index] with { Done = Cycles() });
                }

                ulong clickStart = Cycles();
                h.Click(h[2 + rep % 10 * 2]);
                ulong clickEnd = Cycles();
                CompositionTarget.Rendering += OnRendering;
                try
                {
                    await TableHarness.WaitUntilAsync(
                        () => ticks.Count(tick => tick.Done > 0) == 3, "three frames to render after the click");
                }
                finally
                {
                    CompositionTarget.Rendering -= OnRendering;
                }

                ulong refreshStart = Cycles();
                refresh.Invoke(h.Table, null);
                ulong refreshEnd = Cycles();
                await NextFrameAsync();

                clicks.Add(Mega(clickStart, clickEnd));
                frames.Add(ticks.Sum(tick => Mega(tick.Render, tick.Done)));
                refreshes.Add(Mega(refreshStart, refreshEnd));
            }

            List<double> totals = clicks.Select((c, i) => c + frames[i]).ToList();
            double total = Median(totals), alone = Median(refreshes);
            TestContext.WriteLine($"rows 200, cached containers {panel.LastCacheIndex - panel.FirstCacheIndex + 1}, clicks {Reps}");
            TestContext.WriteLine($"click Mc: {Spread(clicks)}; next 3 frames Mc: {Spread(frames)}; total Mc: {Spread(totals)}");
            TestContext.WriteLine($"RefreshContainers alone Mc: {Spread(refreshes)}; median {alone / total:P1} of the median total");
            TestContext.WriteLine("per click Mc (click, frames, refresh): "
                + string.Join("; ", clicks.Select((c, i) => $"{c:0.00},{frames[i]:0.00},{refreshes[i]:0.000}")));
        });
}
