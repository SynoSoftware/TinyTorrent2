using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;
using Selection = Syno.TableView.Selection;

namespace Syno.TinyTorrent.Library;

public sealed partial class Browser : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<long, LibraryRow> _rows = [];
    private readonly Dictionary<LibraryConfiguration, Dictionary<string, string>> _filters = [];
    private readonly Dictionary<LibraryConfiguration, Selection> _selections = [];
    private Store? _store;
    private VideoLookup? _videos;
    private FileFacts? _facts;
    private Task? _starting;
    private Task? _closing;
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _detail;
    private Task? _searching;
    private int _revision;
    private bool _refresh;
    private bool _refreshDetail;
    private bool _stored;
    private bool _pacing;
    private bool _searched;
    private bool _available;
    private string _emptyText = string.Empty;
    private bool _visible;
    private volatile bool _ready;
    private bool _connected;
    private bool _disposed;
    private string _query = string.Empty;
    private Exception? _failure;
    private LibraryConfiguration _configuration;

    public Strings Text { get; }
    public IReadOnlyList<LibraryRow> Rows { get; private set; } = [];
    // Results keep their configuration while a search for another one is pending.
    internal LibraryConfiguration RowsConfiguration { get; private set; }

    internal async Task<Coverage?> ReadCoverage()
    {
        if (!IsAvailable || _store is not { } store)
            return null;
        try
        {
            return await store.ReadCoverage();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (ReferenceEquals(store, _store))
                Report(error);
        }
        return null;
    }

#if CAPTURE
    internal Task SearchCompletion => _searching ?? Task.CompletedTask;

    internal Task<int> PrepareTiming() =>
        (
            _store ?? throw new InvalidOperationException("The timing capture has no store.")
        ).PrepareTiming();
