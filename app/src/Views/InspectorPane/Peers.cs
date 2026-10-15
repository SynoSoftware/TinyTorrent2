using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Windows.Foundation;

namespace Syno.TinyTorrent.Views;

public sealed partial class InspectorPane
{
    private PeerMap? _peerMap;
    private bool _mapQueued;
    private bool _arrangingPeers;
    private bool _peersStacked;
    private readonly Button _peerMore = CreateAction(Lucide.Ellipsis, "PeerMore");

    private void SetupPeers()
    {
        _peerMore.Flyout = MenuItems.Flyout(PeerMenu, () => Model.Text);
        Peers.SelectionChanged += (_, selection) => _peerMap?.Select(selection.Current as Peer);
        PeerSplit.ValueChanged += (_, value) =>
        {
            if (_arrangingPeers)
                return;
            var length = (_peersStacked ? PeerContent.ActualHeight : PeerContent.ActualWidth) - 16;
            if (length <= 0)
                return;
            var fraction = Math.Clamp(value / length, .15, .85);
            var view = _layout.View;
            if (_peersStacked)
                view = view with { Stacked = fraction };
            else
                view = view with { Side = fraction };
            _layout = _layout with { View = view };
            ArrangePeers();
        };
        RefreshPeerActions();
        ArrangePeers(fit: true);
        RefreshMap();
    }

    private List<MenuEntry> PeerMenu()
    {
        var text = Model.Text;
        var view = _layout.View;
        var installed = Model.HasCities;
        List<MenuEntry> menu =
        [
            new MenuHeading(text.Get("peer_map", "view"), Lucide.Columns2),
            Choice("map", view.Layout == PeerLayout.Map, view with { Layout = PeerLayout.Map }),
            Choice("table", view.Layout == PeerLayout.Table, view with { Layout = PeerLayout.Table }),
            Choice("both", view.Layout == PeerLayout.Both, view with { Layout = PeerLayout.Both }),
            new MenuSeparator(),
            new MenuHeading(text.Get("peer_map", "geography"), Lucide.Globe2),
            Choice("countries", !view.Cities || !installed, view with { Cities = false }),
            Choice("cities", view.Cities && installed, view with { Cities = true }, installed),
        ];
        if (!installed)
            menu.Add(new MenuCommand(text.Get("peer_map", "download"), Lucide.Download, Model.DownloadCities));
        return menu;

        MenuChoice Choice(string key, bool selected, PeerView choice, bool enabled = true) =>
            new(text.Get("peer_map", key), null, selected,
                new RelayCommand(() => SetPeerView(choice), () => enabled));
    }

    private void SetPeerView(PeerView view)
    {
        if (_layout.View == view)
            return;
        _layout = _layout with { View = view };
        ArrangePeers(fit: true);
        RefreshMap();
    }

    private void RefreshPeerActions()
    {
        var text = Model.Text;
        AutomationProperties.SetName(_peerMore, text.Get("peer_map", "options"));
        ToolTipService.SetToolTip(_peerMore, text.Get("peer_map", "options"));
        if (PeerSplit is not null)
            AutomationProperties.SetName(PeerSplit, text.Get("peer_map", "resize"));
    }

    private void OnPeerSize(object sender, SizeChangedEventArgs args) => ArrangePeers(fit: true);

    private void ArrangePeers(bool fit = false)
    {
        if (PeerContent is null || _arrangingPeers)
            return;
        _arrangingPeers = true;
        try
        {
            var view = _layout.View;
            var combined = view.Layout == PeerLayout.Both;
            if (fit && combined)
            {
                var width = PeerContent.ActualWidth;
                var height = PeerContent.ActualHeight;
                var side = PeerMap.FitScale(new Size(Math.Max(0, width - 16) * (1 - view.Side), height));
                var below = PeerMap.FitScale(new Size(width, Math.Max(0, height - 16) * (1 - view.Stacked)));
                if (side != below)
                    _peersStacked = below > side;
            }
            var stacked = combined && _peersStacked;
            var showsMap = view.Layout != PeerLayout.Table && Model.Section == InspectorSection.Peers
                && Model.IsVisible && Model.HasContent && _library is null;
            if (Peers.Visibility == Visibility.Collapsed && view.Layout != PeerLayout.Map)
                Peers.RefreshView();
            Peers.Visibility = view.Layout == PeerLayout.Map ? Visibility.Collapsed : Visibility.Visible;
            PeerDrawing.Visibility = showsMap ? Visibility.Visible : Visibility.Collapsed;
            PeerSplit.Visibility = combined ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(Peers, stacked ? 2 : 0);
            Grid.SetColumn(Peers, combined && !stacked ? 2 : 0);
            Grid.SetRow(PeerSplit, stacked ? 1 : 0);
            Grid.SetColumn(PeerSplit, stacked ? 0 : 1);
            PeerSplit.Orientation = stacked ? Orientation.Horizontal : Orientation.Vertical;
            var fraction = stacked ? view.Stacked : view.Side;
            for (var index = 0; index < 3; index++)
            {
                PeerContent.RowDefinitions[index].Height = Length(index, combined && stacked, fraction);
                PeerContent.ColumnDefinitions[index].Width = Length(index, combined && !stacked, fraction);
            }
            var extent = Math.Max(0, (stacked ? PeerContent.ActualHeight : PeerContent.ActualWidth) - 16);
            PeerSplit.SetBounds(extent * .15, extent * .85, extent * fraction);
            if (showsMap)
            {
                if (_peerMap is null)
                {
                    _peerMap = new PeerMap { ContextFlyout = _peerMore.Flyout };
                    _peerMap.Select(Peers.Selection.Current as Peer);
                }
                if (!ReferenceEquals(PeerDrawing.Content, _peerMap))
                    PeerDrawing.Content = _peerMap;
            }
            else
            {
                PeerDrawing.Content = null;
                if (view.Layout == PeerLayout.Table)
                    _peerMap = null;
            }
        }
        finally { _arrangingPeers = false; }

        static GridLength Length(int index, bool split, double fraction) => index switch
        {
            0 => new GridLength(split ? 1 - fraction : 1, GridUnitType.Star),
            1 => new GridLength(split ? 16 : 0),
            _ => split ? new GridLength(fraction, GridUnitType.Star) : new GridLength(0),
        };
    }

    private void RefreshMap()
    {
        if (_mapQueued)
            return;
        _mapQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _mapQueued = false;
            if (!IsLoaded || _peerMap is null || _library is not null || Model.Section != InspectorSection.Peers
                || !Model.IsVisible || !ReferenceEquals(PeerDrawing.Content, _peerMap))
                return;
            _peerMap.Show(Model.IsAvailable ? Model.Peers : null, Model.Origin, Model.Text,
                _layout.View.Cities && Model.HasCities);
        });
    }
}
