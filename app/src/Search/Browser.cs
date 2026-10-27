using System.ComponentModel;
using System.Windows.Input;
using Syno.TableView;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using TableSelection = Syno.TableView.Selection;

namespace Syno.TinyTorrent.Search;

public sealed class Browser : INotifyPropertyChanged, IDisposable
{
    private const int ResultLimit = 1000;
    private readonly CatalogueClient _client;
    private readonly BackgroundWork _work;
    private BackgroundWork.Operation? _rebuild;
    private bool _cancelledRebuild;
    private string _query = string.Empty;
    private CatalogueTarget _target;
    private CatalogueSort _sort;
    private bool _descending;
    private string _extension = string.Empty;
    private string _torrent = string.Empty;
    private long? _minimum;
    private long? _maximum;
    private long _generation;
    private long _detailGeneration;
    private bool _visible;
    private bool _connected;
    private bool _disposed;
    private bool _readingStatus;
    private bool _readingPage;
    private Catalogue.Cursor? _next;
    private Catalogue.Entry? _entry;
    private Catalogue.Source? _pendingSource;
    private Catalogue.Hit? _hit;
    private long _statusRead;
    private Catalogue.Query _scope = new();
    private int _sourceOffset;
    private bool _moreSources;
    private bool _readingSources;
    private bool _limited;
    private readonly SemaphoreSlim _queries = new(1, 1);
    private CancellationTokenSource? _findDelay;

    internal Browser(Shared.Strings text, CatalogueClient client, BackgroundWork work)
    {
        Text = text;
        _client = client;
        _work = work;
        FileChoices = new(text);
        DownloadSelected = new RelayCommand(DownloadSelection, () => CanDownload && Selection.Items.Count > 0);
        DownloadWhole = new RelayCommand(() =>
        {
            if ((Selection.Current as Row ?? Selection.Items.OfType<Row>().FirstOrDefault()) is { } row)
                Download(row.Source, wholeTorrent: true);
        }, () => CanDownload && Selection.Items.Count > 0);
        DownloadFiles = new RelayCommand(DownloadFileSelection, () => CanDownload && FileChoices.HasWanted && _entry is not null);
        FileChoices.Changed += (_, _) => ((RelayCommand)DownloadFiles).Refresh();
        FileChoices.Failed += (_, error) => Failed?.Invoke(this, error);
        Refresh = new RelayCommand(RefreshAll, () => _connected);
        ClearFilters = new RelayCommand(ResetFilters,
            () => _extension.Length > 0 || _torrent.Length > 0 || _minimum.HasValue || _maximum.HasValue);
        DownloadTorrent = new RelayCommand(() =>
        {
            if (Inspected is { } source)
                Download(source, wholeTorrent: true);
            return Task.CompletedTask;
        }, () => CanDownload && Inspected is not null);
    }

