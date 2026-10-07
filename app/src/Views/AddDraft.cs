using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed class AddDraft : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private readonly Strings _strings;
    private string _destination;
    private string _defaultDestination;
    private bool _paused;
    private bool _queueTop;
    private bool _sequential;
    private bool _firstLast;
    private bool _neverShow;
    private Exception? _failure;
    private CancellationTokenSource? _polling;
    private Task _poller = Task.CompletedTask;
    private Task _pass = Task.CompletedTask;
    private readonly FileSelection _emptyFiles;
    private bool _preparing;
    public bool IsSubmitting { get; private set; }
    internal bool IsPending => _preparing || IsSubmitting;
    public ObservableCollection<AddSource> Sources { get; } = [];
    public bool HasSources => Sources.Count > 0;
    public FileSelection Files => Sources.Count == 1 ? Sources[0].Files : _emptyFiles;
    private string _magnet = string.Empty;
    private Exception? _magnetFailure;
    public string Magnet { get => _magnet; set { _magnet = value; _magnetFailure = null; Refresh(); } }
    public bool HasMagnetError => _magnetFailure is not null;
    public string MagnetMessage => _magnetFailure is null ? string.Empty : _strings.Error(_magnetFailure);
    public bool EditingMagnet { get; internal set; }
    public string Source => string.Join(Environment.NewLine, Sources.Select(source => source.Source));
    public string Destination
    {
        get => _destination;
        set
        {
            if (_destination == value) return;
            _destination = value;
            if (IsDestinationError(_failure)) _failure = null;
            foreach (var source in Sources)
                if (IsDestinationError(source.Failure)) source.Failure = null;
            Refresh();
        }
    }
    public bool Paused { get => _paused; set { _paused = value; Refresh(); } }
    public bool QueueTop { get => _queueTop; set { _queueTop = value; Refresh(); } }
    public bool Sequential { get => _sequential; set { _sequential = value; Refresh(); } }
    public bool FirstLast { get => _firstLast; set { _firstLast = value; Refresh(); } }
    public bool NeverShow { get => _neverShow; set { _neverShow = value; Refresh(); } }
    public bool HasFiles => Sources.Count == 1 && Sources[0].MetadataReady && Sources[0].Duplicate.Length == 0;
    public bool GettingMetadata => Sources.Any(source => !source.MetadataReady && source.Failure is null && source.Duplicate.Length == 0);
    public string Heading => Sources.Count switch
    {
        0 => _strings.Get("add", "title"),
        1 => Sources[0].Name,
        _ => _strings.Format("add", "sources", Sources.Count)
    };
    public string Detail => Sources.Count != 1 ? string.Empty : Sources[0].MetadataReady ? _strings.Bytes(Sources[0].Size) : _strings.Get("add", "metadata");
    public string SubmitText => _strings.Get("add", IsSubmitting ? "pending" : "submit");
    public string SubmitToolTip => _strings.Format("add", (SubmitCount > 1, _paused) switch
    {
        (true, true) => "submit_tip_all_paused",
        (true, false) => "submit_tip_all",
        (false, true) => "submit_tip_paused",
        (false, false) => "submit_tip"
    }, SubmitCount);
    // Magnet text not yet accepted becomes one more source when Add runs.
    private int SubmitCount => Sources.Count + (Magnet.Trim().Length > 0 ? 1 : 0);
    public string Space => FreeSpace() is not { } free ? string.Empty : Needed > free ?
        _strings.Format("add", "short", _strings.Bytes(Needed), _strings.Bytes(free)) : _strings.Format("add", "free", _strings.Bytes(free));
    public bool LacksSpace => FreeSpace() is { } free && Needed > free;
    private long Needed => Sources.Where(source => source.MetadataReady && source.Duplicate.Length == 0).Sum(source => source.Files.WantedBytes);
    public string[] Folders => _owner.Torrents.OrderByDescending(torrent => torrent.Added).Select(torrent => torrent.SavePath)
        .Prepend(_defaultDestination).Select(folder => Path.TrimEndingDirectorySeparator(folder.Trim()))
        .Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
    public string Shared => string.Join(Environment.NewLine, Sources.Select(source => source.Shared).Where(text => text.Length > 0));
    private IEnumerable<Exception> Failures => Sources.Select(source => source.Failure).Prepend(_failure).OfType<Exception>();
    internal Exception? Failure => Failures.FirstOrDefault();
    internal static bool IsDestinationError(Exception? error) => error is CommandFailure { Code: "invalid_destination" };
    public bool HasDestinationError => Failures.Any(IsDestinationError);
    public string DestinationMessage => Failures.FirstOrDefault(IsDestinationError) is { } failure ? _strings.Error(failure) : string.Empty;
    public string Message => !_owner.IsConnected ? _owner.Message : Failures.FirstOrDefault(error => !IsDestinationError(error)) is { } failure ? _strings.Error(failure) :
        HasFiles && !Files.HasWanted ? _strings.Get("add", "no_files") : string.Empty;
    public bool HasError => Message.Length > 0;
    public InfoBarSeverity Severity => InfoBarSeverity.Error;
    private bool CanSave => _owner.CanSave && !IsPending;
    public bool CanEdit => CanSave && !_owner.IsClosing;
    public bool CanSubmit => CanEdit && !HasMagnetError && (Sources.Count > 0 || !string.IsNullOrWhiteSpace(Magnet)) && !string.IsNullOrWhiteSpace(_destination) &&
        Sources.All(source => source.PreviewId is not null || source.Failure is not null || source.Uncertain) &&
        Sources.All(source => !source.MetadataReady || source.SharedDestination == _destination) &&
        Sources.All(source => !source.MetadataReady || source.Duplicate.Length > 0 || source.Files.HasWanted) &&
        (!HasFiles || Files.HasWanted);
    public bool HasChanges => Sources.Count > 0 || !string.IsNullOrWhiteSpace(Magnet);
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
                Own(new AddSource(source, _strings, this));
        Refresh();
    }

    private void Own(AddSource source)
    {
        source.Files.Changed += (_, _) => Refresh();
        Sources.Add(source);
    }

    public async Task PrepareMagnet()
    {
        if (!CanEdit || string.IsNullOrWhiteSpace(Magnet)) return;
        if (!await AcceptMagnet()) return;
        await PrepareAll();
    }

    private async Task<bool> AcceptMagnet()
    {
        var input = Magnet;
        var value = input.Trim();
        if (value.Length == 0) return true;
        _preparing = true;
        _magnetFailure = null;
        Refresh();
        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var address) || address.Scheme != "magnet")
                throw new CommandFailure("invalid_source", null, _strings, "preview");
            var source = new AddSource(value, _strings, this);
            var destination = _destination;
            var preview = await PreviewSource(source, destination);
            if (Magnet != input || !_owner.IsAddOpen && !_owner.IsClosing)
            {
                await Release(preview.GetProperty("preview_id").GetString()!);
                return false;
            }
            ApplyPreview(source, preview, destination);
            Magnet = string.Empty;
            return true;
        }
        catch (Exception error)
        {
            if (Magnet == input)
            {
                _magnetFailure = error;
                _owner.Announce(MagnetMessage);
            }
            return false;
        }
        finally { _preparing = false; Refresh(); }
    }

    internal async Task PrepareAll()
    {
        if (!CanEdit) return;
        _preparing = true;
        Refresh();
        try
        {
            await _pass;
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
            var preview = await PreviewSource(source, destination);
            if (!Sources.Contains(source))
            {
                await Release(preview.GetProperty("preview_id").GetString()!);
                return;
            }
            ApplyPreview(source, preview, destination);
        }
        catch (Exception error) { source.Failure = error; }
        source.Refresh();
        Refresh();
    }

    private async Task<JsonElement> PreviewSource(AddSource source, string destination)
    {
        var preview = await _client.Send("preview", new { source = source.Source, destination });
        foreach (var input in source.Inputs.Skip(1))
            preview = await _client.Send("preview", new { source = input, destination });
        return preview;
    }

    private void ApplyPreview(AddSource source, JsonElement preview, string destination)
    {
        source.Apply(preview, destination);
        var existing = Sources.FirstOrDefault(existing => existing != source && existing.PreviewId == source.PreviewId);
        if (existing is not null)
        {
            existing.Inputs.AddRange(source.Inputs.Where(input => !existing.Inputs.Contains(input, StringComparer.Ordinal)));
            existing.Apply(preview, destination);
            Sources.Remove(source);
        }
        else if (!Sources.Contains(source)) Own(source);
    }

    public async Task<bool> Submit()
    {
        if (!CanSave || !await AcceptMagnet()) return false;
        if (!CanSave || Sources.Count == 0 || string.IsNullOrWhiteSpace(_destination) || (HasFiles && !Files.HasWanted)) return false;
        IsSubmitting = true;
        Refresh();
        var destination = _destination;
        var paused = _paused;
        var queueTop = _queueTop;
        var sequential = _sequential;
        var firstLast = _firstLast;
        try
        {
            await _pass;
            var captured = Sources.ToArray();
            // Prepending in reverse preserves the order shown in the form.
            if (queueTop) Array.Reverse(captured);
            var revealed = new List<string>();
            if (_neverShow) await _owner.Preferences.HideAddForm();
            foreach (var source in captured)
            {
                if (source.Uncertain && _owner.Find(source.Hashes) is { } existing)
                {
                    Sources.Remove(source);
                    revealed.Add(existing.TorrentId);
                    continue;
                }
                if (source.PreviewId is null) await Acquire(source);
                if (source.PreviewId is null) continue;
                if (source.Duplicate.Length > 0)
                {
                    if (source.Merge)
                        await _client.Send("merge_trackers", new { preview_id = source.PreviewId, torrent_id = source.Duplicate });
                    Sources.Remove(source);
                    await Release(source.PreviewId);
                    revealed.Add(source.Duplicate);
                    continue;
                }
                var previewId = source.PreviewId;
                source.PreviewId = null;
                JsonElement addition;
                try
                {
                    var priorities = source.MetadataReady ? source.Files.Priorities() : [];
                    addition = await _client.Send("add", new
                    {
                        preview_id = previewId, destination, paused, queue_top = queueTop,
                        sequential, first_last = firstLast, priorities
                    });
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
                revealed.Add(addition.GetProperty("torrent_id").GetString()!);
            }
            _owner.Reveal([.. revealed]);
            if (Sources.Count > 0)
            {
                _failure = Sources.Select(source => source.Failure).FirstOrDefault(error => error is not null);
                var message = Message.Length > 0 ? Message : DestinationMessage;
                if (message.Length > 0) _owner.Announce(message);
                return false;
            }
            Clear();
            _owner.Accepted("add", "title");
            return true;
        }
        catch (Exception error) { _failure = error; _owner.Announce(_strings.Error(error)); return false; }
        finally
        {
            IsSubmitting = false;
            Refresh();
        }
    }

    internal void StartPolling()
    {
        if (_polling is not null) return;
        _polling = new CancellationTokenSource();
        _poller = Poll(_poller, _polling);
    }

    internal void StopPolling()
    {
        _polling?.Cancel();
        _polling = null;
        _client.Withdraw(Consumer.Draft);
    }

    // Starts after the previous poller ends, so two never overlap. Each pass
    // stops once other draft work begins, and that work awaits the pass.
    private async Task Poll(Task previous, CancellationTokenSource stop)
    {
        using var _ = stop;
        await previous;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                if (!CanEdit) continue;
                _pass = Pass(stop.Token);
                await _pass;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _failure = error; Refresh(); }
    }

    private async Task Pass(CancellationToken token)
    {
        foreach (var source in Sources.ToArray())
        {
            if (token.IsCancellationRequested || !CanEdit) return;
            if (source.PreviewId is null)
            {
                if (!source.Uncertain) await Acquire(source);
                continue;
            }
            if (source.MetadataReady && source.SharedDestination == _destination) continue;
            try
            {
                var destination = _destination;
                var preview = await _client.Read(Consumer.Draft, "preview_detail", new { preview_id = source.PreviewId, destination });
                if (token.IsCancellationRequested) return;
                if (Sources.Contains(source)) source.Apply(preview, destination);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) { source.Failure = error; source.Refresh(); }
        }
        Refresh();
    }

    public async Task Cancel()
    {
        var cancelled = Sources.ToArray();
        Clear();
        foreach (var source in cancelled)
            if (source.PreviewId is { } previewId) await Release(previewId);
    }

    // The engine reuses a preview for content the connection already
    // previews, so a preview stays open while any source still uses it. An
    // acquisition that finishes after its source left releases its preview here.
    private async Task Release(string previewId)
    {
        if (!_owner.IsConnected || Sources.Any(source => source.PreviewId == previewId)) return;
        await _client.Send("cancel_preview", new { preview_id = previewId });
    }

    internal void UseDefault(string destination)
    {
        if (Sources.Count > 0 || _destination != _defaultDestination || _destination == destination) return;
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
        _paused = _queueTop = _sequential = _firstLast = _neverShow = false;
        _failure = null;
        _defaultDestination = _destination;
        Refresh();
    }

    internal void Refresh()
    {
        foreach (var source in Sources) source.Refresh();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private long? FreeSpace()
    {
        try
        {
            if (!Path.IsPathFullyQualified(_destination)) return null;
            var drive = new DriveInfo(_destination);
            // A disconnected network drive can block the UI thread for seconds.
            if (drive.DriveType == DriveType.Network || !drive.IsReady) return null;
            return drive.AvailableFreeSpace;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
    public string Description => Failure is not null && !AddDraft.IsDestinationError(Failure) ? _strings.Error(Failure) :
        Duplicate.Length > 0 ? _strings.Get("add", "already_added") :
        !MetadataReady ? _strings.Get("add", "metadata") : !Files.HasWanted ? _strings.Get("add", "no_files") : _strings.Bytes(Size);
    // An already added source adds no files, so it shares none.
    public string Shared => Duplicate.Length > 0 || SharedWith.Length == 0 || SharedDestination != _draft.Destination ? string.Empty :
        _strings.Format("add", "shared", string.Join(", ", SharedWith));
    public string MergeLabel => _strings.Get("add", "merge_trackers");
    public string AllLabel => _strings.Get("add", "all_files");
    public string AllToolTip => _strings.Get("add", "all_files_tip");
    public event PropertyChangedEventHandler? PropertyChanged;

    internal AddSource(string source, Strings strings, AddDraft draft)
    {
        Inputs = [source];
        Name = source;
        _strings = strings;
        _draft = draft;
        Files = new FileSelection(strings);
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
        ((Command)SelectAll).Refresh();
    }
    public override string ToString() => Name;
}
