using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private JsonElement _settings;
    private string _search = string.Empty;
    private bool _errorsOnly;
    private bool _receivingSources;
    private bool _sourcesPending;
    public SpeedLimits Speed { get; }
    public ObservableCollection<Torrent> VisibleTorrents { get; } = [];
    public string Search { get => _search; set { if (_search == value) return; _search = value; Refresh(); } }
    public bool ErrorsOnly { get => _errorsOnly; set { if (_errorsOnly == value) return; _errorsOnly = value; Refresh(); } }
    public bool AllPaused { get; private set; }
    public bool HasIncoming { get; private set; }
    public string Incoming => Text.Get("window", HasIncoming ? "incoming" : "no_incoming");
    public string SelectionText => Text.Format("window", "selected", _selected.Length);
    public string ErrorCount => Text.Format("window", "errors", Torrents.Count(torrent => torrent.ErrorCode.Length > 0));
    public bool HasSelection => _selected.Length > 0;
    public bool CanReorder => CanEdit && VisibleTorrents.Count(torrent => torrent.Queue >= 0) > 1;
    private bool CanMove => CanEdit && _selected.Length > 0 && _selected.All(torrent => torrent.Queue >= 0);
    public string EmptyTitle => Text.Get("window", Torrents.Count > 0 ? "no_matches" : "empty_title");
    public string EmptyInstruction => Text.Get("window", Torrents.Count > 0 ? "no_matches_detail" : "empty");
    public string EmptyActionText => Text.Get("window", Torrents.Count > 0 ? "clear_filters" : "add");
    public ICommand EmptyAction => Torrents.Count > 0 ? ClearFilters : Add;
    public ICommand ClearFilters { get; }
    public bool ShowAdd => Setting("show_add", true);
    public bool AlternativeLimits
    {
        get => Setting("alternative_limits", false);
        set { if (value != AlternativeLimits) _ = SaveAlternative(value); }
    }
    public Torrent? Inspector { get; private set; }
    public bool HasInspector => Inspector is not null;
    public string InspectorDownloaded => Inspector is null ? string.Empty : Text.Bytes(Inspector.Downloaded);
    public string InspectorRemaining => Inspector is null ? string.Empty : Text.Bytes(Inspector.Remaining);
    public string InspectorFolder => Inspector?.SavePath ?? string.Empty;
    public ICommand AddMagnet { get; }
    public ICommand Force { get; }
    public ICommand Verify { get; }
    public ICommand Remove { get; }
    public ICommand Up { get; }
    public ICommand Down { get; }
    public ICommand Top { get; }
    public ICommand Bottom { get; }
    public ICommand PauseAll { get; }
    public ICommand ResumeAll { get; }
    public ICommand Open { get; }
    public ICommand OpenFolder { get; }
    public ICommand CopyMagnet { get; }
    public ICommand CopyHash { get; }
    public ICommand Properties { get; }
    public ICommand Limits { get; }
    public event EventHandler? LimitsRequested;
    public event EventHandler? FilesRequested;
    public event EventHandler<Torrent[]>? RemoveRequested;
    public event EventHandler<string>? OpenRequested;
    public event EventHandler<string>? CopyRequested;

    private bool Setting(string name, bool fallback) => _settings.ValueKind == JsonValueKind.Object &&
        _settings.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;
    public double Limit(string name) => _settings.ValueKind == JsonValueKind.Object &&
        _settings.TryGetProperty(name, out var value) ? value.GetInt32() / 1024.0 : 0;

    private void Project()
    {
        var desired = Torrents.Where(torrent => torrent.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) &&
            (!_errorsOnly || torrent.ErrorCode.Length > 0)).OrderBy(torrent => torrent.QueueOrder).ToArray();
        foreach (var torrent in VisibleTorrents.Where(torrent => !desired.Contains(torrent)).ToArray()) VisibleTorrents.Remove(torrent);
        foreach (var torrent in desired)
            if (!VisibleTorrents.Contains(torrent)) VisibleTorrents.Add(torrent);
        for (var index = 0; index < desired.Length; index++)
        {
            var current = VisibleTorrents.IndexOf(desired[index]);
            if (current != index) VisibleTorrents.Move(current, index);
        }
        if (Inspector is not null && !Torrents.Contains(Inspector)) CloseInspector();
    }

    internal Torrent? Find(IEnumerable<string> hashes) => Torrents.FirstOrDefault(torrent =>
        torrent.Hashes.Intersect(hashes, StringComparer.OrdinalIgnoreCase).Any());

    public Task AddSources(IEnumerable<string> sources)
    {
        Draft.Own(sources);
        return AddOwnedSources();
    }

    private async Task AddOwnedSources()
    {
        if (Draft.Sources.Count == 0) return;
        if (ShowAdd || IsAddOpen)
        {
            AddRequested?.Invoke(this, EventArgs.Empty);
            await Draft.PrepareAll();
        }
        else
        {
            await Draft.PrepareAll();
            if (!Draft.Sources.Any(source => source.MergeAvailable)) await Draft.Submit();
            if (Draft.Sources.Count > 0) AddRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task ReceiveSources()
    {
        _sourcesPending = true;
        if (_receivingSources || !_ready || !_connected) return;
        _receivingSources = true;
        try
        {
            while (_ready && _connected)
            {
                _sourcesPending = false;
                var pending = await _client.Send("pending_sources");
                var activations = pending.GetProperty("activations").EnumerateArray().ToArray();
                foreach (var activation in activations)
                    Draft.Own(activation.GetProperty("sources").EnumerateArray().Select(source => source.GetString()!));
                if (activations.Length == 0)
                {
                    if (_sourcesPending) continue;
                    return;
                }
                await _client.Send("sources_received", new { activation_ids = activations.Select(activation => activation.GetProperty("activation_id").GetString()).ToArray() });
                await AddOwnedSources();
            }
        }
        catch (Exception error) { Report(error); }
        finally { _receivingSources = false; }
    }

    internal async Task SaveSettings(object changes)
    {
        await _client.Send("settings", new { changes });
        await _client.Send("snapshot");
    }

    private async Task SaveAlternative(bool enabled)
    {
        if (!CanEdit) return;
        Busy(true);
        try { await SaveSettings(new { alternative_limits = enabled }); Accepted("window", "alternative"); ClearError(); }
        catch (Exception error) { Report(error); }
        finally { Busy(false); }
    }

    public async Task SaveLimits(IReadOnlyDictionary<string, double> values)
    {
        if (!CanEdit) throw new InvalidOperationException(Message.Length > 0 ? Message : Text.Get("connection", "unavailable"));
        var changes = new Dictionary<string, int>();
        foreach (var (name, value) in values)
        {
            if (!double.IsFinite(value) || value < 0 || value > int.MaxValue / 1024.0)
                throw new CommandFailure("invalid_limits", null, Text);
            changes[name] = checked((int)Math.Round(value * 1024));
        }
        Busy(true);
        try { await SaveSettings(changes); Accepted("commands", "limits"); ClearError(); }
        finally { Busy(false); }
    }

    private Task Queue(string direction) => Queue(new { torrent_ids = _selected.Select(torrent => torrent.TorrentId).ToArray(), direction });

    public Task Reorder(IEnumerable<Torrent> torrents, Torrent? before) => Queue(new
    {
        torrent_ids = torrents.Select(torrent => torrent.TorrentId).ToArray(), before_torrent_id = before?.TorrentId
    });

    private async Task Queue(object arguments)
    {
        if (!CanEdit) return;
        Busy(true);
        try
        {
            await _client.Send("queue", arguments);
            Accepted("outcomes", "queue");
            ClearError();
            await _client.Send("snapshot");
        }
        catch (Exception error) { Report(error); }
        finally { Busy(false); }
    }

    private async Task SessionPause(bool paused)
    {
        if (!CanEdit) return;
        Busy(true);
        try { await _client.Send("session_pause", new { paused }); Accepted("commands", paused ? "pause_all" : "resume_all"); ClearError(); await _client.Send("snapshot"); }
        catch (Exception error) { Report(error); }
        finally { Busy(false); }
    }

    public async Task RemoveTorrents(IEnumerable<Torrent> torrents)
    {
        if (!CanEdit) return;
        var identities = torrents.Select(torrent => torrent.TorrentId).ToArray();
        Busy(true);
        try { await _client.Send("remove", new { torrent_ids = identities }); Accepted("commands", "remove"); ClearError(); await _client.Send("snapshot"); }
        catch (Exception error) { Report(error); }
        finally { Busy(false); }
    }

    private async Task<JsonElement> Detail(Torrent torrent) =>
        await _client.Send("torrent", new { torrent_id = torrent.TorrentId });

    private async Task OpenTorrent(bool folder)
    {
        if (_selected.Length != 1) return;
        var torrent = _selected[0];
        try
        {
            var detail = await Detail(torrent);
            var destination = detail.GetProperty("save_path").GetString()!;
            var files = detail.GetProperty("files").EnumerateArray().Where(file => !file.GetProperty("padding").GetBoolean()).ToArray();
            var path = !folder && files.Length == 1 ? Path.Combine(destination, files[0].GetProperty("path").GetString()!) :
                detail.GetProperty("folder").GetString()!;
            OpenRequested?.Invoke(this, path);
        }
        catch (Exception error) { Report(error); }
    }

    private async Task CopyTorrent(bool hashes)
    {
        if (_selected.Length != 1) return;
        try
        {
            var detail = await Detail(_selected[0]);
            CopyRequested?.Invoke(this, hashes ? string.Join(Environment.NewLine,
                detail.GetProperty("hashes").EnumerateArray().Select(hash => hash.GetString())) : detail.GetProperty("magnet").GetString()!);
        }
        catch (Exception error) { Report(error); }
    }

    private Task ShowInspector()
    {
        if (_selected.Length != 1) return Task.CompletedTask;
        Inspector = _selected[0];
        Refresh();
        return Task.CompletedTask;
    }

    public void CloseInspector() { Inspector = null; Changed(string.Empty); }
}
