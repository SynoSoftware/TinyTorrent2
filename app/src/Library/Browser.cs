using System.ComponentModel;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Library;

public sealed partial class Browser : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<long, LibraryRow> _rows = [];
    private readonly Dictionary<LibraryConfiguration, Dictionary<string, string>> _filters = [];
    private readonly Dictionary<LibraryConfiguration, long> _selections = [];
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
    private bool _visible;
    private volatile bool _ready;
    private bool _connected;
    private bool _disposed;
    private string _query = string.Empty;
    private Exception? _failure;
    private LibraryConfiguration _configuration;
    private LibraryRow? _selected;

    public Strings Text { get; }
    public IReadOnlyList<LibraryRow> Rows { get; private set; } = [];
#if CAPTURE
    internal Task SearchCompletion => _searching ?? Task.CompletedTask;
    internal Task<int> PrepareTiming() => (_store ?? throw new InvalidOperationException("The timing capture has no store.")).PrepareTiming();
#endif
    public IReadOnlyList<Facet> Facets { get; private set; } = [];
    public IReadOnlyDictionary<LibraryConfiguration, int> Counts { get; private set; } = new Dictionary<LibraryConfiguration, int>();
    public LibraryRow? Selected => _selected;
    public bool IsAvailable => !_disposed && _ready && _connected && _failure is null &&
        _starting is { IsCompletedSuccessfully: true };
    public bool IsOpen { get; private set; }
    public bool IsFilterOpen { get; set; }
    public string Failure => _failure is null ? string.Empty : Text.Error(_failure);
    internal bool HasFailure => _failure is not null;
    public string Message => _selected?.OpeningFailure is not null ? _selected.Failure :
        IsEnrichmentEnabled && _fetchFailure is { } fetchFailure ? Error(fetchFailure) :
        IsEnrichmentEnabled && _videos?.Failure is { } failure ? Error(failure) : string.Empty;

    internal string Error(Exception error)
    {
        var message = Text.Error(error);
        return error is ProviderException { RetryAt: { } retry } && retry > DateTimeOffset.UtcNow
            ? Text.Format("library", "provider_retry_at", message, Text.Time(retry)) : message;
    }
    public string Status => Text.Format("filters", "count", Text.Get("library", Configuration.ToString().ToLowerInvariant()), Rows.Count) +
        (Filters.Count == 0 ? string.Empty : " · " + string.Join(" · ", Filters.Select(pair => FilterText(pair.Key, pair.Value)))) +
        (Configuration == LibraryConfiguration.Videos && !IsEnrichmentEnabled ? " · " + Text.Get("library", "information_off") : string.Empty);
    public bool IsEmpty => Query.Length == 0 && Filters.Count == 0 && !Counts.Values.Any(count => count > 0);
    public string EmptyText => Text.Get("library", IsEmpty ? "empty" : Configuration switch
    {
        LibraryConfiguration.Videos => "no_videos",
        LibraryConfiguration.Music => "no_music",
        _ => "no_matches",
    });
    public string Cast { get; private set; } = string.Empty;
    public string Synopsis { get; private set; } = string.Empty;
    public bool CanEnable => IsAvailable && _videos?.IsAvailable == true;
    public bool IsEnrichmentEnabled => _videos?.Enabled == true;
    public string InformationLabel => Text.Get("library", "video_information");
    public string InformationAction => Text.Get("library", IsEnrichmentEnabled ? "disable" : "enable");
    public string InformationTip => Text.Get("library", IsEnrichmentEnabled ? "disable_tip" : "enable_tip");
    public string IdentifyLabel => Text.Get("library", _selected?.Entry.Type.Length > 0 ? "edit" : "identify");
    public string IdentifyGlyph => Lucide.Pencil;
    public IReadOnlyList<Origin> Origins { get; private set; } = [];
    public string FileType { get; private set; } = string.Empty;
    public string Application { get; private set; } = string.Empty;
    public ImageSource? ApplicationIcon { get; private set; }
    public string OpenTip => Application.Length == 0 ? Text.Get("commands", "open") : Text.Format("library", "open_with", Application);
    public IReadOnlyDictionary<string, string> Filters =>
        _filters.TryGetValue(Configuration, out var filters) ? filters : new Dictionary<string, string>();
    public LibraryConfiguration Configuration
    {
        get => _configuration;
        set
        {
            if (_configuration == value)
                return;
            if (_selected is not null)
                _selections[_configuration] = _selected.Entry.EntryId;
            _configuration = value;
            Select(null);
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
    public ICommand ClearIdentification { get; }
    public ICommand Identify { get; }
    public ICommand Enable { get; }
    public ICommand Retry { get; }
    public ICommand ClearFilters { get; }
    public ICommand ShowTorrents { get; }
    internal event EventHandler<LibraryRow>? OpenRequested;
    internal event EventHandler<string>? FolderRequested;
    internal event EventHandler<string>? TorrentRequested;
    internal event EventHandler? IdentifyRequested;
    internal event EventHandler? EnableRequested;
    internal event EventHandler? TorrentsRequested;
    internal event EventHandler? RetryRequested;
    internal event EventHandler<Exception>? CommandFailed;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Browser(Strings text, DispatcherQueue dispatcher)
    {
        Text = text;
        _dispatcher = dispatcher;
        ShowTorrents = new RelayCommand(() =>
        {
            TorrentsRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }, () => true);
        Open = new RelayCommand(() =>
        {
            if (_selected is { } row)
                OpenRequested?.Invoke(this, row);
            return Task.CompletedTask;
        }, () => IsAvailable && _selected is not null);
        OpenFolder = new RelayCommand(() =>
        {
            if (_selected is { } row)
                FolderRequested?.Invoke(this, row.Entry.Folder);
            return Task.CompletedTask;
        }, () => IsAvailable && _selected is not null);
        Properties = new RelayCommand(() =>
        {
            IsOpen = true;
            _ = ReadDetail();
            Changed(nameof(IsOpen));
            return Task.CompletedTask;
        }, () => _selected is not null);
        Identify = new RelayCommand(() =>
        {
            IdentifyRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }, () => IsAvailable && IsEnrichmentEnabled && _selected?.Entry.Kind == FileKind.Video);
        Enable = new RelayCommand(() =>
        {
            EnableRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }, () => CanEnable);
        ClearIdentification = new RelayCommand(async () =>
        {
            if (_store is null || _selected is not { } row)
                return;
            try
            {
                await _store.ClearIdentification(row.Entry, CancellationToken.None);
                Refresh();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { ReportCommand(error); }
        }, () => IsAvailable && _selected is { Entry.Type.Length: > 0 });
        Retry = new RelayCommand(async () =>
        {
            if (_failure is null && _selected?.OpeningFailure is not null)
            {
                Open.Execute(null);
                return;
            }
            _failure = null;
            Changed(nameof(Failure));
            if (_starting is { IsFaulted: true } && _videos is not null)
                _starting = _videos.Initialize(Text.Language);
            RetryRequested?.Invoke(this, EventArgs.Empty);
            await Observe();
            if (!_disposed && _failure is null && _videos is not null && _starting is { IsCompletedSuccessfully: true })
            {
                try { await _videos.Retry(); }
                catch (OperationCanceledException) { }
                catch (Exception error) { ReportCommand(error); }
            }
        }, () => _connected);
        ClearFilters = new RelayCommand(() =>
        {
            _filters.Remove(Configuration);
            SearchChanged();
            return Task.CompletedTask;
        }, () => Filters.Count > 0);
        ConfigureInformation();
    }

    internal void Attach(Store store, VideoLookup videos, FileFacts facts)
    {
        _store = store;
        _videos = videos;
        _facts = facts;
        videos.Changed += OnVideosChanged;
        videos.Failed += OnWorkerFailed;
        facts.Changed += OnFactsChanged;
        facts.Failed += OnWorkerFailed;
        _starting = videos.Initialize(Text.Language);
        _ = Observe();
    }

    private void OnVideosChanged(object? sender, EventArgs args) => _dispatcher.TryEnqueue(RefreshInformation);
    private void OnFactsChanged(object? sender, EventArgs args) => _dispatcher.TryEnqueue(Refresh);
    private void OnWorkerFailed(object? sender, Exception error) => Report(error);

    internal Task SetEnabled(bool enabled) => _videos?.SetEnabled(enabled) ?? Task.CompletedTask;
    internal void RefreshInformation()
    {
        if (_disposed)
            return;
        Refresh();
        RefreshProviders();
        Changed(nameof(IsEnrichmentEnabled));
        Changed(nameof(Status));
        Changed(nameof(InformationAction));
        Changed(nameof(InformationTip));
        Changed(nameof(Message));
    }
    internal Task<IReadOnlyList<VideoChoice>> FindVideo(string query, CancellationToken cancellation) =>
        _videos is null ? Task.FromResult<IReadOnlyList<VideoChoice>>([]) : _videos.Search(query, cancellation);
    internal Task<bool> IdentifyVideo(Entry entry, VideoChoice choice, CancellationToken cancellation) =>
        _videos?.Identify(entry, choice, cancellation) ?? Task.FromResult(false);

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
            if (IsAvailable)
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

    public void Filter(string section, string? value)
    {
        if (!_filters.TryGetValue(Configuration, out var filters))
            _filters[Configuration] = filters = [];
        if (value is null)
            filters.Remove(section);
        else
            filters[section] = value;
        SearchChanged();
    }

    internal string FilterText(string section, string value) => section switch
    {
        "kind" when int.TryParse(value, out var kind) && Enum.IsDefined((FileKind)kind) =>
            Text.Get("library", ((FileKind)kind).ToString().ToLowerInvariant()),
        "type" => Text.Get("library", value.Length == 0 ? "unidentified" : value),
        "modified" or "size" => Text.Get("library", value switch
        {
            "empty" => "zero_size", "year" => "this_year", _ => value,
        }),
        _ => value,
    };

    internal void Refresh()
    {
        if (_disposed)
            return;
        _refreshDetail = true;
        RefreshRows();
        Changed(nameof(IsAvailable));
    }

    private void RefreshRows()
    {
        if (_disposed)
            return;
        _refresh = true;
        if (_visible && IsAvailable && _store is not null &&
            (_searching is null || _searching.IsCompleted))
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
                    Configurations = _filters.ToDictionary(pair => pair.Key,
                        pair => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(pair.Value)),
                };
                var matches = await _store.Search(query, cancellation.Token);
                if (revision != _revision || !IsAvailable || !_visible)
                    continue;
                var previous = _selected;
                var previousEntry = previous?.Entry;
                var refreshDetail = _refreshDetail;
                _refreshDetail = false;
                var retained = new HashSet<long>();
                var rows = new List<LibraryRow>(matches.Entries.Count);
                foreach (var entry in matches.Entries)
                {
                    retained.Add(entry.EntryId);
                    if (!_rows.TryGetValue(entry.EntryId, out var row))
                        _rows[entry.EntryId] = row = new LibraryRow(entry, Text);
                    else
                        row.Update(entry);
                    rows.Add(row);
                }
                foreach (var key in _rows.Keys.Where(key => !retained.Contains(key)).ToArray())
                    _rows.Remove(key);
                Rows = rows;
                Facets = matches.Facets;
                Counts = matches.Counts;
                if (_selected is null && _selections.TryGetValue(Configuration, out var remembered) &&
                    _rows.TryGetValue(remembered, out var restored))
                    Select(restored);
                if (_selected is { } selected && !retained.Contains(selected.Entry.EntryId))
                    Select(null);
                else if (_selected is not null && ReferenceEquals(previous, _selected) && IsOpen &&
                    (refreshDetail || previousEntry != _selected.Entry))
                    _ = ReadDetail();
                if (_selected is not null && previousEntry?.Path != _selected.Entry.Path)
                    Changed(nameof(Selected));
                if (previousEntry?.Type != _selected?.Entry.Type)
                    Changed(nameof(IdentifyLabel));
                Changed(nameof(Rows));
                Changed(nameof(Facets));
                Changed(nameof(Counts));
                Changed(nameof(Status));
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Report(error); }
            finally
            {
                if (ReferenceEquals(_search, cancellation))
                    _search = null;
            }
            if (revision == _revision && !_refresh)
                return;
        }
    }

    public void Select(LibraryRow? row)
    {
        if (ReferenceEquals(_selected, row))
            return;
        _selected = row;
        _detail?.Cancel();
        CancelInformation();
        _detailLoaded = false;
        _noVideoMatch = false;
        _fetchFailure = null;
        Cast = string.Empty;
        Synopsis = string.Empty;
        Origins = [];
        FileType = Application = string.Empty;
        ApplicationIcon = null;
        if (row is not null)
        {
            IsOpen = true;
            _ = ReadDetail();
        }
        Changed(nameof(Selected));
        Changed(nameof(IsOpen));
        Changed(nameof(Message));
    }

    private async Task ReadDetail()
    {
        if (_store is null || _selected is not { } selected || !IsAvailable)
            return;
        _detail?.Cancel();
        var entry = selected.Entry;
        using var cancellation = new CancellationTokenSource();
        _detail = cancellation;
        try
        {
            var detail = await _store.Detail(entry.EntryId, cancellation.Token);
            if (!ReferenceEquals(selected, _selected) || selected.Entry != entry ||
                detail is not null && detail.Entry != entry ||
                cancellation.IsCancellationRequested || !IsAvailable || !IsOpen)
                return;
            Cast = detail?.Cast ?? string.Empty;
            Synopsis = detail?.Synopsis ?? string.Empty;
            Origins = detail?.Origins ?? [];
            _detailLoaded = detail is not null;
            Changed(nameof(Cast));
            Changed(nameof(Synopsis));
            Changed(nameof(Origins));
            ScheduleInformation();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Report(error); }
        finally
        {
            if (ReferenceEquals(_detail, cancellation))
                _detail = null;
        }
    }

    internal void ShowTorrent(string origin)
    {
        if (IsAvailable && Origins.Any(item => item.OriginId == origin))
            TorrentRequested?.Invoke(this, origin);
    }

    internal void Close()
    {
        IsOpen = false;
        _detail?.Cancel();
        CancelInformation();
        Changed(nameof(IsOpen));
    }

    internal void ReportCommand(Exception error)
    {
        if (error is SqliteException or DatabaseVersionException)
            Report(error);
        else
            _dispatcher.TryEnqueue(() =>
            {
                if (!_disposed)
                    CommandFailed?.Invoke(this, error);
            });
    }

    internal void Report(Exception error)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed)
                return;
            _failure = error;
            RefreshReadiness();
            Changed(nameof(Failure));
            Changed(nameof(Message));
            Changed(nameof(IsAvailable));
        });
    }

    internal void Opened(LibraryRow row, string path, Exception? error)
    {
        if (row.Entry.Path != path)
            return;
        row.OpeningFailure = error;
        if (ReferenceEquals(row, _selected))
            Changed(nameof(Message));
    }

    internal void Associate(FileAssociation association)
    {
        var extension = Path.GetExtension(_selected?.Entry.Path) ?? string.Empty;
        FileType = association.Type.Length == 0 ? extension : association.Type + " (" + extension + ")";
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

    private void Changed(string property)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        if (property is not (nameof(IsAvailable) or nameof(Selected) or nameof(Rows) or nameof(Facets) or
            nameof(IsEnrichmentEnabled) or nameof(CanEnable) or nameof(CanConfigure) or nameof(IsOnDemand) or nameof(IsFetching) or ""))
            return;
        foreach (var command in new[] { Open, OpenFolder, Properties, Identify, Enable, ClearIdentification, Retry, ClearFilters, FetchDetails, ConfigureProvider })
            ((RelayCommand)command).Refresh();
    }

    internal Dictionary<LibraryConfiguration, Dictionary<string, string>> SaveFilters() =>
        _filters.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string>(pair.Value));

    internal void RestoreFilters(IReadOnlyDictionary<LibraryConfiguration, Dictionary<string, string>> filters)
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
        _search?.Cancel();
        _detail?.Cancel();
        CancelInformation();
        if (_videos is not null)
        {
            _videos.Changed -= OnVideosChanged;
            _videos.Failed -= OnWorkerFailed;
        }
        if (_facts is not null)
        {
            _facts.Changed -= OnFactsChanged;
            _facts.Failed -= OnWorkerFailed;
        }
    }

    public ValueTask DisposeAsync() => new(_closing ??= Drain());

    private async Task Drain()
    {
        Dispose();
        if (_starting is not null)
        {
            try { await _starting; }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        }
        if (_facts is not null)
            await _facts.DisposeAsync();
        if (_videos is not null)
            await _videos.DisposeAsync();
    }
}

