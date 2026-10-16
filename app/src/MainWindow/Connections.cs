using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    public ICommand SelectedMap { get; }
    public ICommand AllMap { get; }
    private MapReturn? _mapReturn;
    private bool _changingMap;

    private sealed record MapReturn
    {
        internal WindowPage Page { get; init; }
        internal FilterPane Filters { get; init; }
        internal bool Open { get; init; }
        internal bool Compact { get; init; }
        internal bool Maximized { get; init; }
        internal double Height { get; init; }
        internal double Offset { get; init; }
        internal Torrent? Target { get; init; }
        internal InspectorSection Section { get; init; }
        internal bool AllPeers { get; init; }
        internal IReadOnlyList<Torrent>? Selection { get; init; }
        internal PeerView View { get; init; } = PeerView.Default;
        internal Control? Focus { get; init; }
    }

    private bool CanShowMap(bool all) =>
        !Model.IsClosing
        && !HasDialog
        && !Model.Inspector.IsPending
        && Model.Page is WindowPage.Torrents or WindowPage.Library
        && (all || Model.Page == WindowPage.Torrents && Model.Selected.Count > 0);

    private async Task ShowConnections(bool all)
    {
        if (!CanShowMap(all))
            return;
        if (!await LeaveInspector() || !CanShowMap(all))
            return;
        if (_mapReturn is null)
        {
            var pane = InspectorContent.Content as InspectorPane;
            var saved = new MapReturn
            {
                Page = Model.Page,
                Filters = Model.FilterPane,
                Open = Model.HasInspector,
                Compact = Model.IsInspectorCompact,
                Maximized = pane?.Surface.IsMaximized == true,
                Height = _splitHeight,
                Offset = Torrents.VerticalOffset,
                Target = Model.Inspector.Target,
                Section = Model.Inspector.Section,
                AllPeers = Model.Inspector.ShowsAllPeers,
                Selection = Model.Inspector.PeerSelection,
                View = pane?.Layout.View ?? _placement?.Inspector?.View ?? PeerView.Default,
                Focus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control,
            };
            if (!await Model.Navigation.Visit(WindowPage.Torrents, RestoreMap))
                return;
            _mapReturn = saved;
        }
        _changingMap = true;
        try
        {
            Model.Inspector.Show(
                !all && Model.Selected.Count == 1 ? Model.Selected[0] : null,
                InspectorSection.Peers,
                all,
                all ? null : Model.Selected.ToArray()
            );
            Model.Inspector.Select(InspectorSection.Peers);
            Model.SetInspector(open: true, compact: false);
            Model.FilterPane = FilterPane.Hidden;
            var pane = (InspectorPane)InspectorContent.Content;
            pane.Layout = pane.Layout with
            {
                View = pane.Layout.View with { Layout = PeerLayout.Map },
            };
            pane.Surface.IsMaximized = true;
            UpdateInspectorSize();
            BackButton.Focus(FocusState.Programmatic);
        }
        finally
        {
            _changingMap = false;
        }
    }

    private void RestoreMap()
    {
        if (_mapReturn is not { } saved)
            return;
        _mapReturn = null;
        _changingMap = true;
        try
        {
            var target = saved.Target is { } previous && Model.Contains(previous) ? previous : null;
            var section = target is null && saved.Section is not (InspectorSection.Peers or InspectorSection.Speed)
                ? InspectorSection.Peers
                : saved.Section;
            Model.Inspector.Show(target, section, saved.AllPeers, saved.Selection);
            Model.Inspector.Select(section);
            Model.SetInspector(saved.Open, saved.Compact);
            Model.FilterPane = saved.Filters;
            _splitHeight = saved.Height;
            if (InspectorContent.Content is InspectorPane pane)
            {
                pane.Layout = pane.Layout with { View = saved.View };
                pane.Surface.IsMaximized = saved.Maximized;
            }
            UpdateInspectorSize();
            var page = Model.Navigation.Current;
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (!Model.IsClosing && _mapReturn is null && Model.Navigation.Current == page)
                        Torrents.ScrollTo(saved.Offset);
                }
            );
        }
        finally
        {
            _changingMap = false;
        }
    }

    private void KeepMap()
    {
        if (_changingMap || _mapReturn is null || Model.Page != WindowPage.Torrents)
            return;
        _mapReturn = null;
        Model.Navigation.KeepWorkspace();
    }

    private async void OnMapKey(object sender, KeyRoutedEventArgs args)
    {
        if (
            args.Key != VirtualKey.Escape
            || _mapReturn is null
            || Model.Page != WindowPage.Torrents
            || HasDialog
            || HasEditorFocus()
        )
            return;
        args.Handled = true;
        await GoBack();
    }
}