    public Shared.Strings Text { get; }
    public Catalogue.Status State { get; private set; } = new();
    public IReadOnlyList<Row> Rows { get; private set; } = [];
    public IReadOnlyList<SourceRow> Sources { get; private set; } = [];
    public IReadOnlyList<AliasRow> Aliases { get; private set; } = [];
    public Selection Selection { get; private set; } = TableSelection.Empty;
    public FileSelection FileChoices { get; }
    public bool IsBusy { get; private set; }
    public bool IsLimited => _limited || State.Projecting || State.Rebuilding;
    public bool HasMore => _next is not null && Rows.Count < ResultLimit;
    public bool CanContinue => _connected && !IsBusy && !_readingPage && HasMore;
    public bool CanDownload => _connected && !IsBusy;
    public bool HasDatabase => State.Exists;
    public bool HasSelection => _hit is not null || _entry is not null || _pendingSource is not null;
    public bool InspectsTorrent => _entry is not null || _pendingSource is not null;
    public string Failure { get; private set; } = string.Empty;
    public CatalogueCondition Condition => State.ErrorKind;
    private Catalogue.Source? Inspected => _entry?.Torrent ?? _pendingSource;
    public bool InspectsUnverified => Inspected is { Verified: false };
    // An unverified entry's file list is a peer's claim and Download replaces
    // it, so the inspector offers no file choices for it.
    public bool OffersFiles => InspectsTorrent && !InspectsUnverified;
    public string UnverifiedTip => Text.Get("catalogue", "unverified_tip");
    public string Heading => Inspected?.Name ?? _hit?.Matched.File.Name ?? string.Empty;
    public string HeadingGlyph => InspectsTorrent ? Lucide.Package : Lucide.File;
    public string Size => (Inspected?.Size ?? _hit?.Matched.File.Size) is { } bytes ? Text.Bytes(bytes) : string.Empty;
    public string FileCount => Inspected?.Count.ToString("N0") ?? string.Empty;
    public string SourceCount => _hit is { } hit ? hit.Sources.Select(source => source.Hash).Distinct().Count().ToString("N0")
        + (hit.SourcesLimited ? "+" : string.Empty) : string.Empty;
    public string AliasCount => Aliases.Count.ToString("N0") + (_hit?.SourcesLimited == true ? "+" : string.Empty);
    public string Observed => Date(Inspected?.Availability.Observed);
    public string LastSeen => Date(Inspected?.Seen);
    public string Seeds => Count(Inspected?.Availability.Seeds);
    public string Peers => Count(Inspected?.Availability.Peers);
    public string EstimateTip => Text.Format("catalogue", "estimate_tip", Observed);
    public string Comment => _entry?.Comment is { Length: > 0 } comment ? comment : "—";
    public string Creator => _entry?.Creator is { Length: > 0 } creator ? creator : "—";
    public string Created => Date(_entry?.Created);
    public string HashV1 => Inspected?.V1 ?? string.Empty;
    public string HashV2 => Inspected?.V2 ?? string.Empty;
    public bool HasRows => Rows.Count > 0;
    public bool IsFirstUse => !State.Exists && Condition == CatalogueCondition.None && Failure.Length == 0 && !IsBusy;
    public bool ShowsContinue => HasMore && HasDatabase && Condition == CatalogueCondition.None && Failure.Length == 0;
    public bool ShowsClear => HasDatabase && Condition == CatalogueCondition.None && !IsBusy && Failure.Length == 0;
    public bool ShowsFolder => Condition != CatalogueCondition.None;
    public string RecoveryFolder => Condition == CatalogueCondition.Unavailable
        ? Path.GetDirectoryName(State.Directory) ?? State.Directory
        : State.Directory;
    public bool ShowsRepair => HasDatabase && Condition == CatalogueCondition.Damaged;
    public string EmptyText => Condition == CatalogueCondition.Damaged ? Text.Get("catalogue", "damaged")
        : Condition == CatalogueCondition.Unavailable ? Text.Get("catalogue", "inaccessible")
        : Failure.Length > 0 ? Failure
        : IsBusy || _readingPage ? Text.Get("catalogue", "searching")
        : !State.Exists ? Text.Get("catalogue", "first_use")
        : IsLimited ? Text.Get("catalogue", "no_matches_partial")
        : State.Torrents == 0 ? Text.Get("catalogue", "empty")
        : Text.Get("catalogue", "no_matches");
    public string EmptyDescription => Condition != CatalogueCondition.None
        ? Text.Get("catalogue", Condition == CatalogueCondition.Damaged ? "repair_help" : "access_help")
            + (State.Error.Length > 0 ? " " + State.Error : string.Empty)
        : IsFirstUse ? Text.Get("catalogue", "description")
        : State.Exists && Condition == CatalogueCondition.None && Failure.Length == 0 && !IsBusy
            ? Text.Get("catalogue", "no_matches_help") : string.Empty;
    public string Totals => _statusRead == 0 ? Text.Get("window", "connecting")
        : !_connected ? Text.Format("window", "last_known", Text.Format("catalogue", "indexed_counts", State.Torrents, State.Files))
        : Text.Format("catalogue", "indexed_counts", State.Torrents, State.Files);
    public string Matches => Text.Format("catalogue", Rows.Count >= ResultLimit ? "results_capped"
        : IsLimited ? "results_partial" : "results", Rows.Count);
    public string Rate => Text.Format("catalogue", "index_rate", State.TorrentsPerSecond);
    public bool CollectionUnavailable => !_connected || Condition != CatalogueCondition.None || State.Enabled && State.Error.Length > 0;
    public string Collection => CollectionUnavailable ? Text.Get("catalogue", "indexing_unavailable")
        : State.Enabled ? Text.Format("catalogue", "fetching", State.Acquiring) : Text.Get("catalogue", "indexing_off");
    public bool ShowsRate => State.Exists && State.Enabled && !CollectionUnavailable;
    public string Status
    {
        get
        {
            if (_statusRead == 0)
                return Text.Get("window", "connecting");
            var counts = Text.Format("catalogue", "indexed_counts", State.Torrents, State.Files);
            if (!_connected)
                return Text.Format("window", "last_known", counts);
            return counts
                + (State.Enabled && !CollectionUnavailable ? " · " + Text.Format("catalogue", "crawl_progress", State.Acquiring, State.Probing, State.Queued) : string.Empty)
                + " · " + Text.Format("catalogue", Rows.Count >= ResultLimit ? "results_capped" : IsLimited ? "results_partial" : "results", Rows.Count)
                + (Selection.Items.Count > 0 ? " · " + Text.Format("catalogue", "selected_count", Selection.Items.Count) : string.Empty)
                + " · " + Text.Get("catalogue", CollectionUnavailable ? "indexing_unavailable"
                    : State.Enabled ? "indexing_on" : "indexing_off")
                + " · " + Text.Bytes(State.Allocated) + " / " + Text.Bytes(State.BudgetMib * 1048576L)
                + (State.Allocated > State.BudgetMib * 1048576L ? " · " + Text.Get("catalogue", "above_budget") : string.Empty);
        }
    }
    public string StatusTip => !_connected || _statusRead == 0 ? Status : Status + " · "
        + Text.Format("catalogue", "discovery_details", State.Nodes, State.Sampled, State.Pending)
        + (ShowsRate ? " · " + Text.Format("catalogue", "index_rate_hint", State.TorrentsPerSecond) : string.Empty)
        + (CollectionUnavailable && State.Error.Length > 0 ? " · " + State.Error : string.Empty);
    public ICommand DownloadSelected { get; }
    public ICommand DownloadWhole { get; }
    public ICommand DownloadFiles { get; }
    public ICommand DownloadTorrent { get; }
    public ICommand Refresh { get; }
    public ICommand ClearFilters { get; }
    public void ClearSearch()
    {
        _query = string.Empty;
        Changed(nameof(Query));
        ResetFilters();
    }

