using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;

namespace Syno.TableView.Tests;

/// <summary>
/// What the cost tests share: the UI thread's CPU cycles, which other load on the machine does
/// not change, and the summary of repeated readings.
/// </summary>
internal static class Measurement
{
    [DllImport("kernel32.dll")]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    internal static ulong Cycles()
    {
        QueryThreadCycleTime(GetCurrentThread(), out ulong cycles);
        return cycles;
    }

    internal static double Median(List<double> values)
    {
        List<double> sorted = values.Order().ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    internal static string Spread(List<double> values) =>
        $"median {Median(values):0.000} (range {values.Min():0.000}-{values.Max():0.000})";

    /// <summary>Completes after the next frame's work, layout and the list's deferred work included.</summary>
    internal static Task NextFrameAsync()
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendering = null;
        rendering = (_, _) =>
        {
            CompositionTarget.Rendering -= rendering;
            done.SetResult();
        };
        CompositionTarget.Rendering += rendering;
        return done.Task;
    }
}
