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
    private bool _arrangingPeers;
    private bool _peersStacked;
    private bool _refreshingPeerActions;

    private void SetupPeers()
    {
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
    }

    private void OnPeerView(object sender, RoutedEventArgs args)
    {
        if (_refreshingPeerActions)
            return;
        var map = ShowPeerMap.IsChecked == true;
        var table = ShowPeerTable.IsChecked == true;
        if (map || table)
            _layout = _layout with { View = _layout.View with
            {
                Layout = map && table ? PeerLayout.Both : map ? PeerLayout.Map : PeerLayout.Table,
            } };
        RefreshPeerActions();
        ArrangePeers(fit: true);
    }

    private void RefreshPeerActions()
    {
        var text = Model.Text;
        _refreshingPeerActions = true;
        ShowPeerMap.IsChecked = _layout.View.Layout != PeerLayout.Table;
        ShowPeerTable.IsChecked = _layout.View.Layout != PeerLayout.Map;
        _refreshingPeerActions = false;
        ShowPeerMap.IsEnabled = _layout.View.Layout != PeerLayout.Map;
        ShowPeerTable.IsEnabled = _layout.View.Layout != PeerLayout.Table;
        AutomationProperties.SetName(ShowPeerMap, text.Get("peer_map", "map"));
        AutomationProperties.SetName(ShowPeerTable, text.Get("peer_map", "table"));
        ToolTipService.SetToolTip(ShowPeerMap, text.Get("peer_map", "map"));
        ToolTipService.SetToolTip(ShowPeerTable, text.Get("peer_map", "table"));
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
                var side = PeerMap.FitScale(new Size((width - 16) * (1 - view.Side), height));
                var below = PeerMap.FitScale(new Size(width, (height - 16) * (1 - view.Stacked)));
                if (side != below)
                    _peersStacked = below > side;
            }
            var stacked = combined && _peersStacked;
            var showsMap = view.Layout != PeerLayout.Table && Model.Section == InspectorSection.Peers
                && Model.IsVisible && Model.HasContent && _library is null;
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
                    _peerMap = new PeerMap();
                    PeerDrawing.Content = _peerMap;
                    _peerMap.Select(Peers.Selection.Current as Peer);
                }
                RefreshMap();
            }
            else
            {
                PeerDrawing.Content = null;
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
        if (_peerMap is null || Model.Section != InspectorSection.Peers || !Model.IsVisible)
            return;
        _peerMap.Show(Model.IsAvailable ? Model.Peers : null, Model.Origin, Model.Text);
    }
}
