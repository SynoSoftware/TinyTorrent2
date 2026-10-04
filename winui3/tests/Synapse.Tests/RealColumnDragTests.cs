using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// Section 11's drag end to end: real mouse messages through the window, so the press, the
/// threshold, the insertion marker, the drop and Escape are exercised rather than the arithmetic
/// behind them. <c>PointerRoutedEventArgs</c> cannot be constructed, so this is the only way to
/// run the gesture at all.
/// </summary>
[TestClass]
// Interactive: these inject real Windows input, so they take the pointer and keyboard away
// from whoever is at the machine. Excluded from the default run by .runsettings; ask for them
// deliberately with --filter TestCategory=Interactive when the input path has changed.
[TestCategory("Interactive")]
public class RealColumnDragTests
{
    [TestMethod]
    public Task ARealDragDropsTheColumnOnTheBoundaryUnderThePointer() => TestHost.RunAsync(async () =>
    {
        DragHarness h = await DragHarness.LoadAsync();
        int events = 0;
        h.Table.LayoutChanged += (_, kind) =>
        {
            Assert.AreEqual(TableLayoutChangeKind.ColumnMove, kind);
            events++;
        };

        await h.MoveAsync(100);
        await h.PressAsync(100);

        Assert.AreEqual("a", h.PressedColumn(), "the press armed a drag on the first header");

        await h.MoveAsync(160);
        await h.MoveAsync(520);

        Assert.AreEqual(Visibility.Visible, h.Marker.Visibility, "the destination is shown");
        Assert.AreEqual(600 - (h.Marker.Width / 2), h.MarkerX(), 0.01, "after the last column");
        Assert.AreEqual(0.8, h.HeaderOpacity(0), 0.001,
            "the dragged header carries the platform's drag opacity");

        await h.ReleaseAsync(520);

        CollectionAssert.AreEqual(new[] { "b", "c", "a" }, TableHarness.Order(h.Table));
        Assert.AreEqual(1, events, "one completed move, one notification");
        Assert.AreEqual(Visibility.Collapsed, h.Marker.Visibility);
        Assert.AreEqual(1.0, h.HeaderOpacity(0), 0.001, "and the dimmed header cell is opaque again");
    });

    [TestMethod]
    public Task ARealPressBelowTheThresholdChangesNothing() => TestHost.RunAsync(async () =>
    {
        DragHarness h = await DragHarness.LoadAsync();
        int events = 0;
        h.Table.LayoutChanged += (_, _) => events++;

        await h.MoveAsync(100);
        await h.PressAsync(100);
        await h.MoveAsync(102);
        await h.ReleaseAsync(102);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(h.Table));
        Assert.AreEqual(Visibility.Collapsed, h.Marker.Visibility, "no drag ever started");
        Assert.AreEqual(0, events);
    });

    [TestMethod]
    public Task EscapeCancelsARealDrag() => TestHost.RunAsync(async () =>
    {
        DragHarness h = await DragHarness.LoadAsync();
        int events = 0;
        h.Table.LayoutChanged += (_, _) => events++;

        await h.MoveAsync(100);
        await h.PressAsync(100);
        await h.MoveAsync(520);

        Assert.AreEqual(Visibility.Visible, h.Marker.Visibility);

        await h.EscapeAsync();

        Assert.AreEqual(Visibility.Collapsed, h.Marker.Visibility, "the drag is over");
        Assert.AreEqual(1.0, h.HeaderOpacity(0), 0.001);

        await h.ReleaseAsync(520);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(h.Table));
        Assert.AreEqual(0, events);
    });

    [TestMethod]
    public Task ARealDropBackOnItsOwnPlaceReportsNothing() => TestHost.RunAsync(async () =>
    {
        DragHarness h = await DragHarness.LoadAsync();
        int events = 0;
        h.Table.LayoutChanged += (_, _) => events++;

        await h.MoveAsync(100);
        await h.PressAsync(100);
        await h.MoveAsync(180);
        await h.ReleaseAsync(180);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(h.Table));
        Assert.AreEqual(0, events, "the drop asked for the place it came from");
    });
}

/// <summary>
/// A loaded table whose header can be driven with real mouse messages, and the reflection needed
/// to read what the strip did with them.
/// </summary>
internal sealed class DragHarness
{
    private const int MouseMove = 0x0001;
    private const int LeftDown = 0x0002;
    private const int LeftUp = 0x0004;
    private const int VirtualDesk = 0x4000;
    private const int Absolute = 0x8000;

    private const int VirtualScreenX = 76;
    private const int VirtualScreenY = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;

    private DragHarness(TableView table, TableHeaderStrip strip, InputInjector injector, IntPtr window)
    {
        Table = table;
        Strip = strip;
        Injector = injector;
        Window = window;
    }

    internal TableView Table { get; }

    internal TableHeaderStrip Strip { get; }

    private InputInjector Injector { get; }

    private IntPtr Window { get; }

