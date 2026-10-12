using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Views;

public sealed partial class InspectorPane : UserControl
{
    private readonly FileBrowser _files;
    private readonly SelectorBarItem[] _torrentSections;
    private readonly Motion _motion = new();
    private readonly Motion _arrival = new();
    private Torrent? _target;
    private InspectorSection _section;
    private bool _refreshing;
    private bool _awaitingArrival;
    private bool _composing;
    private (Torrent Target, int Index)? _fileReveal;
    public Inspector Model { get; }
    internal double HeaderHeight => Surface.HeaderHeight;
    internal InspectorLayout Layout
    {
        get => new(Peers.Layout, TrackerTable.Layout);
        set
        {
            if (value.Peers is { } peers)
                Peers.Layout = peers;
            if (value.Trackers is { } trackers)
                TrackerTable.Layout = trackers;
        }
    }

    public InspectorPane(Inspector model)
    {
        Model = model;
        InitializeComponent();
        SectionContent.SizeChanged += Motion.Clip;
        _files = new FileBrowser(model.Files) { ToolbarPadding = new Thickness(16, 12, 16, 0) };
        FileContent.Content = _files;
        TrackerInput.TextCompositionStarted += (_, _) => _composing = true;
        TrackerInput.TextCompositionEnded += (_, _) => _composing = false;
        TrackerInput.KeyDown += (_, args) =>
        {
            if (
                args.Key != Windows.System.VirtualKey.Escape
                || _composing
                || !Model.CancelTrackers.CanExecute(null)
            )
                return;
            Model.CancelTrackers.Execute(null);
            DispatcherQueue.TryEnqueue(() => EditTrackers.Focus(FocusState.Programmatic));
            args.Handled = true;
        };
        // Focus moves after the click, once the editor has opened or closed.
        EditTrackers.Click += (_, _) =>
            DispatcherQueue.TryEnqueue(() => TrackerInput.Focus(FocusState.Programmatic));
        CancelTrackers.Click += (_, _) =>
            DispatcherQueue.TryEnqueue(() => EditTrackers.Focus(FocusState.Programmatic));
        SaveTrackers.Click += async (_, _) =>
        {
            if (_composing || !Model.CanSaveTrackers)
                return;
            await Model.CommitTrackers();
            // Someone who moved on during the request keeps their place.
            if (!Model.IsEditingTrackers && HasTrackerEditorFocus())
                DispatcherQueue.TryEnqueue(() => EditTrackers.Focus(FocusState.Programmatic));
        };
        _torrentSections = new[]
        {
            GeneralSection,
            FilesSection,
            PeersSection,
            TrackersSection,
            SpeedSection,
            PiecesSection,
        };
        for (var index = 0; index < _torrentSections.Length; index++)
            _torrentSections[index].Tag = (InspectorSection)index;
        Sections.SelectedItem = _torrentSections[(int)model.Section];
        // The values are selectable text, so a double-click can arrive already handled.
        foreach (
            var (element, command) in new (UIElement, ICommand)[]
            {
                (DownloadRate, model.LimitSpeed),
                (UploadRate, model.LimitSpeed),
                (Limit, model.LimitSpeed),
                (Ratio, model.ShowRatioLimit),
                (SeedCount, model.ShowConnectionLimit),
                (PeerCount, model.ShowConnectionLimit),
                (FolderPath, model.MoveFiles),
            }
        )
            element.AddHandler(
                DoubleTappedEvent,
                new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler(
                    (_, _) => command.Execute(null)
                ),
                true
            );
        Peers
            .Schema<Peer>()
            .Key(peer => peer.Endpoint)
            .SortKey(EndpointColumn, peer => peer.Endpoint)
            .SortKey(ClientColumn, peer => peer.Client)
            .SortKey(ConnectionColumn, peer => peer.ConnectionText)
            .SortKey(PeerProgressColumn, peer => peer.Progress)
            .SortKey(PeerDownColumn, peer => peer.DownloadRate)
            .SortKey(PeerUpColumn, peer => peer.UploadRate)
            .SortKey(PeerDownloadedColumn, peer => peer.Downloaded)
            .SortKey(PeerUploadedColumn, peer => peer.Uploaded);
        TrackerTable
            .Schema<Tracker>()
            .Key(tracker => tracker.Url)
            .SortKey(UrlColumn, tracker => tracker.Url)
            .SortKey(TierColumn, tracker => tracker.Tier)
            .SortKey(TrackerStatusColumn, tracker => tracker.StatusText)
            .SortKey(SeedsColumn, tracker => tracker.SeedCount)
            .SortKey(LeechersColumn, tracker => tracker.LeecherCount)
            .SortKey(DownloadsColumn, tracker => tracker.DownloadCount)
            .SortKey(NextColumn, tracker => tracker.NextAnnounce)
            .SortKey(MessageColumn, tracker => tracker.Message);
        Loaded += (_, _) =>
        {
            Model.TextChanged += OnText;
            Model.PropertyChanged += OnModel;
            Model.RowsUpdated += OnRows;
            Model.RecoveryRequested += OnRecovery;
            RefreshText();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            CancelReveal();
            _motion.Stop();
            _arrival.Stop();
            Model.TextChanged -= OnText;
            Model.PropertyChanged -= OnModel;
            Model.RowsUpdated -= OnRows;
            Model.RecoveryRequested -= OnRecovery;
            Map.Show(null, Model.Text);
        };
        RefreshText();
        Refresh();
    }

