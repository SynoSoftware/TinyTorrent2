using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.System;
using Windows.UI.Input.Preview.Injection;

namespace Syno.TableView.Tests;

/// <summary>
/// The keyboard path end to end: real key messages through the window, so the key mapping and the
/// focused-element gate are exercised, not just the navigation methods behind them.
/// </summary>
[TestClass]
// Interactive: these inject real Windows input, so they take the pointer and keyboard away
// from whoever is at the machine. Excluded from the default run by .runsettings; ask for them
// deliberately with --filter TestCategory=Interactive when the input path has changed.
[TestCategory("Interactive")]
public class RealKeyboardTests
{
    private const string EditorCellXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <TextBox Width="80" />
        </DataTemplate>
        """;

    [TestMethod]
    public Task RealArrowAndHomeEndKeysDriveTheCurrentRow() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(8);
        InputInjector injector = await FocusWindowAndCreateInjectorAsync(h.Table);

        FocusRow(h, 0);

        await PressAsync(injector, VirtualKey.Down);
        Assert.AreEqual("k0", h.CurrentKey(), "The first Down lands on the first row.");

        await PressAsync(injector, VirtualKey.Down);
        await PressAsync(injector, VirtualKey.Down);
        Assert.AreEqual("k2", h.CurrentKey());
        CollectionAssert.AreEqual(new[] { "k2" }, h.SelectedKeys());

        await PressAsync(injector, VirtualKey.Down, VirtualKey.Shift);
        CollectionAssert.AreEqual(new[] { "k2", "k3" }, h.SelectedKeys());

        await PressAsync(injector, VirtualKey.End);
        Assert.AreEqual("k7", h.CurrentKey());

        await PressAsync(injector, VirtualKey.Home);
        Assert.AreEqual("k0", h.CurrentKey());

        await PressAsync(injector, VirtualKey.A, VirtualKey.Control);
        Assert.AreEqual(8, h.SelectedKeys().Length);
    });

    [TestMethod]
    public Task RealEnterInvokesTheCurrentRow() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(8);
        InputInjector injector = await FocusWindowAndCreateInjectorAsync(h.Table);

        FocusRow(h, 0);
        await PressAsync(injector, VirtualKey.Down);
        await PressAsync(injector, VirtualKey.Enter);

        Assert.AreEqual(1, h.Invoked.Count);
        Assert.AreEqual("k0", ((Row)h.Invoked[0]).Key);
    });

    [TestMethod]
    public Task CtrlArrowAndSpaceBuildADiscontiguousSelection() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(8);
        InputInjector injector = await FocusWindowAndCreateInjectorAsync(h.Table);
        h.Click(h[1]);
        FocusRow(h, 1);

        await PressAsync(injector, VirtualKey.Down, VirtualKey.Control);
        await PressAsync(injector, VirtualKey.Down, VirtualKey.Control);
        CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys());
        Assert.AreEqual("k3", h.CurrentKey());

        await PressAsync(injector, VirtualKey.Space, VirtualKey.Control);
        CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.SelectedKeys());
        CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.ContainerSelectedKeys());
        await PressAsync(injector, VirtualKey.Space, VirtualKey.Control);
        CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys());
    });

    [TestMethod]
    public Task RealArrowKeysDoNotMoveTheCurrentRowFromInsideACellEditor() => TestHost.RunAsync(async () =>
    {
        DataTemplate cell = (DataTemplate)XamlReader.Load(EditorCellXaml);
        SelectionHarness h = await SelectionHarness.LoadAsync(8, t => t.Columns[0].CellTemplate = cell);
        h.Table.UpdateLayout();
        await Task.Delay(200);

        InputInjector injector = await FocusWindowAndCreateInjectorAsync(h.Table);

        FocusRow(h, 0);
        await PressAsync(injector, VirtualKey.Down);
        await PressAsync(injector, VirtualKey.Down);
        Assert.AreEqual("k1", h.CurrentKey());

        ListViewItem container = (ListViewItem)h.HostedList().ContainerFromItem(h[3]);
        TextBox editor = SelectionHarness.Descendant<TextBox>(container)
            ?? throw new AssertFailedException("The cell editor was not realized.");
        Assert.IsTrue(editor.Focus(FocusState.Programmatic));

        // One key press. A single-line TextBox leaves Down unhandled, so gating on Handled would
        // move the current row here.
        await PressAsync(injector, VirtualKey.Down);
        await PressAsync(injector, VirtualKey.Space);
        Assert.AreEqual(" ", editor.Text, "Space must remain text input in a cell editor.");

        Assert.AreEqual(
            "k1",
            h.CurrentKey(),
            "The table must not act on a key that arrived while the caret was in a cell editor.");
    });

    // ------------------------------------------------------------------ plumbing

    private static void FocusRow(SelectionHarness h, int index)
    {
        h.Table.UpdateLayout();
        ListViewItem container = (ListViewItem)h.HostedList().ContainerFromItem(h[index]);
        Assert.IsTrue(container.Focus(FocusState.Keyboard));
    }

    /// <summary>
    /// Injected keys go to the foreground window, so the test refuses to inject anything until the
    /// test window actually holds it.
    /// </summary>
    private static async Task<InputInjector> FocusWindowAndCreateInjectorAsync(FrameworkElement element)
    {
        IntPtr hwnd = Win32Interop.GetWindowFromWindowId(
            element.XamlRoot.ContentIslandEnvironment.AppWindowId);

        SetForegroundWindow(hwnd);
        await Task.Delay(250);

        if (GetForegroundWindow() != hwnd)
        {
            Assert.Inconclusive(
                "The test window could not take the foreground, so real key messages could not be " +
                "injected without sending them to another application.");
        }

        return InputInjector.TryCreate()
            ?? throw new AssertFailedException("InputInjector.TryCreate() returned null.");
    }

    private static async Task PressAsync(InputInjector injector, VirtualKey key, VirtualKey? modifier = null)
    {
        List<InjectedInputKeyboardInfo> down = new();
        if (modifier is VirtualKey held)
        {
            down.Add(Info(held, up: false));
        }

        down.Add(Info(key, up: false));
        down.Add(Info(key, up: true));
        if (modifier is VirtualKey release)
        {
            down.Add(Info(release, up: true));
        }

        injector.InjectKeyboardInput(down);
        await Task.Delay(120);
    }

    private static InjectedInputKeyboardInfo Info(VirtualKey key, bool up) => new()
    {
        VirtualKey = (ushort)key,
        KeyOptions = up ? InjectedInputKeyOptions.KeyUp : InjectedInputKeyOptions.None,
    };

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