#endif

    public IReadOnlyList<Facet> Facets { get; private set; } = [];
    public IReadOnlyDictionary<LibraryConfiguration, int> Counts { get; private set; } =
        new Dictionary<LibraryConfiguration, int>();
    public Selection Selection { get; private set; } = Selection.Empty;
    public LibraryRow? Selected =>
        Selection.Items.Count == 1 ? Selection.Items[0] as LibraryRow : null;
    public bool IsAvailable =>
        !_disposed
        && _connected
        && _failure is null
        && _starting is { IsCompletedSuccessfully: true };
    public string Failure => _failure is null ? string.Empty : Text.Error(_failure);
    internal bool HasFailure => _failure is not null;
    internal Exception? FailureCause => _failure;

    // Retry and RetryTip act on the failure that Message shows.
    public string Message =>
        Selected?.OpeningFailure is not null ? Selected.Failure
        : IsEnrichmentEnabled && _fetchFailure is { } fetchFailure ? Error(fetchFailure)
        : IsEnrichmentEnabled
        && Selected?.Entry.Kind == FileKind.Video
        && _videos?.Failure is { } failure
            ? Error(failure)
        : string.Empty;
    internal string RetryTip =>
        Selected?.OpeningFailure is not null ? Text.Get("library", "retry_open_tip")
        : Option is { } option ? Text.Format("library", "retry_provider_tip", option.Name)
        : string.Empty;

    /// <summary>
    /// Text such as "Waiting for TMDB" while a failure of the saved provider
    /// holds lookups back, otherwise null. Videos without metadata show it in the card.
    /// </summary>
    internal string? Waiting =>
        IsEnrichmentEnabled && _videos?.Failure is not null && Option is { } option
            ? Text.Format("library", "waiting", option.Name)
            : null;

    internal string Error(Exception error)
    {
        var message = Text.Error(error);
        return error is ProviderException { RetryAt: { } retry } && retry > DateTimeOffset.UtcNow
            ? Text.Format("library", "provider_retry_at", message, Text.Time(retry))
            : message;
    }

    public string Status =>
        Text.Format(
            "filters",
            "count",
            Text.Get("library", Configuration.ToString().ToLowerInvariant()),
            Rows.Count
        )
        + (
            Filters.Count == 0
                ? string.Empty
                : " · "
                    + string.Join(" · ", Filters.Select(pair => FilterText(pair.Key, pair.Value)))
        )
        + (
            Configuration != LibraryConfiguration.Videos ? string.Empty
            : !IsEnrichmentEnabled ? " · " + Text.Get("library", "download_off")
            : Waiting is { } waiting ? " · " + waiting
            : string.Empty
        );
    public bool IsEmpty =>
        Query.Length == 0 && Filters.Count == 0 && !Counts.Values.Any(count => count > 0);

    // Only a completed search of an available collection shows that nothing
    // matched; otherwise the table stays blank.
    public string EmptyText =>
        !_searched || !IsAvailable
            ? string.Empty
            : Text.Get(
                "library",
                IsEmpty
                    ? "empty"
                    : Configuration switch
                    {
                        LibraryConfiguration.Videos => "no_videos",
                        LibraryConfiguration.Music => "no_music",
                        _ => "no_matches",
                    }
            );
    private Detail? _loadedDetail;
    public string Cast => _loadedDetail?.Cast ?? string.Empty;
    public string Synopsis => _loadedDetail?.Synopsis ?? string.Empty;
    internal VideoChoice? Series => _loadedDetail?.Series;
    internal IReadOnlyList<Episode> Episodes => _loadedDetail?.Episodes ?? [];
    public bool IsProviderAvailable => _videos?.IsAvailable == true;
    public bool IsEnrichmentEnabled => _videos?.Enabled == true;
    private string? LookupCatalog => IsEnrichmentEnabled ? Provider?.CatalogId : null;
    public string DownloadLabel => Text.Get("library", "download_information");
    public string FetchMetadataLabel => Text.Get("library", "fetch_metadata");
    public string FetchMetadataGlyph => Lucide.Download;
    public IReadOnlyList<Origin> Origins => _loadedDetail?.Origins ?? [];
    public string FileType { get; private set; } = string.Empty;
    public string Application { get; private set; } = string.Empty;
    public ImageSource? ApplicationIcon { get; private set; }
    public string OpenTip =>
        Application.Length == 0
            ? Text.Get("commands", "open")
            : Text.Format("library", "open_with", Application);
    public IReadOnlyDictionary<string, string> Filters =>
        _filters.TryGetValue(Configuration, out var filters)
            ? filters
            : new Dictionary<string, string>();
    public LibraryConfiguration Configuration
    {
        get => _configuration;
        set
        {
            if (_configuration == value)
                return;
            _selections[_configuration] = Selection;
            _configuration = value;
            Select(Selection.Empty);
            SearchChanged();
            Changed(nameof(Configuration));
        }
    }
    public string Query
    {
        get => _query;
        set
        {
            if (_query == value)
                return;
            _query = value;
            SearchChanged();
            Changed(nameof(Query));
        }
    }

    public ICommand Open { get; }
    public ICommand OpenFolder { get; }
    public ICommand Properties { get; }
    public ICommand EraseMetadata { get; }
    public ICommand FetchMetadata { get; }
    public ICommand ShowSettings { get; }
    public ICommand Retry { get; }
    public ICommand ClearFilters { get; }
    public ICommand ShowTorrents { get; }
    internal event EventHandler<LibraryRow>? OpenRequested;
    internal event EventHandler<string>? FolderRequested;
    internal event EventHandler<Origin>? TorrentRequested;
    internal event EventHandler<ProviderOption>? FetchMetadataRequested;
    internal event EventHandler? SettingsRequested;
    internal event EventHandler? TorrentsRequested;
    internal event EventHandler? RetryRequested;
    internal event EventHandler? FiltersRequested;
    internal event EventHandler? PropertiesRequested;
    internal event EventHandler<Exception>? CommandFailed;

    // Raised with the number of files Fetch metadata left unidentified
    // because the search limit refused their search.
    internal event EventHandler<int>? BatchLimited;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Browser(Strings text, DispatcherQueue dispatcher)
    {
        Text = text;
        _dispatcher = dispatcher;
        ShowTorrents = new RelayCommand(
            () =>
            {
                TorrentsRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => true
        );
        Open = new RelayCommand(
            () =>
            {
                foreach (var row in Selection.Items.Cast<LibraryRow>().ToArray())
                    OpenRequested?.Invoke(this, row);
                return Task.CompletedTask;
            },
            () => IsAvailable && Selection.Items.Count > 0
        );
        OpenFolder = new RelayCommand(
            () =>
            {
                foreach (
                    var folder in Selection
                        .Items.Cast<LibraryRow>()
                        .Select(row => row.Entry.Folder)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                )
                    FolderRequested?.Invoke(this, folder);
                return Task.CompletedTask;
            },
            () => IsAvailable && Selection.Items.Count > 0
        );
        Properties = new RelayCommand(
            () =>
            {
                PropertiesRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => Selected is not null
        );
        FetchMetadata = new RelayCommand(
            () =>
            {
                if (Option is { } option)
                    Fetch(option);
                return Task.CompletedTask;
            },
            () =>
                CanLookup
                && Selection
                    .Items.Cast<LibraryRow>()
                    .Any(row =>
                        row.Entry.Kind == FileKind.Video
                        && (Selection.Items.Count == 1 || !row.Entry.HasMetadata)
                    )
        );
        ShowSettings = new RelayCommand(
            () =>
            {
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            },
            () => true
        );
        EraseMetadata = new RelayCommand(
            async () =>
            {
                if (_store is not { } store)
                    return;
                var entries = Selection
                    .Items.Cast<LibraryRow>()
                    .Select(row => row.Entry)
                    .Where(entry => entry.HasMetadata)
                    .ToArray();
                try
                {
                    foreach (var entry in entries)
                        await store.EraseMetadata(entry, CancellationToken.None);
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    if (ReferenceEquals(store, _store))
                        ReportCommand(error);
                }
                finally
                {
                    Refresh();
                }
            },
            () =>
                IsAvailable && Selection.Items.Cast<LibraryRow>().Any(row => row.Entry.HasMetadata)
        );
        Retry = new RelayCommand(
            () =>
            {
                if (Selected?.OpeningFailure is not null)
                    Open.Execute(null);
                else if (_fetchFailure is not null)
                    FetchDetails.Execute(null);
                else
                    _videos?.Retry();
                return Task.CompletedTask;
            },
            () =>
                Selected?.OpeningFailure is not null ? Open.CanExecute(null)
                : _fetchFailure is not null ? FetchDetails.CanExecute(null)
                : _videos?.Failure is { } failure && !(failure.RetryAt > DateTimeOffset.UtcNow)
        );
        ClearFilters = new RelayCommand(
            () =>
            {
                _filters.Remove(Configuration);
                SearchChanged();
                return Task.CompletedTask;
            },
            () => Filters.Count > 0
        );
        ConfigureInformation();
    }

    internal void Attach(Store store, VideoLookup videos, FileFacts facts)
    {
        _store = store;
        _videos = videos;
        _facts = facts;
        videos.Changed += OnVideosChanged;
        videos.Stored += OnStored;
        videos.BatchFailed += OnBatchFailed;
        videos.BatchLimited += OnBatchLimited;
        videos.Failed += OnWorkerFailed;
        facts.Changed += OnStored;
        facts.Failed += OnWorkerFailed;
        _starting = videos.Initialize(Text.Language);
        _ = Observe();
    }

    private void OnVideosChanged(object? sender, EventArgs args) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _videos))
                RefreshInformation();
        });

    private void OnStored(object? sender, EventArgs args) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _videos) || ReferenceEquals(sender, _facts))
                StoredChanged();
        });

    private void OnBatchFailed(object? sender, Exception error) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _videos))
                ReportCommand(error);
        });

    private void OnBatchLimited(object? sender, int files) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed && ReferenceEquals(sender, _videos))
                BatchLimited?.Invoke(this, files);
        });

    private void OnWorkerFailed(object? sender, Exception error) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _videos) || ReferenceEquals(sender, _facts))
                Report(error);
        });

    // Lookups and file facts can store many files a second, and each refresh
    // searches the Library again and replaces the table's rows. Their changes
    // refresh the rows at once, then at most once a second while they arrive.
    // The batch raises the same event for each file it finishes, so this also
    // shows its progress.
    private void StoredChanged()
    {
        _stored = true;
        if (!_pacing)
            _ = PaceRefresh();
    }

    private async Task PaceRefresh()
    {
        _pacing = true;
        while (_stored && !_disposed)
        {
            _stored = false;
            Refresh();
            ChangedBatch();
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        _pacing = false;
    }

    internal Task<IReadOnlyList<VideoChoice>> FindVideo(
        string query,
        ProviderOption source,
        CancellationToken cancellation
    ) =>
        _videos is null
            ? Task.FromResult<IReadOnlyList<VideoChoice>>([])
            : _videos.Search(query, source, cancellation);

    internal Task SaveMetadata(
        Entry entry,
        VideoChoice choice,
        ProviderOption source,
        CancellationToken cancellation
    ) =>
        (_videos ?? throw new InvalidOperationException("Library has no video lookup.")).Identify(
            entry,
            choice,
            source,
            cancellation
        );

    internal async Task Recover()
    {
        _failure = null;
        Changed(nameof(Failure));
        if (_starting is { IsFaulted: true } && _videos is not null)
            _starting = _videos.Initialize(Text.Language);
        RetryRequested?.Invoke(this, EventArgs.Empty);
        await Observe();
        if (
            !_disposed
            && _failure is null
            && _videos is not null
            && _starting is { IsCompletedSuccessfully: true }
        )
            _videos.Retry();
    }

    internal Task Observe(bool ready, bool connected)
    {
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;
            _ready = ready;
            _connected = connected;
        }
        return Observe();
    }

    private async Task Observe()
    {
        if (_disposed)
            return;
        if (!IsAvailable)
            RefreshReadiness();
        var starting = _starting;
        try
        {
            if (starting is not null)
                await starting;
            if (!_disposed && ReferenceEquals(starting, _starting))
                RefreshReadiness();
        }
        catch (Exception error)
        {
            if (!_disposed && ReferenceEquals(starting, _starting))
                Report(error);
        }
    }

    internal void Disconnect()
    {
        lock (_gate)
        {
            _ready = false;
            _connected = false;
            Pause();
        }
    }

    private void Pause()
    {
        if (_disposed)
            return;
        _facts?.SetConnected(false);
        _videos?.Refresh(false, Text.Language);
    }

    private void RefreshReadiness()
    {
        lock (_gate)
        {
            if (_ready && IsAvailable)
            {
                _facts?.SetConnected(true);
                _videos?.Refresh(true, Text.Language);
            }
            else
                Pause();
        }
        if (!IsAvailable)
        {
            _revision++;
            _search?.Cancel();
            _detail?.Cancel();
            CancelInformation();
        }
        Refresh();
    }

    // Their listeners rebuild menus and the empty state, which closes what
    // the person has open, so these notify only when they change.
    private void RefreshState()
    {
        if (_available != IsAvailable)
        {
            _available = IsAvailable;
            Changed(nameof(IsAvailable));
        }
        if (_emptyText != EmptyText)
        {
            _emptyText = EmptyText;
            Changed(nameof(EmptyText));
        }
    }

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible)
        {
            _search?.Cancel();
            _detail?.Cancel();
            CancelInformation();
        }
        else
            Refresh();
    }

    public void Filter(string section, string? value, int? season = null)
    {
        if (!_filters.TryGetValue(Configuration, out var filters))
            _filters[Configuration] = filters = [];
        if (value is null)
            filters.Remove(section);
        else
            filters[section] = value;
        if (section == "series")
        {
            if (value is not null && season is { } number)
                filters["season"] = number.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                );
            else
                filters.Remove("season");
        }
        FiltersRequested?.Invoke(this, EventArgs.Empty);
        SearchChanged();
    }

    internal void FilterSeries(VideoChoice series, int? season = null) =>
        Filter("series", Store.SeriesKey(series), season);

    internal string FilterText(string section, string value) =>
        section switch
        {
            "series" => Facets
                .FirstOrDefault(facet => facet.Section == section && facet.Value == value)
                ?.Label
                ?? Text.Get("library", "series"),
            "season" => Text.Format("library", "season_number", value),
            "kind" when int.TryParse(value, out var kind) && Enum.IsDefined((FileKind)kind) =>
                Text.Get("library", ((FileKind)kind).ToString().ToLowerInvariant()),
            "type" => Text.Get("library", value.Length == 0 ? "unidentified" : value),
            "modified" or "size" => Text.Get(
                "library",
                value switch
                {
                    "empty" => "zero_size",
                    "year" => "this_year",
                    _ => value,
                }
            ),
            _ => value,
        };

    internal void Refresh()
    {
        if (_disposed)
            return;
        _refreshDetail = true;
        RefreshRows();
        RefreshState();
        RefreshCommands();
    }

    private void RefreshRows()
    {
        if (_disposed)
            return;
        _refresh = true;
        if (
            _visible
            && IsAvailable
            && _store is not null
            && (_searching is null || _searching.IsCompleted)
        )
            _searching = Search();
    }

    private void SearchChanged()
    {
        _revision++;
        _search?.Cancel();
        RefreshRows();
        ((RelayCommand)ClearFilters).Refresh();
    }

    private async Task Search()
    {
        while (_visible && IsAvailable && _store is not null)
        {
            var revision = _revision;
            _refresh = false;
            using var cancellation = new CancellationTokenSource();
            _search = cancellation;
            try
            {
                var query = new Query(Query, Configuration)
                {
                    CatalogId = LookupCatalog,
                    Configurations = _filters.ToDictionary(
                        pair => pair.Key,
                        pair =>
                            (IReadOnlyDictionary<string, string>)
                                new Dictionary<string, string>(pair.Value)
                    ),
                };
                var matches = await _store.Search(query, cancellation.Token);
                if (revision != _revision || !IsAvailable || !_visible)
                    continue;
                var previous = Selected;
                var previousEntry = previous?.Entry;
                var refreshDetail = _refreshDetail;
                _refreshDetail = false;
                var retained = new HashSet<long>();
                var rows = new List<LibraryRow>(matches.Entries.Count);
                foreach (var entry in matches.Entries)
                {
                    retained.Add(entry.EntryId);
                    if (!_rows.TryGetValue(entry.EntryId, out var row))
                        _rows[entry.EntryId] = row = new LibraryRow(entry, this);
                    else
                        row.Update(entry);
                    rows.Add(row);
                }
                foreach (var key in _rows.Keys.Where(key => !retained.Contains(key)).ToArray())
                    _rows.Remove(key);
                Rows = rows;
                RowsConfiguration = query.Configuration;
                Facets = matches.Facets;
                Counts = matches.Counts;
                _searched = true;
                var selection =
                    _selections.Remove(Configuration, out var remembered)
                    && Selection.Items.Count == 0
                        ? remembered
                        : Selection;
                var items = selection
                    .Items.Cast<LibraryRow>()
                    .Where(row => retained.Contains(row.Entry.EntryId))
                    .Select(row => _rows[row.Entry.EntryId])
                    .ToArray();
                var current =
                    selection.Current is LibraryRow focused
                    && _rows.TryGetValue(focused.Entry.EntryId, out var restored)
                        ? restored
                        : items.FirstOrDefault();
                Select(new Selection(items, current));
                if (
                    Selected is not null
                    && ReferenceEquals(previous, Selected)
                    && (refreshDetail || previousEntry != Selected.Entry)
                )
                    _ = ReadDetail();
                if (Selected is not null && previousEntry?.Path != Selected.Entry.Path)
                    Changed(nameof(Selected));
                Changed(nameof(Rows));
                Changed(nameof(Facets));
                Changed(nameof(Counts));
                Changed(nameof(Status));
                RefreshState();
                RefreshCommands();
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested)
                    Report(error);
            }
            finally
            {
                if (ReferenceEquals(_search, cancellation))
                    _search = null;
            }
            if (revision == _revision && !_refresh)
                return;
        }
    }

    public void Select(LibraryRow? row) =>
        Select(row is null ? Selection.Empty : new Selection([row], row));

    internal void Select(Selection selection)
    {
        if (
            Selection.Items.SequenceEqual(selection.Items)
            && ReferenceEquals(Selection.Current, selection.Current)
        )
            return;
        var previous = Selected;
        Selection = selection;
        if (!ReferenceEquals(previous, Selected))
        {
            _detail?.Cancel();
            CancelInformation();
            _loadedDetail = null;
            _noVideoMatch = false;
            _fetchFailure = null;
            FileType = Application = string.Empty;
            ApplicationIcon = null;
            if (Selected is not null)
                _ = ReadDetail();
            Changed(nameof(Selected));
            Changed(nameof(Message));
        }
        Changed(nameof(Selection));
        RefreshCommands();
    }

    private async Task ReadDetail()
    {
        if (_store is null || Selected is not { } selected || !IsAvailable)
            return;
        _detail?.Cancel();
        var entry = selected.Entry;
        using var cancellation = new CancellationTokenSource();
        _detail = cancellation;
        try
        {
            var detail = await _store.Detail(entry.EntryId, LookupCatalog, cancellation.Token);
            if (
                !ReferenceEquals(selected, Selected)
                || selected.Entry != entry
                || cancellation.IsCancellationRequested
                || !IsAvailable
                || !_visible
            )
                return;
            var changed =
                _loadedDetail?.Entry != detail?.Entry
                || Cast != (detail?.Cast ?? string.Empty)
                || Synopsis != (detail?.Synopsis ?? string.Empty)
                || Series != detail?.Series
                || !Episodes.SequenceEqual(detail?.Episodes ?? [])
                || !Origins.SequenceEqual(detail?.Origins ?? []);
            _loadedDetail = detail;
            if (changed)
            {
                Changed(nameof(Cast));
                Changed(nameof(Synopsis));
                Changed(nameof(Origins));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
                Report(error);
        }
        finally
        {
            if (ReferenceEquals(_detail, cancellation))
                _detail = null;
        }
    }

    internal void ShowTorrent(string origin)
    {
        if (IsAvailable && Origins.FirstOrDefault(item => item.OriginId == origin) is { } source)
            TorrentRequested?.Invoke(this, source);
    }

    internal void ReportCommand(Exception error)
    {
        var store = _store;
        if (error is SqliteException or DatabaseVersionException)
            Report(error);
        else
            _dispatcher.TryEnqueue(() =>
            {
                if (!_disposed && ReferenceEquals(store, _store))
                    CommandFailed?.Invoke(this, error);
            });
    }

    internal void Report(Exception error)
    {
        var store = _store;
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || !ReferenceEquals(store, _store))
                return;
            _failure = error;
            RefreshReadiness();
            Changed(nameof(Failure));
            Changed(nameof(Message));
        });
    }

    internal void Opened(LibraryRow row, string path, Exception? error)
    {
        if (row.Entry.Path != path)
            return;
        row.OpeningFailure = error;
        row.Refresh();
        if (ReferenceEquals(row, Selected))
            Changed(nameof(Message));
    }

    internal void Associate(FileAssociation association)
    {
        var extension = Path.GetExtension(Selected?.Entry.Path) ?? string.Empty;
        FileType =
            association.Type.Length == 0 ? extension : association.Type + " (" + extension + ")";
        Application = association.Application;
        ApplicationIcon = association.Icon;
        Changed(nameof(FileType));
    }

    internal void RefreshText()
    {
        foreach (var row in Rows)
            row.Refresh();
        Changed(string.Empty);
    }

    private void Changed(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private void RefreshCommands()
    {
        foreach (
            var command in new[]
            {
                Open,
                OpenFolder,
                Properties,
                FetchMetadata,
                ShowSettings,
                EraseMetadata,
                Retry,
                ClearFilters,
                FetchDetails,
            }
        )
            ((RelayCommand)command).Refresh();
    }

    internal Dictionary<LibraryConfiguration, Dictionary<string, string>> SaveFilters() =>
        _filters.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string>(pair.Value));

    internal void RestoreFilters(
        IReadOnlyDictionary<LibraryConfiguration, Dictionary<string, string>> filters
    )
    {
        foreach (var pair in filters)
            if (Enum.IsDefined(pair.Key))
                _filters[pair.Key] = new Dictionary<string, string>(pair.Value);
        Refresh();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            Pause();
            _disposed = true;
        }
        Stop();
    }

    private void Stop()
    {
        Disconnect();
        _revision++;
        _search?.Cancel();
        _detail?.Cancel();
        CancelInformation();
        if (_videos is not null)
        {
            _videos.Changed -= OnVideosChanged;
            _videos.Stored -= OnStored;
            _videos.BatchFailed -= OnBatchFailed;
            _videos.BatchLimited -= OnBatchLimited;
            _videos.Failed -= OnWorkerFailed;
        }
        if (_facts is not null)
        {
            _facts.Changed -= OnStored;
            _facts.Failed -= OnWorkerFailed;
        }
    }

    public ValueTask DisposeAsync() => new(_closing ??= Drain());

    private async Task Drain()
    {
        Dispose();
        await Detach();
    }

    internal async Task Detach()
    {
        Stop();
        var starting = _starting;
        var searching = _searching;
        var facts = _facts;
        var videos = _videos;
        _starting = null;
        _store = null;
        _facts = null;
        _videos = null;
        _rows.Clear();
        _selections.Clear();
        Select(Selection.Empty);
        Rows = [];
        RowsConfiguration = Configuration;
        Facets = [];
        Counts = new Dictionary<LibraryConfiguration, int>();
        _searched = false;
        _failure = null;
        Changed(string.Empty);
        RefreshState();
        if (starting is not null)
        {
            try
            {
                await starting;
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine(error);
            }
        }
        if (facts is not null)
            await facts.DisposeAsync();
        if (videos is not null)
            await videos.DisposeAsync();
        if (searching is not null)
            await searching;
    }
}