    internal FrameworkElement Marker => (FrameworkElement)Field(Strip, "_marker")!;

    /// <summary>Three 200 DIP columns in the live window, with the window in the foreground.</summary>
    internal static async Task<DragHarness> LoadAsync(Action<TableView>? configure = null)
    {
        TableView table = TestData.Table(
            TestData.Column("a", 200), TestData.Column("b", 200), TestData.Column("c", 200));
        table.Width = 700;
        table.Height = 200;
        configure?.Invoke(table);

        await TableHarness.LoadAsync(table);
        table.UpdateLayout();

        IntPtr window = Win32Interop.GetWindowFromWindowId(
            table.XamlRoot.ContentIslandEnvironment.AppWindowId);

        SetForegroundWindow(window);
        await Task.Delay(250);

        if (GetForegroundWindow() != window)
        {
            Assert.Inconclusive(
                "The test window could not take the foreground, so real mouse messages could not " +
                "be injected without sending them to another application.");
        }

        TableHeaderStrip strip = Descendants<TableHeaderStrip>(table).First();
        InputInjector injector = InputInjector.TryCreate()
            ?? throw new AssertFailedException("InputInjector.TryCreate() returned null.");

        DragHarness harness = new(table, strip, injector, window);
        harness.RefuseIfTheWindowIsNotUnderThePointer(100);
        return harness;
    }

    internal Task MoveAsync(double x) => InjectAsync(x, MouseMove);

    internal Task PressAsync(double x) => InjectAsync(x, MouseMove | LeftDown);

    internal Task ReleaseAsync(double x) => InjectAsync(x, MouseMove | LeftUp);

    internal Task EscapeAsync() => KeyAsync(VirtualKey.Escape);

    internal async Task KeyAsync(VirtualKey key)
    {
        Injector.InjectKeyboardInput(new[]
        {
            new InjectedInputKeyboardInfo { VirtualKey = (ushort)key },
            new InjectedInputKeyboardInfo
            {
                VirtualKey = (ushort)key,
                KeyOptions = InjectedInputKeyOptions.KeyUp,
            },
        });

        await Task.Delay(120);
    }

    /// <summary>The ID of the column the strip's live gesture is acting on.</summary>
    internal string? PressedColumn()
    {
        object? column = Field(Strip, "_column");
        return column is null ? null : (string?)column.GetType()
            .GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(column);
    }

    internal double MarkerX() => ((TranslateTransform)Field(Strip, "_markerOffset")!).X;

    /// <summary>Opacity of the header cell's template root, which the drag state sets.</summary>
    internal double HeaderOpacity(int visibleIndex)
    {
        Panel panel = (Panel)Field(Strip, "_panel")!;
        FrameworkElement cell = (FrameworkElement)panel.Children[visibleIndex];
        return ((FrameworkElement)VisualTreeHelper.GetChild(cell, 0)).Opacity;
    }

    private async Task InjectAsync(double x, int options)
    {
        (int screenX, int screenY) = ScreenPoint(x);
        int left = GetSystemMetrics(VirtualScreenX);
        int top = GetSystemMetrics(VirtualScreenY);

        Injector.InjectMouseInput(new[]
        {
            new InjectedInputMouseInfo
            {
                DeltaX = Normalize(screenX - left, GetSystemMetrics(VirtualScreenWidth)),
                DeltaY = Normalize(screenY - top, GetSystemMetrics(VirtualScreenHeight)),
                MouseOptions = (InjectedInputMouseOptions)(options | Absolute | VirtualDesk),
            },
        });

        await Task.Delay(80);
    }

    private static int Normalize(int position, int extent) =>
        (int)Math.Round(position * 65535.0 / Math.Max(1, extent - 1));

    /// <summary>A header-strip x, in screen pixels.</summary>
    private (int X, int Y) ScreenPoint(double x)
    {
        Point inWindow = Strip
            .TransformToVisual(null)
            .TransformPoint(new Point(x, Strip.ActualHeight / 2));

        double scale = Strip.XamlRoot.RasterizationScale;
        POINT point = new()
        {
            X = (int)Math.Round(inWindow.X * scale),
            Y = (int)Math.Round(inWindow.Y * scale),
        };

        ClientToScreen(Window, ref point);
        return (point.X, point.Y);
    }

    /// <summary>
    /// An injected click goes to whatever window sits at that screen point. If the test window is
    /// not the one there, the click would land in another application, so the test stops instead.
    /// </summary>
    private void RefuseIfTheWindowIsNotUnderThePointer(double x)
    {
        (int screenX, int screenY) = ScreenPoint(x);
        IntPtr atPoint = WindowFromPoint(new POINT { X = screenX, Y = screenY });

        if (GetAncestor(atPoint, 2) != Window)
        {
            Assert.Inconclusive(
                "Another window covers the test window's header, so mouse messages could not be " +
                "injected without clicking on it.");
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    private static object? Field(object target, string name) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