    private void ResetFilters()
    {
        _extension = string.Empty;
        _torrent = string.Empty;
        _minimum = _maximum = null;
        Changed(nameof(Extension));
        Changed(nameof(Torrent));
        Changed(nameof(Minimum));
        Changed(nameof(Maximum));
        _ = Find();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal event EventHandler<DownloadChoice>? DownloadRequested;
    internal event EventHandler? InspectorRequested;
    internal event EventHandler<Exception>? Failed;
    internal event EventHandler<string>? CopyRequested;

    public string Query
    {
        get => _query;
        set
        {
            if (_query == value)
                return;
            _query = value;
            Changed(nameof(Query));
            _ = Find();
        }
    }
    public CatalogueTarget Target
    {
        get => _target;
        set
        {
            if (_target == value)
                return;
            _target = value;
            Changed(nameof(Target));
            _ = Find();
        }
    }
    public string Extension
    {
        get => _extension;
        set
        {
            if (_extension == value)
                return;
            _extension = value;
            Changed(nameof(Extension));
            _ = Find();
        }
    }
    public long? Minimum
    {
        get => _minimum;
        set { if (_minimum != value) { _minimum = value; _ = Find(); } }
    }
    public string Torrent
    {
        get => _torrent;
        set
        {
            if (_torrent == value)
                return;
            _torrent = value;
            Changed(nameof(Torrent));
            _ = Find();
        }
    }
    public long? Maximum
    {
        get => _maximum;
        set { if (_maximum != value) { _maximum = value; _ = Find(); } }
    }

    internal void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible)
            return;
        _ = Rows.Count == 0 ? RefreshAll() : RefreshStatus();
        if (_pendingSource is { } pending)
            _ = Inspect(pending, open: false);
        else if (!HasSelection && Selection.Current is Row)
            Select(Selection);
    }

    internal void Connect(bool connected)
    {
        if (_connected == connected)
            return;
        _connected = connected;
        RaiseCommands();
        if (connected)
            _ = _visible ? RefreshAll() : RefreshStatus();
        else if (!connected)
        {
            ++_generation;
            ++_detailGeneration;
            IsBusy = false;
            Raise();
        }
    }

    internal void Observe(bool settingsVisible = false)
    {
        if ((_visible || settingsVisible || State.Rebuilding) && Environment.TickCount64 - _statusRead > (State.Rebuilding ? 1000 : _visible && State.Enabled ? 2000 : 5000))
            _ = RefreshStatus();
    }

    public async Task RefreshStatus()
    {
        if (!_connected || _readingStatus || _disposed)
            return;
        _readingStatus = true;
        try
        {
            var previous = State;
            var state = await _client.StatusAsync();
            if (_disposed || !_connected)
                return;
            State = state;
            ObserveRebuild();
            _statusRead = Environment.TickCount64;
            Raise();
            if (_visible && (state.Generation != (_scope.Cursor?.Generation ?? 0) && Rows.Count > 0
                || previous.Rebuilding && !state.Rebuilding
                || Rows.Count == 0 && _next is null && !IsBusy && !_readingPage && state.Torrents > previous.Torrents))
                _ = Find();
        }
        catch (Exception error)
        {
            if (!_disposed)
                SetFailure(error);
        }
        finally { _readingStatus = false; }
    }

    internal async Task Rebuild()
    {
        try
        {
            await _client.RebuildAsync();
            await RefreshStatus();
        }
        catch (Exception error)
        {
            SetFailure(error);
            Failed?.Invoke(this, error);
        }
    }

    private async Task RefreshAll()
    {
        await RefreshStatus();
        await Find();
    }

    internal Task SubmitSearch() => Find();

    internal void Sort(CatalogueSort sort, bool descending)
    {
        _sort = sort;
        _descending = descending;
        _ = Find();
    }

    private Catalogue.Query Request(Catalogue.Cursor? cursor = null) => new()
    {
        Text = _query,
        Target = _target,
        Sort = _sort,
        Descending = _descending,
        Extension = _extension,
        Torrent = _torrent,
        Size = new() { Minimum = _minimum, Maximum = _maximum },
        Cursor = cursor,
        Limit = 100,
    };

    private async Task Find(string? content = null)
    {
        if (!_visible || !_connected || _disposed)
            return;
        var generation = ++_generation;
        _next = null;
        var request = Request();
        _findDelay?.Cancel();
        using var delay = new CancellationTokenSource();
        _findDelay = delay;
        try
        {
            await Task.Delay(150, delay.Token);
            await _queries.WaitAsync(delay.Token);
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (ReferenceEquals(_findDelay, delay))
                _findDelay = null;
        }
        try
        {
            if (!_disposed && _visible && _connected && generation == _generation)
                await Find(request, generation, content);
        }
        finally { _queries.Release(); }
    }

    private async Task Find(Catalogue.Query request, long generation, string? content)
    {
        ++_detailGeneration;
        _next = null;
        Rows = [];
        Selection = TableSelection.Empty;
        _entry = null;
        _pendingSource = null;
        _hit = null;
        Sources = [];
        Aliases = [];
        FileChoices.Clear();
        if (!State.Exists)
        {
            IsBusy = false;
            Failure = string.Empty;
            _limited = false;
            Raise();
            return;
        }
        IsBusy = true;
        Failure = string.Empty;
        Raise();
        try
        {
            var page = await _client.QueryAsync(request);
            if (_disposed || generation != _generation)
                return;
            Rows = page.Hits.Select(hit => new Row(this, hit)).ToArray();
            _next = page.Next;
            _scope = request with
            {
                Cursor = new() { Watermark = page.Watermark, Generation = page.Generation },
            };
            _limited = page.Limited || !page.Exhaustive;
            Selection = TableSelection.Empty;
            _hit = null;
            _entry = null;
            _pendingSource = null;
            Sources = [];
            Aliases = [];
            FileChoices.Clear();
            if (content is not null && Rows.FirstOrDefault(row => row.ContentKey == content) is { } selected)
                Select(new Selection([selected], selected));
        }
        catch (Exception error)
        {
            if (generation == _generation && !_disposed)
                SetFailure(error);
        }
        finally
        {
            if (generation == _generation && !_disposed)
            {
                IsBusy = false;
                Raise();
            }
        }
    }

    internal async Task More()
    {
        if (_next is null || _readingPage || !_visible || !_connected || Rows.Count >= ResultLimit)
            return;
        var generation = _generation;
        _readingPage = true;
        Raise();
        await _queries.WaitAsync();
        try
        {
            if (_disposed || !_visible || !_connected || generation != _generation)
                return;
            var count = Rows.Count;
            do
            {
                var page = await _client.QueryAsync(_scope with { Cursor = _next, Limit = Math.Min(100, ResultLimit - Rows.Count) });
                if (_disposed || !_visible || !_connected || generation != _generation)
                    return;
                var existing = Rows.Select(row => row.ContentKey).ToHashSet();
                Rows = [.. Rows, .. page.Hits.Select(hit => new Row(this, hit)).Where(row => existing.Add(row.ContentKey))];
                _next = page.Next;
                _limited = page.Limited || !page.Exhaustive;
                Raise();
            }
            while (Rows.Count == count && HasMore);
        }
        catch (Exception error)
        {
            if (generation == _generation && !_disposed)
                SetFailure(error);
        }
        finally
        {
            _queries.Release();
            _readingPage = false;
            if (!_disposed)
                Raise();
        }
    }

    internal void Select(Selection selection)
    {
        Selection = selection;
        var row = selection.Current as Row ?? selection.Items.OfType<Row>().FirstOrDefault();
        if (row is not null)
        {
            if (row.IsSource || Target == CatalogueTarget.Torrents)
                _ = Inspect(row.Source, open: false);
            else
            {
                ++_detailGeneration;
                _entry = null;
                _pendingSource = null;
                _hit = row.Hit;
                _sourceOffset = row.Hit.Sources.Count;
                _moreSources = row.Hit.SourcesLimited;
                Sources = row.Hit.Sources.DistinctBy(source => source.Hash).Select(source => new SourceRow(this, source)).ToArray();
                Aliases = row.Hit.Aliases.Select(name => new AliasRow(this, name, row.Hit)).ToArray();
                FileChoices.Clear();
            }
        }
        else
        {
            ++_detailGeneration;
            _entry = null;
            _pendingSource = null;
            _hit = null;
            Sources = [];
            Aliases = [];
            FileChoices.Clear();
        }
        Raise();
    }

    internal async Task MoreSources()
    {
        if (!_moreSources || _readingSources || _hit is not { } hit || _entry is not null || Sources.Count >= 1000)
            return;
        var generation = _detailGeneration;
        _readingSources = true;
        try
        {
            var page = await _client.SourcesAsync(hit.Matched, _scope, _sourceOffset);
            if (_disposed || generation != _detailGeneration)
                return;
            var known = hit.Sources.Select(source => (source.Hash, source.File.Index)).ToHashSet();
            var combined = hit with
            {
                Sources = [.. hit.Sources, .. page.Sources.Where(source => known.Add((source.Hash, source.File.Index)))],
                Aliases = hit.Aliases.Concat(page.Aliases).Distinct(StringComparer.Ordinal).ToArray(),
                SourcesLimited = page.More || page.Limited,
            };
            _hit = combined;
            _sourceOffset = page.Next;
            _moreSources = page.More;
            Sources = combined.Sources.DistinctBy(source => source.Hash).Select(source => new SourceRow(this, source)).ToArray();
            Aliases = combined.Aliases.Select(name => new AliasRow(this, name, combined)).ToArray();
            foreach (var row in Rows)
                if (row.ContentKey == Row.Content(hit.Matched))
                    row.UpdateSources(combined);
            Rows = Rows.ToArray();
            Raise();
        }
        catch (Exception error)
        {
            if (!_disposed && generation == _detailGeneration)
                SetFailure(error);
        }
        finally { _readingSources = false; }
    }

    internal async Task Inspect(Catalogue.Source source, bool open = true)
    {
        var fromFile = Target == CatalogueTarget.Files;
        var generation = ++_detailGeneration;
        _entry = null;
        _pendingSource = source;
        _hit = null;
        FileChoices.Clear();
        Raise();
        try
        {
            var entry = await _client.DetailAsync(source.Hash);
            if (_disposed || generation != _detailGeneration || !_visible)
                return;
            _entry = entry;
            _pendingSource = null;
            if (entry.Torrent.Verified)
            {
                FileChoices.InitializePage(Page(entry), async (offset, query) =>
                {
                    var next = await _client.DetailAsync(source.Hash, offset, 200, query);
                    if (generation != _detailGeneration)
                        throw new OperationCanceledException();
                    _entry = next;
                    return Page(next);
                }, defaultPriority: 0);
                if (fromFile && !source.File.Padding && !source.File.Symlink)
                    FileChoices.Select(0, [new(source.File.Index, 4, source.File.Size, 0)]);
            }
            Raise();
            if (open)
                InspectorRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            if (!_disposed && generation == _detailGeneration)
            {
                SetFailure(error);
                Failed?.Invoke(this, error);
            }
        }
    }

    private static FilePage Page(Catalogue.Entry entry) => new()
    {
        Files = entry.Files.Select(file => new FileEntry(file.Index, file.Path, file.Size, 0, file.Padding)).ToArray(),
        Offset = entry.Offset,
        Next = entry.More ? entry.Next : null,
        Count = entry.Torrent.Count,
        SelectableCount = entry.SelectableCount,
        Bytes = entry.Torrent.Size,
    };

    internal void Download(Catalogue.Source source, bool wholeTorrent) =>
        DownloadRequested?.Invoke(this, new()
        {
            Source = source,
            Scope = _scope,
            WholeTorrent = wholeTorrent,
            Files = wholeTorrent ? null : [source.File.Index],
        });

    private Task DownloadSelection()
    {
        foreach (var group in Selection.Items.OfType<Row>()
            .DistinctBy(row => row.ContentKey)
            .GroupBy(row => row.Source.Hash))
        {
            var source = group.First().Source;
            DownloadRequested?.Invoke(this, new()
            {
                Source = source,
                Scope = _scope,
                WholeTorrent = Target == CatalogueTarget.Torrents,
                Files = Target == CatalogueTarget.Torrents ? null : group.Select(row => row.Source.File.Index).Distinct().ToArray(),
            });
        }
        return Task.CompletedTask;
    }

    private Task DownloadFileSelection()
    {
        if (_entry is { } entry)
        {
            var choices = FileChoices.Choices().Select(choice => new Catalogue.Choice
            {
                Index = choice.Index,
                Priority = choice.Priority,
            }).ToArray();
            DownloadRequested?.Invoke(this, new()
            {
                Source = entry.Torrent,
                Scope = _scope,
                DefaultPriority = FileChoices.DefaultPriority ?? 0,
                Choices = choices,
            });
        }
        return Task.CompletedTask;
    }

    internal void SearchAlias(string name)
    {
        var content = _hit is { } hit ? Row.Content(hit.Matched) : null;
        _query = "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        Changed(nameof(Query));
        _ = Find(content);
    }
    internal void CopyAlias(string name) => CopyRequested?.Invoke(this, name);
    internal void InspectorRequestedFromView() => InspectorRequested?.Invoke(this, EventArgs.Empty);
    internal string Date(long? value) => value is > 0
        ? Text.Time(DateTimeOffset.FromUnixTimeSeconds(value.Value))
        : "—";
    internal string Count(long? value) => value.HasValue ? value.Value.ToString("N0") : "—";

    internal void RefreshText()
    {
        Rows = Rows.Select(row => new Row(this, row.Hit) { IsExpanded = row.IsExpanded }).ToArray();
        Sources = Sources.Select(row => new SourceRow(this, row.Source)).ToArray();
        Aliases = _hit is { } hit ? hit.Aliases.Select(name => new AliasRow(this, name, hit)).ToArray() : [];
        Raise();
    }

    private void ObserveRebuild()
    {
        if (!State.Rebuilding)
        {
            if (_rebuild is { } finished)
            {
                finished.Outcome = State.Error.Length > 0 ? WorkOutcome.Failed
                    : _cancelledRebuild ? WorkOutcome.Cancelled : WorkOutcome.Completed;
                if (State.Error.Length > 0)
                    finished.Error = new IOException(State.Error);
                finished.Dispose();
                _rebuild = null;
            }
            _cancelledRebuild = false;
            return;
        }
        WorkProgress progress = State.Torrents > 0
            ? new WorkProgress.Fraction(Math.Clamp(State.Rebuilt, 0, State.Torrents), State.Torrents, WorkUnit.Percent)
            : new WorkProgress.Unknown();
        var observation = new WorkObservation(new WorkDescription.Maintenance(MaintenanceWork.Rebuild),
            progress, new WorkExecution.Running());
        _rebuild ??= _work.Begin(observation, async () =>
        {
            try
            {
                await _client.RebuildAsync(cancel: true);
                _cancelledRebuild = true;
                await RefreshStatus();
            }
            catch (Exception error) { SetFailure(error); }
        });
        _rebuild.Report(observation);
    }
    private void SetFailure(Exception error)
    {
        Failure = error.Message;
        Raise();
    }
    private void Raise()
    {
        Changed(string.Empty);
        RaiseCommands();
    }
    private void RaiseCommands()
    {
        ((RelayCommand)DownloadSelected).Refresh();
        ((RelayCommand)DownloadWhole).Refresh();
        ((RelayCommand)DownloadFiles).Refresh();
        ((RelayCommand)DownloadTorrent).Refresh();
        ((RelayCommand)Refresh).Refresh();
        ((RelayCommand)ClearFilters).Refresh();
        foreach (var row in Rows)
            row.RefreshCommands();
    }
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    public void Dispose()
    {
        _disposed = true;
        _findDelay?.Cancel();
        ++_generation;
        ++_detailGeneration;
        if (_rebuild is { } operation)
        {
            operation.ReportsSuccess = false;
            operation.Outcome = WorkOutcome.Completed;
            operation.Dispose();
            _rebuild = null;
        }
        _client.Withdraw();
    }
}