public sealed class LibraryRow(Entry entry, Browser library) : INotifyPropertyChanged
{
    private Strings Text => library.Text;
    internal Exception? OpeningFailure { get; set; }
    public string Failure =>
        OpeningFailure is null
            ? string.Empty
            : (
                OpeningFailure is Win32Exception { NativeErrorCode: 2 }
                    ? Text.Get("library", "missing")
                : OpeningFailure
                    is Win32Exception { NativeErrorCode: 3 or 5 or 21 or 53 or 64 or 67 or 1231 }
                    ? Text.Get("library", "unavailable")
                : Text.Get("library", "opening_error")
            )
                + " "
                + Text.Error(OpeningFailure);
    public Entry Entry { get; private set; } = entry;
    public string Name => Entry.Name;
    public string Glyph =>
        OpeningFailure is not null ? Lucide.TriangleAlert
        : Entry.Kind == FileKind.Video && Entry.Type == "episode" ? Lucide.Tv
        : FileName.Glyph(Entry.Kind);
    public string Folder => Entry.Folder;
    public string Title => Entry.Title;
    public string Type => Text.Get("library", Entry.Type.Length > 0 ? Entry.Type : "unidentified");
    public string Year => Provided(Entry.Year?.ToString() ?? string.Empty);
    public string ImdbRating => Provided(Score(Entry.ImdbRating));
    public string TmdbRating => Provided(Score(Entry.TmdbRating));
    public string ImdbTip => RatingTip(Entry.ImdbRating);
    public string TmdbTip => RatingTip(Entry.TmdbRating);
    public string Genres => Provided(Entry.Genres);
    public string Cast => Provided(Entry.Cast);
    public string Artist => Entry.Artist;
    public string Album => Entry.Album;
    public string Subtitles =>
        Entry.Subtitles.Count == 1
            ? Text.LanguageName(Entry.Subtitles[0])
            : string.Join(", ", Entry.Subtitles.Select(language => language.ToUpperInvariant()));
    public string SubtitlesTip => string.Join(", ", Entry.Subtitles.Select(Text.LanguageName));
    public string Track => Entry.Track?.ToString() ?? string.Empty;
    public string Duration =>
        Entry.Duration is { } seconds
            ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss")
            : string.Empty;
    public string Bitrate =>
        Entry.Bitrate is { } bits
            ? bits >= 1_000_000
                ? Text.Format("library", "bitrate_mbps", bits / 1_000_000d)
                : Text.Format("library", "bitrate_kbps", bits / 1_000d)
            : string.Empty;
    public string Resolution =>
        Entry.Width is { } width && Entry.Height is { } height
            ? Text.Format("library", "dimensions", width, height)
            : string.Empty;
    public string Size => Text.Bytes(Entry.Size);
    public string Modified =>
        Entry.Modified is { } time
            ? Text.Time(DateTimeOffset.FromUnixTimeSeconds(time))
            : string.Empty;
    public string Created =>
        Entry.Created is { } time
            ? Text.Time(DateTimeOffset.FromUnixTimeSeconds(time))
            : string.Empty;
    public string Kind => Text.Get("library", Entry.Kind.ToString().ToLowerInvariant());

    private static string Score(Rating? rating) => rating?.Value.ToString("0.0") ?? string.Empty;

    // An empty provider cell of an unmatched file shows a dash, so a file the
    // provider does not have differs from one not looked up yet.
    private string Provided(string value) => value.Length == 0 && Entry.IsUnmatched ? "—" : value;

    private string RatingTip(Rating? rating) =>
        rating is null
            ? string.Empty
            : Text.Format("library", "rating", Score(rating))
                + (
                    rating.Kind == VideoKind.Series
                        ? " · " + Text.Get("library", "series_rating")
                        : string.Empty
                );

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(Entry value)
    {
        if (Entry == value)
            return;
        if (Entry.Path != value.Path)
            OpeningFailure = null;
        Entry = value;
        Refresh();
    }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
