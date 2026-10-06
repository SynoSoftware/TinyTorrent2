using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Views;

public sealed partial class InspectorForm : UserControl
{
    private readonly FileBrowser _files;
    private bool _refreshing;
    private bool _editing;
    private bool _composing;
    public Inspector Model { get; }
    internal InspectorLayout Layout
    {
        get => new(Peers.Layout, TrackerTable.Layout);
        set
        {
            if (value.Peers is { } peers) Peers.Layout = peers;
            if (value.Trackers is { } trackers) TrackerTable.Layout = trackers;
        }
    }

    public InspectorForm(Inspector model)
    {
        Model = model;
        InitializeComponent();
        _files = new FileBrowser(model.Files);
        FileContent.Content = _files;
        TrackerInput.TextCompositionStarted += (_, _) => _composing = true;
        TrackerInput.TextCompositionEnded += (_, _) => _composing = false;
        SaveTrackers.Click += (_, _) => { if (!_composing && Model.SaveTrackers.CanExecute(null)) Model.SaveTrackers.Execute(null); };
        var sections = new[] { GeneralSection, FilesSection, PeersSection, TrackersSection, SpeedSection, PiecesSection };
        for (var index = 0; index < sections.Length; index++) sections[index].Tag = (InspectorSection)index;
        Sections.SelectedItem = sections[(int)model.Section];
        for (var index = 0; index < Ranges.Items.Count; index++) Ranges.Items[index].Tag = (SpeedRange)index;
        Peers.Schema<Peer>().Key(peer => peer.Endpoint).SortKey(EndpointColumn, peer => peer.Endpoint)
            .SortKey(ClientColumn, peer => peer.Client).SortKey(ConnectionColumn, peer => peer.ConnectionText)
            .SortKey(PeerProgressColumn, peer => peer.Progress).SortKey(PeerDownColumn, peer => peer.DownloadRate)
            .SortKey(PeerUpColumn, peer => peer.UploadRate).SortKey(PeerDownloadedColumn, peer => peer.Downloaded)
            .SortKey(PeerUploadedColumn, peer => peer.Uploaded);
        TrackerTable.Schema<Tracker>().Key(tracker => tracker.Url).SortKey(UrlColumn, tracker => tracker.Url)
            .SortKey(TierColumn, tracker => tracker.Tier).SortKey(TrackerStatusColumn, tracker => tracker.StatusText)
            .SortKey(SeedsColumn, tracker => tracker.Seeds).SortKey(LeechersColumn, tracker => tracker.Leechers)
            .SortKey(CompletedColumn, tracker => tracker.Downloaded).SortKey(NextColumn, tracker => tracker.NextAnnounce)
            .SortKey(MessageColumn, tracker => tracker.Message);
        Loaded += (_, _) => { Model.TextChanged += OnText; Model.PropertyChanged += OnModel; Model.RowsUpdated += OnRows; RefreshText(); Refresh(); };
        Unloaded += (_, _) => { Model.TextChanged -= OnText; Model.PropertyChanged -= OnModel; Model.RowsUpdated -= OnRows; Map.Show(null, Model.Text); };
        RefreshText();
        Refresh();
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();

    internal Control Recover()
    {
        Model.Select(Model.IsEditingTrackers ? InspectorSection.Trackers : InspectorSection.Files);
        return Model.IsEditingTrackers ? TrackerInput : RetryFiles;
    }
    public static bool Not(bool value) => !value;
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    private void OnRows(object? sender, InspectorSection section)
    {
        if (section == InspectorSection.Peers) Peers.RefreshView();
        else if (section == InspectorSection.Trackers) TrackerTable.RefreshView();
    }
    private void OnModel(object? sender, PropertyChangedEventArgs args) => Refresh();
    private void OnSection(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_refreshing && sender.SelectedItem is { Tag: InspectorSection section }) Model.Select(section);
    }
    private void OnRange(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_refreshing && sender.SelectedItem is { Tag: SpeedRange range }) Model.Range = range;
    }

    private void Refresh()
    {
        FrameworkElement[] views = [General, Files, Peers, Trackers, Speed, Map];
        for (var index = 0; index < views.Length; index++)
            views[index].Visibility = index == (int)Model.Section ? Visibility.Visible : Visibility.Collapsed;
        _refreshing = true;
        Sections.SelectedItem = Sections.Items.First(item => Equals(item.Tag, Model.Section));
        Ranges.SelectedItem = Ranges.Items.First(item => Equals(item.Tag, Model.Range));
        _refreshing = false;
        Map.Show(Model.Pieces, Model.Text);
        if (_editing != Model.IsEditingTrackers)
        {
            _editing = Model.IsEditingTrackers;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_editing) TrackerInput.Focus(FocusState.Programmatic);
                else EditTrackers.Focus(FocusState.Programmatic);
            });
        }
    }

    internal void RefreshText()
    {
        Bindings.Update();
        var text = Model.Text;
        FlowDirection = text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (var section in Sections.Items)
        {
            section.Text = text.Get("inspector", ((InspectorSection)section.Tag).ToString().ToLowerInvariant());
            AutomationProperties.SetName(section, section.Text);
        }
        AutomationProperties.SetName(Sections, text.Get("inspector", "sections"));
        AutomationProperties.SetName(Close, text.Get("inspector", "close"));
        ToolTipService.SetToolTip(Close, text.Get("inspector", "close"));
        Hashes.Header = text.Get("inspector", "hashes");
        Magnet.Header = text.Get("add", "magnet");
        AutomationProperties.SetName(Hashes, (string)Hashes.Header);
        AutomationProperties.SetName(Magnet, (string)Magnet.Header);
        AutomationProperties.SetName(TrackerInput, text.Get("inspector", "trackers"));
        Peers.Strings = TrackerTable.Strings = text.Table;
        EndpointColumn.DisplayName = text.Get("peers", "endpoint");
        ClientColumn.DisplayName = text.Get("peers", "client");
        ConnectionColumn.DisplayName = text.Get("peers", "connection_label");
        PeerProgressColumn.DisplayName = text.Get("columns", "progress");
        PeerDownColumn.DisplayName = text.Get("columns", "down");
        PeerUpColumn.DisplayName = text.Get("columns", "up");
        PeerDownloadedColumn.DisplayName = text.Get("inspector", "downloaded");
        PeerUploadedColumn.DisplayName = text.Get("peers", "uploaded");
        UrlColumn.DisplayName = text.Get("trackers", "url");
        TierColumn.DisplayName = text.Get("trackers", "tier");
        TrackerStatusColumn.DisplayName = text.Get("columns", "status");
        SeedsColumn.DisplayName = text.Get("trackers", "seeds");
        LeechersColumn.DisplayName = text.Get("trackers", "leechers");
        CompletedColumn.DisplayName = text.Get("trackers", "completed");
        NextColumn.DisplayName = text.Get("trackers", "next");
        MessageColumn.DisplayName = text.Get("trackers", "message");
        Graph.RefreshText(text);
        _files.RefreshText();
        Peers.RefreshView();
        TrackerTable.RefreshView();
        Refresh();
    }
}

public sealed record InspectorLayout(Syno.TableView.ColumnLayout? Peers, Syno.TableView.ColumnLayout? Trackers);
