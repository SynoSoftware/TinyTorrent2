using System.ComponentModel;
using System.Windows.Input;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class InspectorPane : UserControl
{
    private FileBrowser? _files;
    private InspectorLayout _layout = new(null, null);
    private readonly SelectorBarItem[] _torrentSections;
    private readonly Motion _motion = new();
    private readonly Motion _arrival = new();
    private InspectorSection _section;
    private bool _refreshing;
    private readonly TurnCallback _trackerRows;
    private readonly TurnCallback _facts;
    private bool _tracking;
    private (Torrent Target, int Index)? _fileReveal;
    public Inspector Model { get; }
    internal event EventHandler? LayoutChanged;
    internal event EventHandler? ViewChanged;
    internal InspectorLayout Layout
    {
        get =>
            new(Peers?.Layout ?? _layout.Peers, TrackerTable?.Layout ?? _layout.Trackers)
            {
                View = _layout.View,
            };
        set
        {
            var view = value.View ?? PeerView.Default;
            view = view with
            {
                // Saved side-by-side layouts now follow the available space.
                Layout =
                    (int)view.Layout == 3 ? PeerLayout.Both
                    : Enum.IsDefined(view.Layout) ? view.Layout
                    : PeerView.Default.Layout,
                Side = Fraction(view.Side, PeerView.Default.Side),
                Stacked = Fraction(view.Stacked, PeerView.Default.Stacked),
                SpeedCorner = Enum.IsDefined(view.SpeedCorner) ? view.SpeedCorner : PeerView.Default.SpeedCorner,
                Animation = Enum.IsDefined(view.Animation) ? view.Animation : PeerView.Default.Animation,
                DotTotal = Enum.IsDefined(view.DotTotal) ? view.DotTotal : PeerView.Default.DotTotal,
                ArcLimit = view.ArcLimit > 0 ? view.ArcLimit : PeerView.Default.ArcLimit,
                Grouping = Enum.IsDefined(view.Grouping) ? view.Grouping : PeerView.Default.Grouping,
            };
            var peers = value.Peers;
            if (
                peers is { Order: { } order }
                && order.Contains("location")
                && !order.Contains("city")
            )
            {
                var columns = order.ToList();
                columns.Insert(columns.IndexOf("location") + 1, "city");
                peers = peers with { Order = columns };
            }
            if (peers is { Order: { } saved } && saved.Contains("endpoint") && !saved.Contains("port"))
            {
                var columns = saved.ToList();
                columns.Insert(columns.IndexOf("endpoint") + 1, "port");
                peers = peers with { Order = columns };
            }
            _layout = value with { Peers = peers, View = view };
            if (Peers is not null && peers is not null)
                Peers.Layout = peers;
            if (TrackerTable is not null && value.Trackers is { } trackers)
                TrackerTable.Layout = trackers;
            ArrangePeers(fit: true);
            _mapRefresh.Request();
            RefreshPeerActions();

            static double Fraction(double value, double fallback) =>
                double.IsFinite(value) ? Math.Clamp(value, .15, .85) : fallback;
        }
    }

    public InspectorPane(Inspector model, ICommand downloadSubtitles)
    {
        // Assigned first, because the rest of the constructor can request these.
        _trackerRows = new TurnCallback(DispatcherQueue, DispatcherQueuePriority.Low, RefreshTrackerRows);
        _facts = new TurnCallback(DispatcherQueue, DispatcherQueuePriority.Low, ShowFacts);
        _mapRefresh = new TurnCallback(DispatcherQueue, DispatcherQueuePriority.Low, RefreshMap);
        _peerRows = new TurnCallback(DispatcherQueue, DispatcherQueuePriority.Low, DeliverPeerRows);
        Model = model;
        _downloadSubtitles = downloadSubtitles;
        InitializeComponent();
        _libraryOpen = CreateAction(Segoe.Play, "LibraryOpen");
        _libraryTorrents = CreateAction(Segoe.LocateFixed, "LibraryTorrents");
        _libraryMore = CreateAction(Segoe.Ellipsis, "LibraryMore");
        _peerMore = CreateAction(Segoe.Ellipsis, "PeerMore");
        SectionContent.SizeChanged += Motion.Clip;
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
        Loaded += (_, _) =>
        {
            Model.TextChanged += OnText;
            Model.PropertyChanged += OnModel;
            Model.RowsUpdated += OnRows;
            Model.PeersWithdrawn += OnPeersWithdrawn;
            Model.RecoveryRequested += OnRecovery;
            Model.LibraryChanged += OnLibrary;
            _display.TextScaleFactorChanged += OnTextScale;
            ShowLibrary(Model.Library);
            RefreshText();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _display.TextScaleFactorChanged -= OnTextScale;
            ShowActions(null);
            _motion.Stop();
            _arrival.Stop();
            Model.TextChanged -= OnText;
            Model.PropertyChanged -= OnModel;
            Model.RowsUpdated -= OnRows;
            Model.PeersWithdrawn -= OnPeersWithdrawn;
            Model.RecoveryRequested -= OnRecovery;
            Model.LibraryChanged -= OnLibrary;
            ClearLibraryActions();
            StopTracking();
            ClearLibraryContent(_libraryContent);
            ClearLibraryContent(_librarySpare);
        };
        RefreshText();
        ShowLibrary(Model.Library);
        Refresh();
    }

    private FrameworkElement LoadSection(InspectorSection section)
    {
        var content = section switch
        {
            InspectorSection.General => (FrameworkElement?)General,
            InspectorSection.Files => Files,
            InspectorSection.Peers => PeerContent,
            InspectorSection.Trackers => Trackers,
            InspectorSection.Speed => Speed,
            InspectorSection.Pieces => Map,
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
        if (content is not null)
            return content;
        content = (FrameworkElement)FindName(
            section switch
            {
                InspectorSection.Pieces => nameof(Map),
                InspectorSection.Peers => nameof(PeerContent),
                _ => section.ToString(),
            }
        );
        switch (section)
        {
            case InspectorSection.General:
                // The values are selectable text, so a double-click can arrive already handled.
                foreach (
                    var (element, command) in new (UIElement, ICommand)[]
                    {
                        (DownloadRate, Model.LimitSpeed),
                        (UploadRate, Model.LimitSpeed),
                        (Limit, Model.LimitSpeed),
                        (Ratio, Model.ShowRatioLimit),
                        (SeedCount, Model.ShowConnectionLimit),
                        (PeerCount, Model.ShowConnectionLimit),
                        (FolderPath, Model.MoveFiles),
                    }
                )
                    element.AddHandler(
                        DoubleTappedEvent,
                        new Microsoft.UI.Xaml.Input.DoubleTappedEventHandler(
                            (_, _) => command.Execute(null)
                        ),
                        true
                    );
                break;
            case InspectorSection.Files:
                _files = new FileBrowser(Model.Files)
                {
                    ToolbarPadding = new Thickness(16, 12, 16, 0),
                };
                FileContent.Content = _files;
                break;
            case InspectorSection.Peers:
                // A swarm keeps one peer object per endpoint, so a row is its peer.
                var schema = Peers.Schema<Peer>();
                SortPeers(schema, AddressColumn, peer => new PeerAddress(peer.Address), Peer.Properties.None);
                SortPeers(schema, PortColumn, peer => peer.Port, Peer.Properties.None);
                SortPeers(schema, ClientColumn, peer => peer.Client, Peer.Properties.Client);
                SortPeers(schema, CountryColumn, peer => peer.CountryText, Peer.Properties.CountryText);
                SortPeers(schema, CityColumn, peer => peer.CityText, Peer.Properties.CityText);
                SortPeers(schema, PeerStateColumn, peer => peer.State, Peer.Properties.State);
                SortPeers(schema, ConnectionColumn, peer => peer.Connection, Peer.Properties.Connection);
                SortPeers(schema, PeerProgressColumn, peer => peer.Progress, Peer.Properties.Progress);
                SortPeers(schema, PeerDownColumn, peer => peer.DownloadRate, Peer.Properties.DownloadRate);
                SortPeers(schema, PeerUpColumn, peer => peer.UploadRate, Peer.Properties.UploadRate);
                SortPeers(schema, PeerDownloadedColumn, peer => peer.Downloaded, Peer.Properties.Downloaded);
                SortPeers(schema, PeerUploadedColumn, peer => peer.Uploaded, Peer.Properties.Uploaded);
                if (_layout.Peers is { } peers)
                    Peers.Layout = peers;
                SetupPeers();
                break;
            case InspectorSection.Trackers:
                TextEditor.FollowComposition(TrackerInput);
                TrackerInput.KeyDown += (_, args) =>
                {
                    if (
                        args.Key != Windows.System.VirtualKey.Escape
                        || TextEditor.IsComposing(TrackerInput)
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
                    if (TextEditor.IsComposing(TrackerInput) || !Model.CanSaveTrackers)
                        return;
                    await Model.CommitTrackers();
                    // Someone who moved on during the request keeps their place.
                    if (!Model.IsEditingTrackers && HasTrackerEditorFocus())
                        DispatcherQueue.TryEnqueue(() =>
                            EditTrackers.Focus(FocusState.Programmatic)
                        );
                };
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
                if (_layout.Trackers is { } trackers)
                    TrackerTable.Layout = trackers;
                break;
            case InspectorSection.Pieces:
                Map.CopyRequested += (_, text) => _ = Model.Copy(text);
                break;
        }
        RefreshText();
        return content;
    }

    private void OnText(object? sender, EventArgs args)
    {
        RefreshText();
        Refresh();
    }

    // Focus still belongs to the editor when it sits inside it, or when the
    // editor has collapsed under it and nothing holds focus.
    private bool HasTrackerEditorFocus()
    {
        var element =
            Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        return element is null || VisualTree.Contains(TrackerEditor, element);
    }

    private void OnRecovery(object? sender, EventArgs args) =>
        Recover().Focus(FocusState.Programmatic);

    internal Control Recover()
    {
        Model.Select(Model.IsEditingTrackers ? InspectorSection.Trackers : InspectorSection.Files);
        LoadSection(Model.Section);
        return Model.IsEditingTrackers ? TrackerInput : Retry;
    }

    private void OnRows(object? sender, InspectorSection section)
    {
        if (_library is not null || !Model.IsVisible || Model.IsReplacing || section != Model.Section)
            return;
        // The map takes the peers' changes from the inspector once per round.
        if (section == InspectorSection.Peers)
        {
            _peerRows.Request();
            return;
        }
        _trackerRows.Request();
    }

    private void RefreshTrackerRows()
    {
        if (!IsLoaded || _library is not null || !Model.IsVisible || Model.IsReplacing)
        {
            return;
        }
        if (Model.Section == InspectorSection.Trackers && TrackerTable?.Sort is not null)
        {
            TrackerTable.RefreshView();
        }
    }

    private void OnModel(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Inspector.ShownPeers))
        {
            _peerRows.Request();
            return;
        }
        if (args.PropertyName == nameof(Inspector.PeerSummary))
            return;
        if (Inspector.IsFact(args.PropertyName))
        {
            _facts.Request();
            return;
        }
        if (args.PropertyName == nameof(Inspector.IsActive) && Model.IsActive)
            ShowLibrary(Model.Library);
        if (_library is null || args.PropertyName is nameof(Inspector.IsActive) or nameof(Inspector.IsReplacing))
            Refresh();
    }

    internal void RevealFile(int index)
    {
        if (Model.Target is not { } target)
            return;
        CancelReveal();
        _fileReveal = (target, index);
        Model.Select(InspectorSection.Files);
        LoadSection(InspectorSection.Files);
        _files!.LayoutUpdated += OnFileLayout;
        Refresh();
    }

    internal void CancelReveal()
    {
        _fileReveal = null;
        if (_files is not null)
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
        if (_files?.Reveal(reveal.Index) == true)
            CancelReveal();
    }

    private async void OnSection(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_refreshing)
            return;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        if (ReferenceEquals(sender, LibrarySections))
        {
            if (_library is null)
                return;
            _library.Section = ReferenceEquals(sender.SelectedItem, LibraryFile)
                ? LibrarySection.File
                : LibrarySection.Information;
            return;
        }
        if (_library is not null || sender.SelectedItem is not { Tag: InspectorSection section })
            return;
        Refresh();
        await Model.Navigate(section);
    }

    private void Refresh(bool workspace = false)
    {
        SectionViews.IsHitTestVisible = _library is not null || !Model.IsReplacing;
        TabActions.IsHitTestVisible = SectionViews.IsHitTestVisible;
        if (_libraryContent is not null)
            _libraryContent.IsEnabled = Model.IsActive && !Model.IsReplacing;
        (_libraryOpen.Command as GuardedCommand)?.Refresh();
        if (!Model.IsActive)
        {
            ShowActions(null);
            StopTracking();
            return;
        }
        if (_library is not null)
        {
            _tracking = false;
            RefreshLibrary(workspace);
            return;
        }
        if (Model.IsReplacing)
            return;
        if (!_tracking)
            RefreshText();
        EmptyText.Text = Model.EmptyText;
        var inspecting = Model.HasContent && Model.IsVisible;
        var sectionChanged = _section != Model.Section;
        var content = inspecting ? LoadSection(Model.Section) : EmptyState;
        if (workspace)
            _motion.Show(content, forward: false);
        else if (inspecting && _section != Model.Section)
            _motion.Show(content, forward: Model.Section >= _section);
        else
            _motion.CrossFade(content);
        _section = Model.Section;
        _refreshing = true;
        Sections.IsEnabled = Model.IsVisible;
        foreach (var section in _torrentSections)
            section.IsEnabled = Model.CanShow((InspectorSection)section.Tag);
        Sections.SelectedItem = _torrentSections[(int)Model.Section];
        _refreshing = false;
        ShowActions(Model.Section == InspectorSection.Peers ? _peerMore : null);
        if (Model.Section == InspectorSection.Peers || sectionChanged)
        {
            ArrangePeers();
            _mapRefresh.Request();
        }
        var loading = Model.IsAvailable && Model.IsLoading;
        if (Peers is not null && Model.Section == InspectorSection.Peers)
        {
            if (!Model.FindsShownPeers)
                Peers.ItemsSource = Model.ShownPeers;
            ShowArrival(Peers, NoPeers, Model.Peers is not null);
        }
        if (TrackerTable is not null && Model.Section == InspectorSection.Trackers)
        {
            // Trackers come and go by the person's edits, and otherwise only once, when a
            // magnet's metadata arrives, so their arrivals and removals do not wait.
            if (TrackerTable.ItemsSource is IReadOnlyList<Tracker> shown && Model.Trackers is { } next)
                TrackerTable.Release([.. shown.Except(next), .. next.Except(shown)]);
            TrackerTable.ItemsSource = Model.Trackers;
            ShowArrival(TrackerTable, NoTrackers, Model.Trackers is not null);
        }
        if (_files is not null && Model.Section == InspectorSection.Files)
            _files.Model = Model.Files;
        if (Speed is not null && Model.Section == InspectorSection.Speed)
            Speed.Samples = Model.History;
        if (Model.Section == InspectorSection.Pieces)
            Map?.Show(Model.Pieces, Model.Text, Model.Cancellation);
        ShowGeneral();
        RevealFile();

        // A table without rows shows Loading while its read runs; its empty
        // text shows only once its rows have arrived.
        void ShowArrival(Syno.TableView.Table table, UIElement empty, bool arrived)
        {
            table.Placeholder =
                !arrived && loading
                    ? Syno.TableView.Placeholder.Loading
                    : Syno.TableView.Placeholder.Empty;
            empty.Visibility = arrived ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // A poll that changed only the target's facts updates what the bindings do
    // not reach, without rebuilding the pane. The facts arrive as one notice
    // each, so they are shown once, after the input already queued.
    private void ShowFacts()
    {
        if (!IsLoaded || _library is not null || !Model.IsActive || Model.IsReplacing)
        {
            return;
        }
        EmptyText.Text = Model.EmptyText;
        ShowGeneral();
        if (ShowsMap)
        {
            ShowTransfer();
        }
    }

    private void ShowGeneral()
    {
        if (General is null || Model.Section != InspectorSection.General)
        {
            return;
        }
        Progress.Style = Controls.Progress.SelectStyle(Model.Target is { IsProgressMuted: true });
        VisualStateManager.GoToState(
            this,
            Model.Target switch
            {
                { CanRetry: true } => "RetryableError",
                { IsError: true } => "Error",
                { State: TorrentState.FilesMissing } => "Missing",
                _ => "Transferring",
            },
            false
        );
    }

    internal void RefreshText()
    {
        Surface.RefreshText(Model.Text, "inspector");
        if (_library is null && !Model.IsVisible)
        {
            StopTracking();
            return;
        }
        if (!Model.IsReplacing || _library is not null)
            Bindings.Update();
        _tracking = _library is null;
        if (Trackers is not null)
            ActionButton.Align([EditTrackers, Reannounce]);
        if (_library is not null)
            Bindings.StopTracking();
        var text = Model.Text;
        FlowDirection = text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (var section in _torrentSections)
        {
            if (section.Tag is not InspectorSection kind)
                continue;
            section.Text = text.Get(kind);
            AutomationProperties.SetName(section, section.Text);
        }
        AutomationProperties.SetName(Sections, text.Get("inspector", "sections"));
        AutomationProperties.SetName(LibrarySections, text.Get("inspector", "sections"));
        if (Peers is not null)
        {
            Peers.Strings = text.Table;
            AddressColumn.DisplayName = text.Get("peers", "endpoint");
            PortColumn.DisplayName = text.Get("peers", "port");
            ClientColumn.DisplayName = text.Get("peers", "client");
            CountryColumn.DisplayName = text.Get("peers", "country");
            CityColumn.DisplayName = text.Get("peers", "city");
            PeerStateColumn.DisplayName = text.Get("peers", "state");
            ConnectionColumn.DisplayName = text.Get("peers", "connection_label");
            PeerProgressColumn.DisplayName = text.Get("columns", "progress");
            PeerDownColumn.DisplayName = text.Get("columns", "down");
            PeerDownColumn.ShortName = text.Get("columns", "down_short");
            PeerUpColumn.DisplayName = text.Get("columns", "up");
            PeerUpColumn.ShortName = text.Get("columns", "up_short");
            PeerDownloadedColumn.DisplayName = text.Get("inspector", "downloaded");
            PeerDownloadedColumn.ShortName = text.Get("columns", "downloaded_short");
            PeerUploadedColumn.DisplayName = text.Get("peers", "uploaded");
            PeerUploadedColumn.ShortName = text.Get("columns", "uploaded_short");
        }
        if (Trackers is not null)
        {
            TrackerTable.Strings = text.Table;
            AutomationProperties.SetName(TrackerInput, text.Get(InspectorSection.Trackers));
            UrlColumn.DisplayName = text.Get("trackers", "url");
            TierColumn.DisplayName = text.Get("trackers", "tier");
            TrackerStatusColumn.DisplayName = text.Get("columns", "status");
            SeedsColumn.DisplayName = text.Get("trackers", "seeds");
            LeechersColumn.DisplayName = text.Get("trackers", "leechers");
            DownloadsColumn.DisplayName = text.Get("trackers", "downloads");
            NextColumn.DisplayName = text.Get("trackers", "next");
            MessageColumn.DisplayName = text.Get("trackers", "message");
        }
        Speed?.RefreshText(text);
        RefreshPeerActions();
        _mapRefresh.Request();
        _files?.RefreshText();
        Peers?.RefreshView();
        TrackerTable?.RefreshView();
    }

    private void StopTracking()
    {
        CancelReveal();
        Bindings.StopTracking();
        _tracking = false;
        Map?.Show(null, Model.Text);
        if (PeerDrawing is not null)
            PeerDrawing.Content = null;
    }
}

public sealed record InspectorLayout(
    Syno.TableView.ColumnLayout? Peers,
    Syno.TableView.ColumnLayout? Trackers
)
{
    public PeerView View { get; init; } = PeerView.Default;
}

public sealed record PeerView(PeerLayout Layout, double Side, double Stacked)
{
    public bool Cities { get; init; }
    public MapCorner SpeedCorner { get; init; } = MapCorner.BottomRight;
    public MapAnimation Animation { get; init; } = MapAnimation.Normal;
    public MapDotTotal DotTotal { get; init; } = MapDotTotal.Normal;
    public int ArcLimit { get; init; } = MapRenderer.DefaultLimit;
    public MapGrouping Grouping { get; init; } = MapGrouping.Small;
    internal static PeerView Default { get; } = new(PeerLayout.Both, .55, .5);
}
