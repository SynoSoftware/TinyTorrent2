using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class InspectorPane : UserControl
{
    private FileBrowser? _files;
    private InspectorLayout _layout = new(null, null);
    private readonly SelectorBarItem[] _torrentSections;
    private readonly Motion _motion = new();
    private readonly Motion _arrival = new();
    private Torrent? _target;
    private InspectorSection _section;
    private bool _refreshing;
    private bool _rowsQueued;
    private bool _tracking;
    private bool _awaitingArrival;
    private bool _composing;
    private (Torrent Target, int Index)? _fileReveal;
    public Inspector Model { get; }
    internal event EventHandler? LayoutChanged;
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
            _layout = value with { Peers = peers, View = view };
            if (Peers is not null && peers is not null)
                Peers.Layout = peers;
            if (TrackerTable is not null && value.Trackers is { } trackers)
                TrackerTable.Layout = trackers;
            ArrangePeers(fit: true);
            RefreshMap();
            RefreshPeerActions();

            static double Fraction(double value, double fallback) =>
                double.IsFinite(value) ? Math.Clamp(value, .15, .85) : fallback;
        }
    }

    public InspectorPane(Inspector model)
    {
        Model = model;
        InitializeComponent();
        _libraryOpen = CreateAction(Lucide.Play, "LibraryOpen");
        _libraryTorrents = CreateAction(Lucide.LocateFixed, "LibraryTorrents");
        _libraryMore = CreateAction(Lucide.Ellipsis, "LibraryMore");
        _peerMore = CreateAction(Lucide.Ellipsis, "PeerMore");
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
            Model.RecoveryRequested += OnRecovery;
            if (_library is { } library)
                library.PropertyChanged += OnLibrary;
            RefreshText();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            ShowActions(null);
            _motion.Stop();
            _arrival.Stop();
            Model.TextChanged -= OnText;
            Model.PropertyChanged -= OnModel;
            Model.RowsUpdated -= OnRows;
            Model.RecoveryRequested -= OnRecovery;
            if (_library is { } library)
                library.PropertyChanged -= OnLibrary;
            ClearLibraryActions();
            StopTracking();
            ClearLibraryContent(_libraryContent);
            ClearLibraryContent(_librarySpare);
        };
        RefreshText();
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
                Peers
                    .Schema<Peer>()
                    .Key(peer => peer.Key)
                    .SortKey(EndpointColumn, peer => peer.Endpoint)
                    .SortKey(ClientColumn, peer => peer.Client)
                    .SortKey(CountryColumn, peer => peer.CountryText)
                    .SortKey(CityColumn, peer => peer.CityText)
                    .SortKey(PeerStateColumn, peer => peer.State)
                    .SortKey(ConnectionColumn, peer => peer.ConnectionText)
                    .SortKey(PeerProgressColumn, peer => peer.Progress)
                    .SortKey(PeerDownColumn, peer => peer.DownloadRate)
                    .SortKey(PeerUpColumn, peer => peer.UploadRate)
                    .SortKey(PeerDownloadedColumn, peer => peer.Downloaded)
                    .SortKey(PeerUploadedColumn, peer => peer.Uploaded);
                if (_layout.Peers is { } peers)
                    Peers.Layout = peers;
                SetupPeers();
                break;
            case InspectorSection.Trackers:
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
        LoadSection(Model.Section);
        return Model.IsEditingTrackers ? TrackerInput : Retry;
    }

    private void OnRows(object? sender, InspectorSection section)
    {
        if (_library is not null || !Model.IsVisible || section != Model.Section || _rowsQueued)
            return;
        _rowsQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _rowsQueued = false;
            if (!IsLoaded || _library is not null || !Model.IsVisible)
                return;
            if (Model.Section == InspectorSection.Peers)
            {
                if (_layout.View.Layout != PeerLayout.Map)
                    Peers?.RefreshView();
                RefreshMap();
            }
            else if (Model.Section == InspectorSection.Trackers)
                TrackerTable?.RefreshView();
        });
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
        if (_library is not null)
        {
            _tracking = false;
            RefreshLibrary(workspace);
            return;
        }
        if (!Model.IsVisible)
        {
            ShowActions(null);
            StopTracking();
            return;
        }
        if (!_tracking)
            RefreshText();
        EmptyText.Text = Model.EmptyText;
        var inspecting = Model.HasContent && Model.IsVisible;
        var content = inspecting ? LoadSection(Model.Section) : EmptyState;
        if (workspace)
            _motion.Show(content, forward: false);
        else if (inspecting && _section != Model.Section)
            _motion.Show(content, forward: Model.Section >= _section);
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
        Sections.IsEnabled = Model.IsVisible;
        foreach (var section in _torrentSections)
            section.IsEnabled = Model.CanShow((InspectorSection)section.Tag);
        Sections.SelectedItem = Sections.Items.First(item => Equals(item.Tag, Model.Section));
        _refreshing = false;
        ShowActions(Model.Section == InspectorSection.Peers ? _peerMore : null);
        ArrangePeers();
        RefreshMap();
        var loading = Model.IsAvailable && Model.IsLoading;
        if (Peers is not null)
        {
            Peers.Placeholder =
                Model.Peers is null && loading
                    ? Syno.TableView.Placeholder.Loading
                    : Syno.TableView.Placeholder.Empty;
            NoPeers.Visibility = Model.Peers is null ? Visibility.Collapsed : Visibility.Visible;
        }
        if (TrackerTable is not null)
        {
            TrackerTable.Placeholder =
                Model.Trackers is null && loading
                    ? Syno.TableView.Placeholder.Loading
                    : Syno.TableView.Placeholder.Empty;
            NoTrackers.Visibility = Model.Trackers is null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        Map?.Show(Model.Pieces, Model.Text);
        if (General is not null)
        {
            Progress.Style = Controls.Progress.SelectStyle(
                Model.Target is { IsProgressMuted: true }
            );
            VisualStateManager.GoToState(
                this,
                Model.Target is { IsError: true } ? "Error" : "Transferring",
                false
            );
        }
        RevealFile();
    }

    internal void RefreshText()
    {
        Surface.RefreshText(Model.Text, "inspector");
        if (_library is null && !Model.IsVisible)
        {
            StopTracking();
            return;
        }
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
            section.Text = text.Get("inspector", kind.ToString().ToLowerInvariant());
            AutomationProperties.SetName(section, section.Text);
        }
        AutomationProperties.SetName(Sections, text.Get("inspector", "sections"));
        AutomationProperties.SetName(LibrarySections, text.Get("inspector", "sections"));
        if (Peers is not null)
        {
            Peers.Strings = text.Table;
            EndpointColumn.DisplayName = text.Get("peers", "endpoint");
            ClientColumn.DisplayName = text.Get("peers", "client");
            CountryColumn.DisplayName = text.Get("peers", "country");
            CityColumn.DisplayName = text.Get("peers", "city");
            PeerStateColumn.DisplayName = text.Get("peers", "state");
            ConnectionColumn.DisplayName = text.Get("peers", "connection_label");
            PeerProgressColumn.DisplayName = text.Get("columns", "progress");
            PeerDownColumn.DisplayName = text.Get("columns", "down");
            PeerUpColumn.DisplayName = text.Get("columns", "up");
            PeerDownloadedColumn.DisplayName = text.Get("inspector", "downloaded");
            PeerUploadedColumn.DisplayName = text.Get("peers", "uploaded");
        }
        if (Trackers is not null)
        {
            TrackerTable.Strings = text.Table;
            AutomationProperties.SetName(TrackerInput, text.Get("inspector", "trackers"));
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
        RefreshMap();
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
    internal static PeerView Default { get; } = new(PeerLayout.Both, .55, .5);
}