internal sealed record DownloadChoice
{
    public required Catalogue.Source Source { get; init; }
    public required Catalogue.Query Scope { get; init; }
    public bool WholeTorrent { get; init; }
    public IReadOnlyList<int>? Files { get; init; }
    public int? DefaultPriority { get; init; }
    public IReadOnlyList<Catalogue.Choice>? Choices { get; init; }
}

public sealed class Row
{
    private readonly Browser _owner;
    internal Catalogue.Hit Hit { get; private set; }
    internal Catalogue.Source Source { get; }
    internal bool IsSource { get; }
    internal static string Content(Catalogue.Source source) => source.File.Root.Length > 0
        ? source.File.Root + ":" + source.File.Size
        : source.Hash + ":" + source.File.Index;
    internal string ContentKey => _owner.Target == CatalogueTarget.Torrents ? Source.Hash : Content(Hit.Matched);
    public string Key => IsSource ? "source:" + ContentKey + ":" + Source.Hash + ":" + Source.File.Index : "hit:" + ContentKey;
    public bool IsExpanded { get; set; }
    public IReadOnlyList<Row> Children { get; private set; }
    public string Name => IsSource || _owner.Target == CatalogueTarget.Torrents ? Source.Name : Hit.Matched.File.Name;
    public long Bytes => _owner.Target == CatalogueTarget.Torrents ? Source.Size : Source.File.Size;
    public string Size => _owner.Text.Bytes(Bytes);
    public string Sources => IsSource || _owner.Target == CatalogueTarget.Torrents ? string.Empty
        : Hit.Sources.Select(source => source.Hash).Distinct().Count().ToString("N0") + (Hit.SourcesLimited ? "+" : string.Empty);
    public string Seeds => _owner.Count(Source.Availability.Seeds);
    public string Peers => _owner.Count(Source.Availability.Peers);
    public string Observed => _owner.Date(Source.Availability.Observed);
    public string Tip => _owner.Text.Format("catalogue", "source_path", Source.Name, Source.File.Path);
    public string FileTip => _owner.Text.Format("catalogue", "download_file_tip", Source.Name, Source.File.Path);
    public string TorrentTip => _owner.Text.Format("catalogue", "download_torrent_tip", Source.Name);
    public string EstimateTip => _owner.Text.Format("catalogue", "estimate_tip", _owner.Date(Source.Availability.Observed));
    public string Glyph => IsSource || _owner.Target == CatalogueTarget.Torrents ? Lucide.Package : Lucide.File;
    public Microsoft.UI.Xaml.Visibility ShowsFile => !IsSource && _owner.Target == CatalogueTarget.Files
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string UnverifiedTip => _owner.UnverifiedTip;
    public Microsoft.UI.Xaml.Visibility ShowsUnverified => Source.Verified
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public string DownloadText => _owner.Text.Get("catalogue", "download");
    public ICommand DownloadFile { get; }
    public ICommand DownloadTorrent { get; }

