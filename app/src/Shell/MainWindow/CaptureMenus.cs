using System.Diagnostics;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureMenus(Torrent target, List<object> outcomes)
    {
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        await CaptureLayout();
        var preparing = Stopwatch.StartNew();
        while (FileMenu.Items.Count == 0 || !TorrentMenu.Items.OfType<MenuFlyoutItem>()
            .Any(item => item.Command == Model.SwitchSequential))
        {
            if (preparing.Elapsed > TimeSpan.FromSeconds(20))
                throw new InvalidOperationException("The torrent menu did not finish preparing.");
            await Task.Delay(20);
            await CaptureLayout();
        }

        var file = Expand(FileMenu);
        await CaptureLayout();
        var baseline = Offset(FileMenu.Items.OfType<MenuFlyoutItem>().First());
        file.Collapse();
        await CaptureLayout();

        // View intentionally combines content icons and native state. Its
        // larger text offset proves that the gutter measurement detects both.
        var view = Expand(ViewMenu);
        try
        {
            await CaptureLayout();
            if (!ViewMenu.Items.OfType<ToggleMenuFlyoutItem>().Any()
                || !ViewMenu.Items.OfType<RadioMenuFlyoutItem>().Any())
                throw new InvalidOperationException("View lost its native menu choices.");
            Measure(ViewMenu.Items, "view", compact: false);
            await CapturePage("menu-view");
        }
        finally
        {
            view.Collapse();
        }
        await CaptureLayout();

        var torrent = Expand(TorrentMenu);
        try
        {
            await CaptureLayout();
            Measure(TorrentMenu.Items, "torrent", compact: true);
            await CapturePage("menu-torrent");
        }
        finally
        {
            torrent.Collapse();
        }
        await CaptureLayout();

        MenuEntry[] entries = [];
        var menu = MenuItems.Flyout(() => entries, () => Model.Text);
        foreach (bool? state in new bool?[] { false, true, null })
        {
            entries =
            [
                new MenuHeading(target.Name, Segoe.ListChecks),
                .. Selection(properties: true).Select(entry => entry is MenuChoice choice
                    ? choice with { State = state }
                    : entry),
            ];
            menu.ShowAt(Root);
            try
            {
                await CaptureLayout();
                var name = state is null ? "mixed" : state.Value ? "on" : "off";
                Measure(menu.Items, name, compact: true);
                await CapturePage("menu-context-" + name);
            }
            finally
            {
                menu.Hide();
            }
            await CaptureLayout();
        }

        void Measure(IList<MenuFlyoutItemBase> items, string name, bool compact)
        {
            var offsets = items.OfType<MenuFlyoutItem>()
                .Where(item => item.DataContext is MenuCommand or MenuChoice)
                .Select(Offset).ToArray();
            if (offsets.Length == 0 || offsets.Any(offset =>
                compact ? Math.Abs(offset - baseline) > 0.5 : offset <= baseline + 0.5))
                throw new InvalidOperationException($"The {name} menu has the wrong checkmark column width.");
            outcomes.Add(new { menu = name, baseline, offsets, compact });
        }

        static double Offset(MenuFlyoutItem item) => CaptureElements(item)
            .OfType<TextBlock>().Single(label => label.Text == item.Text)
            .TransformToVisual(item).TransformPoint(new Windows.Foundation.Point()).X;

        static IExpandCollapseProvider Expand(MenuBarItem item)
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
            var expand = (IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse);
            expand.Expand();
            return expand;
        }
    }
}
