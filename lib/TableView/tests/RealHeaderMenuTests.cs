using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.System;
using Windows.UI.Input.Preview.Injection;

namespace Syno.TableView.Tests;

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
            Table table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            table.Width = 700;
            table.Height = 200;

            await TableHarness.LoadAsync(table);
            table.UpdateLayout();

            InputInjector injector = await Proof.TakeForegroundAsync(table);

            Control header = HeaderCell(table, 1);
            Assert.IsTrue(header.Focus(FocusState.Keyboard), "the second header takes focus");

            await PressAsync(injector, key, modifier);

            Assert.AreEqual(
                1,
                VisualTreeHelper.GetOpenPopupsForXamlRoot(table.XamlRoot).Count,
                "the key opened one menu"
            );

            await PressAsync(injector, VirtualKey.Escape, null);

            Assert.AreEqual(
                0,
                VisualTreeHelper.GetOpenPopupsForXamlRoot(table.XamlRoot).Count,
                "Escape closed it"
            );
            Assert.AreSame(
                header,
                FocusManager.GetFocusedElement(table.XamlRoot),
                "closure returns focus to the invoking header"
            );
        });

    private static Control HeaderCell(Table table, int visibleIndex)
    {
        Header.Strip strip = Proof.Descendant<Header.Strip>(table)!;
        Panel panel = (Panel)
            strip
                .GetType()
                .GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(strip)!;

        return (Control)panel.Children[visibleIndex];
    }

    private static async Task PressAsync(
        InputInjector injector,
        VirtualKey key,
        VirtualKey? modifier
    )
    {
        Proof.Press(injector, key, modifier);
        await Task.Delay(250);
    }
}
