using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent;

public sealed class AddDraft : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private readonly Strings _strings;
    private string _destination;
    private string _defaultDestination;
    private bool _paused;
    private bool _neverShow;
    private Exception? _failure;
    private CancellationTokenSource? _polling;
    private readonly FileSelection _emptyFiles;
    private bool _preparing;
    public bool IsSubmitting { get; private set; }
    public ObservableCollection<AddSource> Sources { get; } = [];
    public FileSelection Files => Sources.Count == 1 ? Sources[0].Files : _emptyFiles;
    private string _magnet = string.Empty;
    public string Magnet { get => _magnet; set { _magnet = value; Refresh(); } }
    public bool EditingMagnet { get; internal set; }
    public string Source => string.Join(Environment.NewLine, Sources.Select(source => source.Source));
    public string Destination
    {
        get => _destination;
        set { if (_destination == value) return; _destination = value; Refresh(); }
    }
    public bool Paused { get => _paused; set { _paused = value; Refresh(); } }
    public bool NeverShow { get => _neverShow; set { _neverShow = value; Refresh(); } }
    public bool HasFiles => Sources.Count == 1 && Sources[0].MetadataReady && Sources[0].Duplicate.Length == 0;
    public bool GettingMetadata => Sources.Any(source => !source.MetadataReady && source.Failure is null && source.Duplicate.Length == 0);
    public string Preview => Sources.Count == 1 ? _strings.Format("add", "preview", Sources[0].Name,
        Sources[0].MetadataReady ? _strings.Bytes(Sources[0].Size) : _strings.Get("add", "metadata")) : _strings.Format("add", "sources", Sources.Count);
    public string SubmitText => _strings.Get("add", IsSubmitting ? "pending" : Sources.Count > 1 ? "submit_all" : "submit");
    public string Shared => string.Join(Environment.NewLine, Sources.Select(source => source.Shared).Where(text => text.Length > 0));
    private Exception? Failure => _failure ?? Sources.Select(source => source.Failure).FirstOrDefault(error => error is not null);
    public string Message => Failure is { } failure ? _owner.FormatError(failure) :
        HasFiles && !Files.HasWanted ? _strings.Get("add", "no_files") : string.Empty;
    public bool HasError => Message.Length > 0;
    public InfoBarSeverity Severity => InfoBarSeverity.Error;
    public bool CanEdit => _owner.CanEdit && !_preparing;
    public bool CanSubmit => CanEdit && (Sources.Count > 0 || !string.IsNullOrWhiteSpace(Magnet)) && !string.IsNullOrWhiteSpace(_destination) &&
        Sources.All(source => source.PreviewId is not null || source.Failure is not null || source.Uncertain) &&
        Sources.All(source => !source.MetadataReady || source.SharedDestination == _destination) &&
        Sources.All(source => !source.MetadataReady || source.Duplicate.Length > 0 || source.Files.HasWanted) &&
        (!HasFiles || Files.HasWanted);
    public bool HasChanges => Sources.Count > 0 || Magnet.Length > 0 || _destination != _defaultDestination || _paused || _neverShow;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal AddDraft(MainViewModel owner, PipeClient client, Strings strings)
    {
        _owner = owner;
        _client = client;
        _strings = strings;
        _destination = _defaultDestination = Windows.Storage.UserDataPaths.GetDefault().Downloads;
        _emptyFiles = new FileSelection(strings);
    }

    internal void Own(IEnumerable<string> sources)
    {
        foreach (var source in sources.Where(source => !string.IsNullOrWhiteSpace(source)))
            if (!Sources.Any(existing => existing.Inputs.Contains(source, StringComparer.Ordinal)))
            {
                var addition = new AddSource(source, _strings, this);
                addition.Files.Changed += (_, _) => Refresh();
                Sources.Add(addition);
            }
        Refresh();
    }

    public async Task PrepareMagnet()
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(Magnet)) return;
        Own([Magnet.Trim()]);
        Magnet = string.Empty;
        await PrepareAll();
    }

    internal async Task PrepareAll()
    {
        if (!_owner.CanEdit || _preparing) return;
        _preparing = true;
        Refresh();
        try
        {
            foreach (var source in Sources.Where(source => source.PreviewId is null).ToArray())
                await Acquire(source);
            _failure = null;
        }
        finally { _preparing = false; Refresh(); }
    }

    private async Task Acquire(AddSource source)
    {
        if (!Sources.Contains(source)) return;
        try
        {
            var destination = _destination;
            var preview = await _client.Send("preview", new { source = source.Source, destination });
            foreach (var input in source.Inputs.Skip(1))
                preview = await _client.Send("preview", new { source = input, destination });
            if (!Sources.Contains(source))
            {
                await _client.Send("cancel_preview", new { preview_id = preview.GetProperty("preview_id").GetString() });
                return;
            }
            source.Apply(preview, destination);
            var existing = Sources.FirstOrDefault(existing => existing != source && existing.PreviewId == source.PreviewId);
            if (existing is not null)
            {
                existing.Inputs.AddRange(source.Inputs.Where(input => !existing.Inputs.Contains(input, StringComparer.Ordinal)));
                existing.Apply(preview, destination);
                Sources.Remove(source);
                Refresh();
                return;
            }
        }
        catch (Exception error) { source.Failure = error; }
        source.Refresh();
        Refresh();
    }

    public async Task<bool> Submit()
    {
        if (!string.IsNullOrWhiteSpace(Magnet))
        {
            Own([Magnet.Trim()]);
            Magnet = string.Empty;
        }
        if (!CanEdit || Sources.Count == 0 || string.IsNullOrWhiteSpace(_destination) || (HasFiles && !Files.HasWanted)) return false;
        IsSubmitting = true;
        _owner.Busy(true);
        StopPolling();
        var captured = Sources.ToArray();
        var destination = _destination;
        var paused = _paused;
        try
        {
            if (_neverShow) await _owner.SaveSettings(new { show_add = false });
            foreach (var source in captured)
            {
                if (source.Uncertain && _owner.Find(source.Hashes) is { } existing)
                {
                    Sources.Remove(source);
                    try { await _owner.Reveal(existing.TorrentId); }
                    catch (Exception error) { _owner.Report(error); }
                    continue;
                }
                if (source.PreviewId is null) await Acquire(source);
                if (source.PreviewId is null) continue;
                if (source.Duplicate.Length > 0)
                {
                    if (source.Merge)
                        await _client.Send("merge_trackers", new { preview_id = source.PreviewId, torrent_id = source.Duplicate });
                    await _client.Send("cancel_preview", new { preview_id = source.PreviewId });
                    Sources.Remove(source);
                    try { await _owner.Reveal(source.Duplicate); }
                    catch (Exception error) { _owner.Report(error); }
                    continue;
                }
                var previewId = source.PreviewId;
                source.PreviewId = null;
                JsonElement addition;
                try
                {
                    var priorities = source.MetadataReady ? source.Files.Priorities() : null;
                    addition = priorities is null ?
                        await _client.Send("add", new { preview_id = previewId, destination, paused }) :
                        await _client.Send("add", new { preview_id = previewId, destination, paused, priorities });
                }
                catch (Exception error)
                {
                    source.Failure = error;
                    source.Uncertain = error is not CommandFailure;
                    source.Refresh();
                    continue;
                }
                if (addition.GetProperty("duplicate").GetBoolean())
                {
                    source.PreviewId = previewId;
                    source.Apply(await _client.Send("preview_detail", new { preview_id = previewId, destination }), destination);
                    continue;
                }
                Sources.Remove(source);
                _owner.ClearError();
                try { await _owner.Reveal(addition.GetProperty("torrent_id").GetString()!); }
                catch (Exception error) { _owner.Report(error); }
            }
            if (Sources.Count > 0)
            {
                _failure = Sources.Select(source => source.Failure).FirstOrDefault(error => error is not null);
                if (Message.Length > 0) _owner.Announce(Message);
                return false;
            }
            Clear();
            _owner.Accepted("add", "title");
            return true;
        }
        catch (Exception error) { _failure = error; _owner.Announce(_owner.FormatError(error)); return false; }
        finally
        {
            _owner.Busy(false);
            IsSubmitting = false;
            Refresh();
            if (_owner.IsAddOpen) StartPolling();
        }
    }

    internal void StartPolling()
    {
        StopPolling();
        _polling = new CancellationTokenSource();
        _ = Poll(_polling.Token);
    }

    internal void StopPolling()
    {
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = null;
    }

    private async Task Poll(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (!_owner.IsAddOpen || !_owner.IsConnected || _owner.IsBusy || _owner.IsPicking || _preparing) continue;
                foreach (var source in Sources.ToArray())
                {
                    if (token.IsCancellationRequested) return;
                    if (_owner.IsBusy || _owner.IsPicking) break;
                    if (source.PreviewId is null)
                    {
                        if (!source.Uncertain) await Acquire(source);
                        continue;
                    }
                    if (source.MetadataReady && source.SharedDestination == _destination) continue;
                    try
                    {
                        var destination = _destination;
                        var preview = await _client.Send("preview_detail", new { preview_id = source.PreviewId, destination });
                        if (token.IsCancellationRequested || !Sources.Contains(source)) return;
                        source.Apply(preview, destination);
                    }
                    catch (Exception error) { source.Failure = error; source.Refresh(); }
                }
                Refresh();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _failure = error; Refresh(); }
    }

    public async Task Cancel()
    {
        StopPolling();
        _owner.Busy(true);
        try
        {
            foreach (var source in Sources.ToArray())
                if (source.PreviewId is not null && _owner.IsConnected)
                    await _client.Send("cancel_preview", new { preview_id = source.PreviewId });
            Clear();
        }
        finally { _owner.Busy(false); }
    }

    internal async Task Remove(AddSource source)
    {
        if (!CanEdit) return;
        _owner.Busy(true);
        try
        {
            if (source.PreviewId is not null && _owner.IsConnected)
                await _client.Send("cancel_preview", new { preview_id = source.PreviewId });
            Sources.Remove(source);
            _failure = null;
        }
        catch (Exception error) { _failure = error; }
        finally { _owner.Busy(false); }
    }

    internal void UseDefault(string destination)
    {
        if (Sources.Count > 0 || _destination != _defaultDestination) return;
        _defaultDestination = _destination = destination;
        Refresh();
    }

    internal void Invalidate()
    {
        foreach (var source in Sources) source.PreviewId = null;
        Refresh();
    }

    private void Clear()
    {
        Sources.Clear();
        Files.Clear();
        Magnet = string.Empty;
        EditingMagnet = false;
        _paused = _neverShow = false;
        _failure = null;
        _defaultDestination = _destination;
        Refresh();
    }

    internal void Refresh()
    {
        foreach (var source in Sources) source.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed class AddSource : INotifyPropertyChanged
{
    private readonly Strings _strings;
    private readonly AddDraft _draft;
    internal List<string> Inputs { get; }
    public string Source => Inputs[0];
    public FileSelection Files { get; }
    public string Name { get; private set; }
    public ICommand Remove { get; }
    public ICommand SelectAll { get; }
    public long Size { get; private set; }
    public bool MetadataReady { get; private set; }
    public bool NeedsFiles => MetadataReady && Duplicate.Length == 0 && !Files.HasWanted;
    public string Duplicate { get; private set; } = string.Empty;
    public bool MergeAvailable { get; private set; }
    public bool Merge { get; set; }
    public string[] Hashes { get; private set; } = [];
    public string[] SharedWith { get; private set; } = [];
    internal string SharedDestination { get; private set; } = string.Empty;
    internal string? PreviewId { get; set; }
    internal Exception? Failure { get; set; }
    internal bool Uncertain { get; set; }
    public string Description => Failure is not null ? Failure is CommandFailure ? Failure.Message : _strings.Error("unknown", Failure.Message) :
        Duplicate.Length > 0 ? _strings.Get("add", "already_added") :
        !MetadataReady ? _strings.Get("add", "metadata") : !Files.HasWanted ? _strings.Get("add", "no_files") : _strings.Bytes(Size);
    public string Shared => SharedWith.Length == 0 || SharedDestination != _draft.Destination ? string.Empty :
        _strings.Format("add", "shared", string.Join(", ", SharedWith));
    public string MergeLabel => _strings.Get("add", "merge");
    public string RemoveLabel => _strings.Get("add", "remove_source");
    public string AllLabel => _strings.Get("add", "all_files");
    public event PropertyChangedEventHandler? PropertyChanged;

    internal AddSource(string source, Strings strings, AddDraft draft)
    {
        Inputs = [source];
        Name = source;
        _strings = strings;
        _draft = draft;
        Files = new FileSelection(strings);
        Remove = new Command(() => draft.Remove(this), () => draft.CanEdit);
        SelectAll = new Command(() => { Files.SelectAll(); return Task.CompletedTask; }, () => draft.CanEdit && NeedsFiles);
    }

    internal void Apply(JsonElement preview, string destination)
    {
        var hadMetadata = MetadataReady;
        if (Failure is CommandFailure { Command: "preview" or "preview_detail" } ||
            Failure is not CommandFailure && !Uncertain) Failure = null;
        var error = preview.GetProperty("error").GetString();
        if (!string.IsNullOrEmpty(error) && Failure is null)
            Failure = new CommandFailure("preview_failed", error, _strings, "preview_detail");
        PreviewId = preview.GetProperty("preview_id").GetString();
        Name = preview.GetProperty("name").GetString()!;
        Size = preview.GetProperty("size").GetInt64();
        MetadataReady = preview.GetProperty("metadata_ready").GetBoolean();
        Duplicate = preview.GetProperty("duplicate").GetString() ?? string.Empty;
        MergeAvailable = preview.GetProperty("merge_available").GetBoolean();
        Hashes = preview.GetProperty("hashes").EnumerateArray().Select(hash => hash.GetString()!).ToArray();
        SharedWith = preview.GetProperty("shared_with").EnumerateArray().Select(name => name.GetString()!).ToArray();
        SharedDestination = destination;
        if (!hadMetadata && MetadataReady && Duplicate.Length == 0) Files.Load(preview.GetProperty("files"));
        Refresh();
    }

    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        ((Command)Remove).Refresh();
        ((Command)SelectAll).Refresh();
    }
    public override string ToString() => Name;
}
