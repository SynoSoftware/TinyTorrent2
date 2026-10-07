using System.ComponentModel;
using System.Windows.Input;
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
        // The values are selectable text, so a double-click can arrive already handled.
        foreach (var (element, command) in new (UIElement, ICommand)[] { (DownloadRate, model.LimitSpeed),
            (UploadRate, model.LimitSpeed), (Limit, model.LimitSpeed), (Ratio, model.ShowRatioLimit),
            (SeedCount, model.ShowConnectionLimit), (PeerCount, model.ShowConnectionLimit),
            (FolderPath, model.MoveFiles) })
            element.AddHandler(DoubleTappedEvent, new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler((_, _) => command.Execute(null)), true);
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
        Loaded += (_, _) => { Model.TextChanged += OnText; Model.PropertyChanged += OnModel; Model.RowsUpdated += OnRows; Model.RecoveryRequested += OnRecovery; RefreshText(); Refresh(); };
        Unloaded += (_, _) => { Model.TextChanged -= OnText; Model.PropertyChanged -= OnModel; Model.RowsUpdated -= OnRows; Model.RecoveryRequested -= OnRecovery; Map.Show(null, Model.Text); };
        RefreshText();
        Refresh();
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();
    private void OnRecovery(object? sender, EventArgs args) => Recover().Focus(FocusState.Programmatic);

    internal Control Recover()
    {
        Model.Select(Model.IsEditingTrackers ? InspectorSection.Trackers : InspectorSection.Files);
        return Model.IsEditingTrackers ? TrackerInput : Retry;
    }
    public static bool Not(bool value) => !value;
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility Shown(string text) => text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnGeneralSize(object sender, SizeChangedEventArgs args) =>
        VisualStateManager.GoToState(this, args.NewSize.Width >= 760 ? "Wide" : "Narrow", false);
    private void OnRows(object? sender, InspectorSection section)
    {
        if (section == InspectorSection.Peers) Peers.RefreshView();
        else if (section == InspectorSection.Trackers) TrackerTable.RefreshView();
    }
    private void OnModel(object? sender, PropertyChangedEventArgs args) => Refresh();
    private async void OnSection(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_refreshing || sender.SelectedItem is not { Tag: InspectorSection section }) return;
        Refresh();
        await Model.Navigate(section);
    }

    private void Refresh()
    {
        FrameworkElement[] views = [General, Files, Peers, Trackers, Speed, Map];
        for (var index = 0; index < views.Length; index++)
            views[index].Visibility = index == (int)Model.Section ? Visibility.Visible : Visibility.Collapsed;
        _refreshing = true;
        Sections.IsEnabled = !Model.IsPending;
        Sections.SelectedItem = Sections.Items.First(item => Equals(item.Tag, Model.Section));
        _refreshing = false;
        var loading = Model.IsAvailable && Model.IsLoading;
        Peers.Placeholder = Model.Peers is null && loading ? Syno.TableView.Placeholder.Loading : Syno.TableView.Placeholder.Empty;
        TrackerTable.Placeholder = Model.Trackers is null && loading ? Syno.TableView.Placeholder.Loading : Syno.TableView.Placeholder.Empty;
        NoPeers.Visibility = Model.Peers is null ? Visibility.Collapsed : Visibility.Visible;
        NoTrackers.Visibility = Model.Trackers is null ? Visibility.Collapsed : Visibility.Visible;
        Map.Show(Model.Pieces, Model.Text);
        VisualStateManager.GoToState(this, Model.Target is { IsError: true } ? "Error" :
            Model.Target is { IsPaused: true } ? "Paused" : "Transferring", false);
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
        Speed.RefreshText(text);
        _files.RefreshText();
        Peers.RefreshView();
        TrackerTable.RefreshView();
        Refresh();
    }
}

public sealed record InspectorLayout(Syno.TableView.ColumnLayout? Peers, Syno.TableView.ColumnLayout? Trackers);