    private void OnText(object? sender, EventArgs args) => RefreshText();

    // Focus still belongs to the editor when it sits inside it, or when the
    // editor has collapsed under it and nothing holds focus.
    private bool HasTrackerEditorFocus()
    {
        var element =
            Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (element is null)
            return true;
        while (element is not null)
        {
            if (ReferenceEquals(element, TrackerEditor))
                return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnRecovery(object? sender, EventArgs args) =>
        Recover().Focus(FocusState.Programmatic);

    internal Control Recover()
    {
        Model.Select(Model.IsEditingTrackers ? InspectorSection.Trackers : InspectorSection.Files);
        return Model.IsEditingTrackers ? TrackerInput : Retry;
    }

    public static Visibility Hidden(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility Shown(string text) =>
        text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnGeneralSize(object sender, SizeChangedEventArgs args) =>
        VisualStateManager.GoToState(
            this,
            args.NewSize.Width >= FieldColumns.WideMinimum ? "Wide" : "Narrow",
            false
        );

    private void OnRows(object? sender, InspectorSection section)
    {
        if (section == InspectorSection.Peers)
            Peers.RefreshView();
        else if (section == InspectorSection.Trackers)
            TrackerTable.RefreshView();
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        if (_library is null)
            Refresh();
    }

    internal void RevealFile(int index)
    {
        if (Model.Target is not { } target)
            return;
        CancelReveal();
        _fileReveal = (target, index);
        _files.LayoutUpdated += OnFileLayout;
        Model.Select(InspectorSection.Files);
        Refresh();
    }

    internal void CancelReveal()
    {
        _fileReveal = null;
        _files.LayoutUpdated -= OnFileLayout;
    }

    private void OnFileLayout(object? sender, object args) => RevealFile();

    private void RevealFile()
    {
        if (_fileReveal is not { } reveal)
            return;
        if (
            Model.Target != reveal.Target
            || !Model.IsVisible
            || !Model.IsAvailable
            || Model.HasError
            || Model.Section != InspectorSection.Files
        )
        {
            CancelReveal();
            return;
        }
        if (!IsLoaded || !Model.HasFiles)
            return;
        if (_files.Reveal(reveal.Index))
            CancelReveal();
    }

    private async void OnSection(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_library is not null && !_refreshing)
        {
            var previous = _librarySection;
            _librarySection = sender.SelectedItem;
            RefreshLibrary();
            if (_librarySection != previous && _libraryContent is not null)
                _arrival.Play(
                    _libraryContent,
                    ReferenceEquals(_librarySection, _fileSection) ? 24 : -24
                );
            return;
        }
        if (_refreshing || sender.SelectedItem is not { Tag: InspectorSection section })
            return;
        Refresh();
        await Model.Navigate(section);
    }

    private void Refresh(bool workspace = false)
    {
        if (_library is not null)
        {
            RefreshLibrary();
            return;
        }
        EmptyText.Text = Model.EmptyText;
        var inspecting = Model.Target is not null;
        FrameworkElement[] views = [General, Files, Peers, Trackers, Speed, Map];
        var content = inspecting ? views[(int)Model.Section] : EmptyState;
        if (!workspace && inspecting && _section != Model.Section)
            _motion.Show(content, Model.Section >= _section ? 1 : -1);
        else
            _motion.CrossFade(content);
        if (_target != Model.Target)
        {
            _awaitingArrival =
                _target is not null
                && inspecting
                && _section == Model.Section
                && Model.Section != InspectorSection.Pieces;
            _target = Model.Target;
        }
        if (_section != Model.Section)
            _awaitingArrival = false;
        if (_awaitingArrival && !Model.IsLoading)
        {
            _arrival.Fade(SectionViews, Math.Min(SectionViews.Opacity, 0.65), 1);
            _awaitingArrival = false;
        }
        _section = Model.Section;
        _refreshing = true;
        Sections.IsEnabled = inspecting;
        Sections.SelectedItem = Sections.Items.First(item => Equals(item.Tag, Model.Section));
        _refreshing = false;
        var loading = Model.IsAvailable && Model.IsLoading;
        Peers.Placeholder =
            Model.Peers is null && loading
                ? Syno.TableView.Placeholder.Loading
                : Syno.TableView.Placeholder.Empty;
        TrackerTable.Placeholder =
            Model.Trackers is null && loading
                ? Syno.TableView.Placeholder.Loading
                : Syno.TableView.Placeholder.Empty;
        NoPeers.Visibility = Model.Peers is null ? Visibility.Collapsed : Visibility.Visible;
        NoTrackers.Visibility = Model.Trackers is null ? Visibility.Collapsed : Visibility.Visible;
        Map.Show(Model.Pieces, Model.Text);
        VisualStateManager.GoToState(
            this,
            Model.Target is { IsError: true } ? "Error"
                : Model.Target is { IsPaused: true } ? "Paused"
                : "Transferring",
            false
        );
        RevealFile();
    }

    internal void RefreshText()
    {
        Bindings.Update();
        ActionButton.Align([EditTrackers, Reannounce]);
        if (_library is not null)
            Bindings.StopTracking();
        var text = Model.Text;
        FlowDirection = text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (var section in _torrentSections)
        {
            if (section.Tag is not InspectorSection kind)
                continue;
            section.Text = text.Get("inspector", kind.ToString().ToLowerInvariant());
            AutomationProperties.SetName(section, section.Text);
        }
        AutomationProperties.SetName(Sections, text.Get("inspector", "sections"));
        AutomationProperties.SetName(Close, text.Get("inspector", "close"));
        ToolTipService.SetToolTip(Close, text.Get("inspector", "close"));
        AutomationProperties.SetName(Minimize, text.Get("inspector", "minimize"));
        ToolTipService.SetToolTip(Minimize, text.Get("inspector", "minimize"));
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
        DownloadsColumn.DisplayName = text.Get("trackers", "downloads");
        NextColumn.DisplayName = text.Get("trackers", "next");
        MessageColumn.DisplayName = text.Get("trackers", "message");
        Speed.RefreshText(text);
        _files.RefreshText();
        Peers.RefreshView();
        TrackerTable.RefreshView();
        Refresh();
    }
}

public sealed record InspectorLayout(
    Syno.TableView.ColumnLayout? Peers,
    Syno.TableView.ColumnLayout? Trackers
);