    internal Row(Browser owner, Catalogue.Hit hit, Catalogue.Source? source = null)
    {
        _owner = owner;
        Hit = hit;
        Source = source ?? hit.Preferred;
        IsSource = source is not null;
        Children = !IsSource && owner.Target == CatalogueTarget.Files
            ? hit.Sources.DistinctBy(item => item.Hash).Select(item => new Row(owner, hit, item)).ToArray() : [];
        DownloadFile = new RelayCommand(() => { owner.Download(Source, false); return Task.CompletedTask; }, () => owner.CanDownload);
        DownloadTorrent = new RelayCommand(() => { owner.Download(Source, true); return Task.CompletedTask; }, () => owner.CanDownload);
    }

    internal void RefreshCommands()
    {
        ((RelayCommand)DownloadFile).Refresh();
        ((RelayCommand)DownloadTorrent).Refresh();
        foreach (var row in Children)
            row.RefreshCommands();
    }

    internal void UpdateSources(Catalogue.Hit hit)
    {
        Hit = hit;
        Children = hit.Sources.DistinctBy(source => source.Hash).Select(source => new Row(_owner, hit, source)).ToArray();
    }
}

public sealed class SourceRow
{
    internal Catalogue.Source Source { get; }
    public string Key => Source.Hash;
    public string Name => Source.Name;
    public string Size { get; }
    public string Files { get; }
    public string Seeds { get; }
    public string Peers { get; }
    public string Observed { get; }
    public string LastSeen { get; }
    public string Tip => Source.File.Path;
    public string EstimateTip { get; }
    public string InspectText { get; }
    public string UnverifiedTip { get; }
    public Microsoft.UI.Xaml.Visibility ShowsUnverified => Source.Verified
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public ICommand Inspect { get; }
    internal SourceRow(Browser owner, Catalogue.Source source)
    {
        Source = source;
        UnverifiedTip = owner.UnverifiedTip;
        Size = owner.Text.Bytes(source.Size);
        Files = source.Count.ToString("N0");
        Seeds = owner.Count(source.Availability.Seeds);
        Peers = owner.Count(source.Availability.Peers);
        Observed = owner.Date(source.Availability.Observed);
        LastSeen = owner.Date(source.Seen);
        EstimateTip = owner.Text.Format("catalogue", "estimate_tip", Observed);
        InspectText = owner.Text.Get("catalogue", "inspect");
        Inspect = new RelayCommand(() => owner.Inspect(source), () => true);
    }
}

public sealed class AliasRow
{
    public string Name { get; }
    public string Torrents { get; }
    public string SearchText { get; }
    public string CopyText { get; }
    public ICommand Search { get; }
    public ICommand Copy { get; }
    internal AliasRow(Browser owner, string name, Catalogue.Hit hit)
    {
        Name = name;
        Torrents = hit.Sources.Where(source => source.File.Name == name).Select(source => source.Hash)
            .Distinct().Count().ToString("N0") + (hit.SourcesLimited ? "+" : string.Empty);
        SearchText = owner.Text.Get("catalogue", "search");
        CopyText = owner.Text.Get("catalogue", "copy");
        Search = new RelayCommand(() => { owner.SearchAlias(name); return Task.CompletedTask; }, () => true);
        Copy = new RelayCommand(() => { owner.CopyAlias(name); return Task.CompletedTask; }, () => true);
    }
}

