using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent;

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
        Loaded += (_, _) => { Model.TextChanged += OnText; Model.PropertyChanged += OnModel; RefreshText(); Refresh(); };
        Unloaded += (_, _) => { Model.TextChanged -= OnText; Model.PropertyChanged -= OnModel; Map.Show(null, Model.Text); Graph.Show(null); };
        RefreshText();
        Refresh();
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();
    public static bool Not(bool value) => !value;
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    private void OnModel(object? sender, PropertyChangedEventArgs args) => Refresh();
    private void OnSection(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_refreshing && args.SelectedItem is NavigationViewItem { Tag: InspectorSection section }) Model.Select(section);
    }
    private void OnRange(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing) Model.IsDay = ReferenceEquals(Range.SelectedItem, Day);
    }

    private void Refresh()
    {
        FrameworkElement[] views = [General, Files, Peers, Trackers, Speed, Map];
        for (var index = 0; index < views.Length; index++)
            views[index].Visibility = index == (int)Model.Section ? Visibility.Visible : Visibility.Collapsed;
        _refreshing = true;
        Sections.SelectedItem = Sections.MenuItems.OfType<NavigationViewItem>().First(item => Equals(item.Tag, Model.Section));
        Range.SelectedItem = Model.IsDay ? Day : FiveMinutes;
        _refreshing = false;
        Graph.Show(Model);
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
        var text = Model.Text;
        FlowDirection = text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        NavigationViewItem[] sections = [GeneralSection, FilesSection, PeersSection, TrackersSection, SpeedSection, PiecesSection];
        foreach (var section in sections)
        {
            section.Content = text.Get("inspector", ((InspectorSection)section.Tag).ToString().ToLowerInvariant());
            AutomationProperties.SetName(section, (string)section.Content);
        }
        AutomationProperties.SetName(Sections, text.Get("inspector", "sections"));
        DownloadedLabel.Text = text.Get("inspector", "downloaded");
        RemainingLabel.Text = text.Get("inspector", "remaining");
        RatioLabel.Text = text.Get("columns", "ratio");
        StatusLabel.Text = text.Get("columns", "status");
        AddedLabel.Text = text.Get("columns", "added");
        CreatedLabel.Text = text.Get("inspector", "created");
        CreatorLabel.Text = text.Get("inspector", "creator");
        PieceSizeLabel.Text = text.Get("inspector", "piece_size");
        PrivacyLabel.Text = text.Get("inspector", "privacy");
        FolderLabel.Text = text.Get("add", "destination");
        CommentLabel.Text = text.Get("inspector", "comment");
        Hashes.Header = text.Get("inspector", "hashes");
        Magnet.Header = text.Get("add", "magnet");
        AutomationProperties.SetName(Hashes, (string)Hashes.Header);
        AutomationProperties.SetName(Magnet, (string)Magnet.Header);
        Retry.Content = text.Get("inspector", "retry");
        RetryFiles.Content = text.Get("inspector", "retry_files");
        EditTrackers.Content = text.Get("trackers", "edit");
        Reannounce.Content = text.Get("trackers", "reannounce");
        SaveTrackers.Content = text.Get("trackers", "save");
        CancelTrackers.Content = text.Get("add", "cancel");
        TrackerHint.Text = text.Get("trackers", "hint");
        AutomationProperties.SetName(TrackerInput, text.Get("inspector", "trackers"));
        NoPeers.Text = text.Get("peers", "empty");
        NoTrackers.Text = text.Get("trackers", "empty");
        WaitingMetadata.Text = text.Get("pieces", "metadata");
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
        AllTorrents.Text = text.Get("speed", "all");
        FiveMinutes.Content = text.Get("speed", "five_minutes");
        Day.Content = text.Get("speed", "day");
        AutomationProperties.SetName(Range, text.Get("speed", "range"));
        _files.RefreshText();
        Peers.RefreshView();
        TrackerTable.RefreshView();
        Refresh();
    }
}

public sealed record InspectorLayout(Syno.TableView.ColumnLayout? Peers, Syno.TableView.ColumnLayout? Trackers);
