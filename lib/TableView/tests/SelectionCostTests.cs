using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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

    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    private static ulong Cycles()
    {
        QueryThreadCycleTime(GetCurrentThread(), out ulong cycles);
        return cycles;
    }

    private static double Mega(ulong from, ulong to) => (to - from) / 1e6;

    /// <summary>
    /// A click in a 200-row table, the peer table's size: the model operation a press applies and
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
            await Task.Delay(1500);

            MethodInfo refresh =
                typeof(Table).GetMethod("RefreshContainers", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("Table", "RefreshContainers");
            ItemsStackPanel panel = (ItemsStackPanel)h.Surface().ItemsPanelRoot;
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
                await Task.Delay(200);
                CompositionTarget.Rendering -= OnRendering;
                Assert.AreEqual(3, ticks.Count(tick => tick.Done > 0), "three frames rendered after the click");

                ulong refreshStart = Cycles();
                refresh.Invoke(h.Table, null);
                ulong refreshEnd = Cycles();
                await Task.Delay(100);

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

    private static string Spread(List<double> values) =>
        $"median {Median(values):0.000} (range {values.Min():0.000}-{values.Max():0.000})";

    private static double Median(List<double> values)
    {
        List<double> sorted = values.Order().ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
