using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureConnections(Torrent target, List<object> outcomes, List<string> completed)
    {
        await ShowTorrents();
        Model.Filter = TorrentFilter.All;
        await SelectTorrent(new Syno.TableView.Selection([], null));
        Require(!SelectedMap.CanExecute(null), "The selected map was available without a selection.");
        Require(AllMap.CanExecute(null), "The global map was unavailable without a selection.");
        await SelectTorrent(new Syno.TableView.Selection([target], target));
        Model.OpenInspector();
        Model.Inspector.Select(InspectorSection.General);
        Model.IsFilterOpen = true;
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading);
        var pane = (InspectorPane)InspectorContent.Content;
        pane.Layout = pane.Layout with { View = PeerView.Default with { Layout = PeerLayout.Table } };
        Model.IsInspectorMaximized = false;
        await CapturePage("connections-before", pane);
        var view = pane.Layout.View;
        var height = _splitHeight;

        await Open(SelectedMapButton);
        Require(!Model.Inspector.ShowsAllPeers && Model.Inspector.Target == target,
            "The selected map did not inspect the selected torrent.");
        Require(Model.Selected.SequenceEqual([target]), "Opening a map changed the transfer selection.");
        var origin = _mapReturn;
        var menu = (IExpandCollapseProvider)FrameworkElementAutomationPeer
            .CreatePeerForElement(ViewMenu).GetPattern(PatternInterface.ExpandCollapse);
        menu.Expand();
        await CaptureLayout();
        CaptureInvoke(ViewMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Command == AllMap));
        await CaptureLayout();
        Require(ReferenceEquals(origin, _mapReturn), "Switching map scope replaced the original return point.");
        Require(Model.Inspector.IsGlobal && Model.Inspector.ShowsAllPeers,
            "The global map remained limited to the selected torrent.");
        await CapturePage("connections-all", pane);
        CaptureInvoke(BackButton);
        await CaptureLayout();
        Require(_mapReturn is null && !Model.Navigation.HasBack, "Back did not consume the map visit.");
        Require(Model.Inspector.Target == target && Model.Inspector.Section == InspectorSection.General,
            "Back did not restore the inspected torrent and section.");
        Require(Model.IsFilterOpen && !Model.IsInspectorMaximized && pane.Layout.View == view,
            "Back did not restore filters, inspector size and peer view.");
        Require(Math.Abs(_splitHeight - height) < 1, "Back lost the inspector split height.");
        outcomes.Add(new { journey = "selected and global map buttons, scope switch and Back", restored = true });

        await ShowConnections(true);
        Run(Model.Properties);
        await CaptureLayout();
        Require(_mapReturn is null && !Model.Navigation.HasBack,
            "Properties changed the inspected scope but retained the map return point.");
        Require(!await Model.Navigation.GoBack(), "Back undid the explicit Properties command.");
        outcomes.Add(new { journey = "Properties cancels map navigation", canceled = true });

        await SelectTorrent(new Syno.TableView.Selection([target], target));
        await ShowConnections(false);
        Model.Filter = MainViewModel.Matches(target, TorrentFilter.Downloading)
            ? TorrentFilter.Seeding
            : TorrentFilter.Downloading;
        await CaptureLayout();
        await Model.Select([], null);
        Require(!Model.Inspector.IsGlobal && Model.Inspector.PeerSelection?.Single() == target,
            "A dropped table selection broadened the selected map to every transfer.");
        await GoBack();
        Model.Filter = TorrentFilter.All;
        Model.CloseInspector();
        await Model.Navigation.Navigate(WindowPage.Library);
        Model.IsFilterOpen = true;
        await ShowConnections(true);
        await ShowSettings(new(SettingsCategory.General));
        await GoBack();
        Require(_mapReturn is not null && Model.Inspector.IsGlobal,
            "Returning from Settings lost the global map visit.");
        await GoBack();
        Require(Model.Page == WindowPage.Library && Model.IsFilterOpen && !Model.HasInspector && !Model.Navigation.HasBack,
            "The global map did not return to the original Library layout.");
        outcomes.Add(new { journey = "selected scope survives dropped rows and global map returns to Library through Settings", restored = true });

        await ShowTorrents();
        await SelectTorrent(new Syno.TableView.Selection([target], target));
        Model.OpenInspector();
        Model.Inspector.Select(InspectorSection.General);
        await ShowConnections(false);
        Model.FileDraft.Begin([target], FileAction.Remove, DeletionMode.Recycle);
        Model.FileDraft.DeleteDownloaded = false;
        Model.FileDraft.DeleteTorrentFiles = false;
        Require(await Model.FileDraft.Submit(), "The disposable transfer could not be removed.");
        await CaptureReady(Model, () => !Model.Contains(target));
        await GoBack();
        Require(Model.Inspector.Target is null && Model.Inspector.Section == InspectorSection.Peers,
            "Back revived a removed transfer or restored a section that requires it.");
        Require(!Model.Inspector.HasError, "Back showed an error for the removed return target.");
        Model.Inspector.Show(null, InspectorSection.Speed, selection: [target]);
        Model.Inspector.Select(InspectorSection.Speed);
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading
            && (Model.Inspector.History is not null || Model.Inspector.HasError));
        Require(!Model.Inspector.IsGlobal && !Model.Inspector.HasError && Model.Inspector.History!.Count == 0,
            "An exhausted fixed scope failed or displayed global speed history.");
        outcomes.Add(new { journey = "removed return target and exhausted fixed speed scope", restored = true });
        completed.Add("connections");

        async Task Open(Control button)
        {
            CaptureInvoke(button);
            await CaptureLayout();
            Require(_mapReturn is not null && Model.FilterPane == FilterPane.Hidden,
                "The map command did not hide filters and create a return point.");
            Require(Model.HasInspector && !Model.IsInspectorCompact && Model.IsInspectorMaximized,
                "The map command did not maximise the existing inspector.");
            Require(Model.Inspector.Section == InspectorSection.Peers && pane.Layout.View.Layout == PeerLayout.Map,
                "The map command did not show peers as map only.");
            Require(BackButton.Visibility == Visibility.Visible, "The map visit did not show the navigation Back button.");
        }

        static void Require(bool condition, string failure)
        {
            if (!condition)
                throw new InvalidOperationException(failure);
        }
    }
}
