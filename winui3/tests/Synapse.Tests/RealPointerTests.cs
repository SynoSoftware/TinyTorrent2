using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;
using Windows.Foundation;
using Windows.UI.Input.Preview.Injection;

namespace Synapse_Tests;

/// <summary>
/// The pointer arbiter driven by real mouse messages: press, threshold, release, and the deferred
/// plain click that lets a drag keep the whole packet.
/// </summary>
/// <remarks>
/// Mouse buttons cannot be injected while the desktop session is locked. Each test refuses to
/// pretend: it reports inconclusive rather than claiming a result it did not observe.
/// </remarks>
[TestClass]
// Interactive: these inject real Windows input, so they take the pointer and keyboard away
// from whoever is at the machine. Excluded from the default run by .runsettings; ask for them
// deliberately with --filter TestCategory=Interactive when the input path has changed.
[TestCategory("Interactive")]
public class RealPointerTests
{
    [TestMethod]
    public Task PlainClickCtrlClickAndShiftClickFollowSection13() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(8, height: 400);
        Mouse mouse = await Mouse.CreateAsync(h);

        await mouse.ClickRowAsync(h, 1);
        CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys());
        CollectionAssert.AreEqual(new[] { "k1" }, h.ContainerSelectedKeys());

        await mouse.ClickRowAsync(h, 4, ctrl: true);
        CollectionAssert.AreEqual(new[] { "k1", "k4" }, h.SelectedKeys());
        CollectionAssert.AreEqual(new[] { "k1", "k4" }, h.ContainerSelectedKeys());

        await mouse.ClickRowAsync(h, 6, shift: true);
        CollectionAssert.AreEqual(
            new[] { "k4", "k5", "k6" },
            h.SelectedKeys(),
            "Shift extends from the k4 anchor that the Ctrl-click set.");

        await mouse.ClickRowAsync(h, 2);
        CollectionAssert.AreEqual(
            new[] { "k2" }, h.SelectedKeys(), "A plain click replaces, whatever the container did.");
    });

    [TestMethod]
    public Task PressingAnAlreadySelectedRowKeepsThePacketUntilRelease() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(8, height: 400);
        Mouse mouse = await Mouse.CreateAsync(h);

        h.Table.Selection = new(new object[] { h[1], h[2], h[3] }, h[1]);

        await mouse.PressRowAsync(h, 2);
        CollectionAssert.AreEqual(
            new[] { "k1", "k2", "k3" },
            h.SelectedKeys(),
            "Press alone must not collapse the packet a drag would carry.");
        CollectionAssert.AreEqual(new[] { "k1", "k2", "k3" }, h.ContainerSelectedKeys());

        await mouse.ReleaseAsync();
        CollectionAssert.AreEqual(
            new[] { "k2" }, h.SelectedKeys(), "Release inside the threshold is the plain click.");
    });

    /// <summary>
    /// A real drag of the selected packet. It asserts the request the drop raises, not only that
    /// the selection stood still: a drag that died at the threshold leaves the selection alone too,
    /// and the earlier version of this test could not tell the two apart.
    /// </summary>
    [TestMethod]
    public Task DraggingTheSelectionRaisesOneReorderRequest() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            8, configure: t => t.IsRowReorderingEnabled = true, height: 400);
        List<TableRowsReorderRequestedEventArgs> requests = new();
        h.Table.RowsReorderRequested += (_, e) => requests.Add(e);
        Mouse mouse = await Mouse.CreateAsync(h);

        h.Table.Selection = new(new object[] { h[1], h[2], h[3] }, h[1]);
        h.Events = 0;
        double rowHeight = ((FrameworkElement)h.HostedList().ContainerFromItem(h[0])).ActualHeight;

        // Three rows down: a drop inside the packet's own block asks for no change and raises
        // nothing, so the pointer has to leave the block before the request can exist.
        await mouse.PressRowAsync(h, 2);
        await mouse.MoveByAsync(0, 3 * rowHeight);
        await mouse.ReleaseAsync();

        CollectionAssert.AreEqual(
            new[] { "k1", "k2", "k3" },
            h.SelectedKeys(),
            "The gesture went to the drag branch, so the deferred click never ran.");
        Assert.AreEqual(0, h.Events);
        Assert.AreEqual(1, requests.Count, "the drop raised exactly one request");
        CollectionAssert.AreEqual(
            new object[] { h[1], h[2], h[3] }, requests[0].MovingItems.ToArray());
    });

    /// <summary>
    /// A real marquee from the space beside the columns, on a row's own line, swept down over the
    /// rows below: the owner's gesture, and the one the 56% dead zone turned into a surprise. The
    /// harness table is 320 wide with two 120-wide columns, so 80 pixels of that space exist.
    /// </summary>
    [TestMethod]
    public Task DraggingDownBesideTheColumnsSweeps() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(8, height: 400);
        List<TableRowsReorderRequestedEventArgs> requests = new();
        h.Table.RowsReorderRequested += (_, e) => requests.Add(e);
        Mouse mouse = await Mouse.CreateAsync(h);

        double rowHeight = ((FrameworkElement)h.HostedList().ContainerFromItem(h[0])).ActualHeight;

        await mouse.PressBesideRowAsync(h, 1);
        Assert.AreEqual(0, h.SelectedKeys().Length, "a press on empty surface selects nothing");

        await mouse.MoveByAsync(0, 2 * rowHeight);
        CollectionAssert.AreEqual(
            new[] { "k1", "k2", "k3" },
            h.SelectedKeys(),
            "two rows down, the rectangle covers the row it started beside and the two beneath");

        await mouse.ReleaseAsync();
        CollectionAssert.AreEqual(new[] { "k1", "k2", "k3" }, h.SelectedKeys());
        Assert.AreEqual(0, requests.Count, "a sweep never asks to move anything");
    });

    /// <summary>
    /// A real drag down a row the table would not drag. With reordering withheld nothing competes
    /// for the gesture, so it is the sweep, from the row it started on.
    /// </summary>
    [TestMethod]
    public Task DraggingDownARowThatCannotBeDraggedSweeps() => TestHost.RunAsync(async () =>
    {
        // Both defaults, stated rather than set: the marquee is on and reordering is off.
        SelectionHarness h = await SelectionHarness.LoadAsync(8, height: 400);
        List<TableRowsReorderRequestedEventArgs> requests = new();
        h.Table.RowsReorderRequested += (_, e) => requests.Add(e);
        Mouse mouse = await Mouse.CreateAsync(h);

        double rowHeight = ((FrameworkElement)h.HostedList().ContainerFromItem(h[0])).ActualHeight;

        await mouse.PressRowAsync(h, 1);
        CollectionAssert.AreEqual(
            new[] { "k1" }, h.SelectedKeys(), "the press selects the row, as a click would");

        await mouse.MoveByAsync(0, 2 * rowHeight);
        CollectionAssert.AreEqual(
            new[] { "k1", "k2", "k3" },
            h.SelectedKeys(),
            "two rows down, the rectangle covers the row it started on and the two beneath");

        await mouse.ReleaseAsync();
        CollectionAssert.AreEqual(new[] { "k1", "k2", "k3" }, h.SelectedKeys());
        Assert.AreEqual(0, requests.Count, "a sweep never asks to move anything");
    });

    /// <summary>Real mouse messages aimed at a realized row.</summary>
    private sealed class Mouse
    {
        private InputInjector _injector = null!;
        private IntPtr _hwnd;
        private double _scale;
        private FrameworkElement _table = null!;
        private Point _last;

        internal static async Task<Mouse> CreateAsync(SelectionHarness h)
        {
            IntPtr hwnd = Win32Interop.GetWindowFromWindowId(
                h.Table.XamlRoot.ContentIslandEnvironment.AppWindowId);

            SetForegroundWindow(hwnd);
            await Task.Delay(250);

            if (GetForegroundWindow() != hwnd)
            {
                Assert.Inconclusive(
                    "The test window could not take the foreground, so mouse messages could not be " +
                    "injected without sending them to another application.");
            }

            Mouse mouse = new()
            {
                _injector = InputInjector.TryCreate()
                    ?? throw new AssertFailedException("InputInjector.TryCreate() returned null."),
                _hwnd = hwnd,
                _scale = h.Table.XamlRoot.RasterizationScale,
                _table = h.Table,
            };

            // A locked session accepts keyboard input but drops mouse buttons. Prove the cursor
            // actually moves before any test claims a pointer result.
            Point probe = mouse.ScreenPointOf(h.Table, new Point(2, 2));
            POINT now = default;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                mouse.MoveTo(probe);
                await Task.Delay(150);
                GetCursorPos(out now);
                if (Math.Abs(now.X - probe.X) <= 8 && Math.Abs(now.Y - probe.Y) <= 8)
                {
                    // Win32 swallows the click that activates an inactive window, so spend one
                    // here rather than losing a test's first gesture to it.
                    await mouse.ClickRowAsync(h, 0);
                    h.Table.Selection = TableSelection.Empty;
                    h.Events = 0;
                    return mouse;
                }
            }

            Assert.Inconclusive(
                $"Injected pointer input is not reaching the desktop (asked for " +
                $"{probe.X:0},{probe.Y:0}, cursor is at {now.X},{now.Y}). The session is " +
                "probably locked.");

            return mouse;
        }

        internal async Task ClickRowAsync(SelectionHarness h, int row, bool ctrl = false, bool shift = false)
        {
            await PressRowAsync(h, row, ctrl, shift);
            await ReleaseAsync();
        }

        internal async Task PressRowAsync(SelectionHarness h, int row, bool ctrl = false, bool shift = false)
        {
            h.Table.UpdateLayout();
            FrameworkElement container = (FrameworkElement)h.HostedList().ContainerFromItem(h[row]);
            Point centre = ScreenPointOf(
                container, new Point(container.ActualWidth / 2, container.ActualHeight / 2));

            MoveTo(centre);
            await Task.Delay(80);

            if (ctrl)
            {
                Key(Windows.System.VirtualKey.Control, up: false);
            }

            if (shift)
            {
                Key(Windows.System.VirtualKey.Shift, up: false);
            }

            Button(InjectedInputMouseOptions.LeftDown);
            await Task.Delay(150);

            if (shift)
            {
                Key(Windows.System.VirtualKey.Shift, up: true);
            }

            if (ctrl)
            {
                Key(Windows.System.VirtualKey.Control, up: true);
            }
        }

        /// <summary>Press on the row surface beside the columns, level with the middle of a row.</summary>
        internal async Task PressBesideRowAsync(SelectionHarness h, int row)
        {
            h.Table.UpdateLayout();
            ListView list = h.HostedList();
            FrameworkElement container = (FrameworkElement)list.ContainerFromItem(h[row]);
            Point beside = container.TransformToVisual(list).TransformPoint(
                new Point(container.ActualWidth + 20, container.ActualHeight / 2));
            Assert.IsTrue(
                beside.X < list.ActualWidth - 8,
                "the harness table must leave space beside its columns for this gesture");

            MoveTo(ScreenPointOf(list, beside));
            await Task.Delay(80);
            Button(InjectedInputMouseOptions.LeftDown);
            await Task.Delay(150);
        }

        internal async Task MoveByAsync(double dx, double dy)
        {
            MoveTo(new Point(_last.X + (dx * _scale), _last.Y + (dy * _scale)));
            await Task.Delay(150);
        }

        internal async Task ReleaseAsync()
        {
            Button(InjectedInputMouseOptions.LeftUp);
            await Task.Delay(200);
        }

        private Point ScreenPointOf(FrameworkElement element, Point local)
        {
            Point inWindow = element.TransformToVisual(null).TransformPoint(local);
            POINT client = new() { X = (int)(inWindow.X * _scale), Y = (int)(inWindow.Y * _scale) };
            ClientToScreen(_hwnd, ref client);
            _ = _table;
            return new Point(client.X, client.Y);
        }

        private void MoveTo(Point screen)
        {
            _last = screen;
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            _injector.InjectMouseInput(new[]
            {
                new InjectedInputMouseInfo
                {
                    DeltaX = (int)Math.Round((screen.X - left) * 65535.0 / (width - 1)),
                    DeltaY = (int)Math.Round((screen.Y - top) * 65535.0 / (height - 1)),
                    MouseOptions = InjectedInputMouseOptions.Absolute
                        | InjectedInputMouseOptions.VirtualDesk
                        | InjectedInputMouseOptions.MoveNoCoalesce,
                },
            });
        }

        private void Button(InjectedInputMouseOptions options) =>
            _injector.InjectMouseInput(new[] { new InjectedInputMouseInfo { MouseOptions = options } });

        private void Key(Windows.System.VirtualKey key, bool up) =>
            _injector.InjectKeyboardInput(new[]
            {
                new InjectedInputKeyboardInfo
                {
                    VirtualKey = (ushort)key,
                    KeyOptions = up ? InjectedInputKeyOptions.KeyUp : InjectedInputKeyOptions.None,
                },
            });

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
    }
}
