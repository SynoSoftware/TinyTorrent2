using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;
using Windows.System;
using Windows.UI.Input.Preview.Injection;

namespace Synapse_Tests;

/// <summary>
/// Section 12's keyboard path end to end: real key messages through the window, so what is proved
/// is that the platform raises a context request for these keys on a focused header and that the
/// strip answers it. <c>ContextRequestedEventArgs</c> cannot be constructed, so nothing else can
/// exercise the opening itself.
/// </summary>
[TestClass]
// Interactive: these inject real Windows input, so they take the pointer and keyboard away
// from whoever is at the machine. Excluded from the default run by .runsettings; ask for them
// deliberately with --filter TestCategory=Interactive when the input path has changed.
[TestCategory("Interactive")]
public class RealHeaderMenuTests
{
    /// <summary>The Menu key. <c>VirtualKey</c> names it <c>Application</c>.</summary>
    private const VirtualKey MenuKey = VirtualKey.Application;

    [TestMethod]
    public Task TheMenuKeyOpensTheHeaderMenuOnTheFocusedHeader() =>
        OpenAndCloseAsync(MenuKey, modifier: null);

    [TestMethod]
    public Task ShiftF10OpensTheHeaderMenuOnTheFocusedHeader() =>
        OpenAndCloseAsync(VirtualKey.F10, VirtualKey.Shift);

    private static Task OpenAndCloseAsync(VirtualKey key, VirtualKey? modifier) =>
        TestHost.RunAsync(async () =>
        {
            TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            table.Width = 700;
            table.Height = 200;

            await TableHarness.LoadAsync(table);
            table.UpdateLayout();

            InputInjector injector = await FocusWindowAndCreateInjectorAsync(table);

            Control header = HeaderCell(table, 1);
            Assert.IsTrue(header.Focus(FocusState.Keyboard), "the second header takes focus");

            await PressAsync(injector, key, modifier);

            Assert.AreEqual("Hide column “B”", FirstOpenMenuItem(table).Text,
                "the header menu opened on the focused header");

            await PressAsync(injector, VirtualKey.Escape, null);

            Assert.AreEqual(
                0,
                VisualTreeHelper.GetOpenPopupsForXamlRoot(table.XamlRoot).Count,
                "Escape closed it");
            Assert.AreSame(header, FocusManager.GetFocusedElement(table.XamlRoot),
                "closure returns focus to the invoking header");
        });

    private static Control HeaderCell(TableView table, int visibleIndex)
    {
        TableHeaderStrip strip = Descendants<TableHeaderStrip>(table).First();
        Panel panel = (Panel)strip.GetType()
            .GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(strip)!;

        return (Control)panel.Children[visibleIndex];
    }

    private static MenuFlyoutItem FirstOpenMenuItem(TableView table)
    {
        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(table.XamlRoot))
        {
            if (popup.Child is DependencyObject child
                && Descendants<MenuFlyoutItem>(child).FirstOrDefault() is MenuFlyoutItem item)
            {
                return item;
            }
        }

        throw new AssertFailedException("No flyout is open.");
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

    private static async Task PressAsync(InputInjector injector, VirtualKey key, VirtualKey? modifier)
    {
        List<InjectedInputKeyboardInfo> keys = new();
        if (modifier is VirtualKey held)
        {
            keys.Add(Info(held, up: false));
        }

        keys.Add(Info(key, up: false));
        keys.Add(Info(key, up: true));
        if (modifier is VirtualKey release)
        {
            keys.Add(Info(release, up: true));
        }

        injector.InjectKeyboardInput(keys);
        await Task.Delay(250);
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