public sealed class LibraryRow(Entry entry, Strings text) : INotifyPropertyChanged
{
    internal Exception? OpeningFailure { get; set; }
    public string Failure => OpeningFailure is null ? string.Empty :
        (OpeningFailure is Win32Exception { NativeErrorCode: 2 } ? text.Get("library", "missing") :
         OpeningFailure is Win32Exception { NativeErrorCode: 3 or 5 or 21 or 53 or 64 or 67 or 1231 } ? text.Get("library", "unavailable") :
         text.Get("library", "opening_error")) + " " + text.Error(OpeningFailure);
    public Entry Entry { get; private set; } = entry;
    public string Name => Entry.Name;
    public string Glyph =>
        OpeningFailure is not null ? Lucide.TriangleAlert :
        Entry.Kind == FileKind.Video && Entry.Type == "episode" ? Lucide.Tv :
        FileName.Glyph(Entry.Kind);
    public string Folder => Entry.Folder;
    public string Title => Entry.Title;
    public string Type => text.Get("library", Entry.Type.Length == 0 ? "unidentified" : Entry.Type);
    public string Year => Entry.Year?.ToString() ?? string.Empty;
    public string Genres => Entry.Genres;
    public string Cast => Entry.Cast;
    public string Artist => Entry.Artist;
    public string Album => Entry.Album;
    public string Track => Entry.Track?.ToString() ?? string.Empty;
    public string Duration => Entry.Duration is { } seconds ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : string.Empty;
    public string Size => text.Bytes(Entry.Size);
    public string Modified => Entry.Modified is { } time ? text.Time(DateTimeOffset.FromUnixTimeSeconds(time)) : string.Empty;
    public string Created => Entry.Created is { } time ? text.Time(DateTimeOffset.FromUnixTimeSeconds(time)) : string.Empty;
    public string Kind => text.Get("library", Entry.Kind.ToString().ToLowerInvariant());
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Update(Entry value)
    {
        if (Entry == value)
            return;
        Entry = value;
        Refresh();
    }
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
