using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private JsonElement _settings;
    private bool _receivingSources;
    private bool _sourcesPending;
    public bool AllPaused { get; private set; }
    public bool HasIncoming { get; private set; }
    public string MissingInterface { get; private set; } = string.Empty;
    private bool _alternativeLimits;
    public string Incoming => MissingInterface.Length > 0 ? Text.Format("preferences", "unavailable_interface", MissingInterface) :
        Text.Get("window", HasIncoming ? "incoming" : "no_incoming");
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
        get => _alternativeLimits;
        set { if (value != AlternativeLimits) _ = SaveAlternative(value); }
    }
    public Inspector Inspector { get; }
    public bool HasInspector => Inspector.IsOpen;
    public ICommand AddMagnet { get; }
    public ICommand Force { get; }
    public ICommand Verify { get; }
    public ICommand Remove { get; }
    public ICommand MoveFiles { get; }
    public ICommand DeleteFiles { get; }
    public FileOperation Files { get; }
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
    public event EventHandler? FilesRequested;
    public event EventHandler<Torrent[]>? RemoveRequested;
    public event EventHandler<Torrent[]>? MoveRequested;
    public event EventHandler<Torrent[]>? DeleteRequested;
    public event EventHandler<string>? OpenRequested;
    public event EventHandler<string>? CopyRequested;

    private bool Setting(string name, bool fallback) => _settings.ValueKind == JsonValueKind.Object &&
        _settings.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;

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

    // The engine keeps offered sources until sources_received, so sources the
    // window has not taken when closing begins stay with the engine.
    internal async Task ReceiveSources()
    {
        _sourcesPending = true;
        if (_receivingSources || !_ready || !_connected || _closing) return;
        _receivingSources = true;
        try
        {
            while (_sourcesPending && _ready && _connected && !_closing)
            {
                _sourcesPending = false;
                var pending = await _client.Send("pending_sources");
                if (_closing)
                {
                    _sourcesPending = true;
                    return;
                }
                var activations = pending.GetProperty("activations").EnumerateArray().ToArray();
                if (activations.Length == 0) continue;
                foreach (var activation in activations)
                    Draft.Own(activation.GetProperty("sources").EnumerateArray().Select(source => source.GetString()!));
                await _client.Send("sources_received", new { activation_ids = activations.Select(activation => activation.GetProperty("activation_id").GetString()).ToArray() });
                await AddOwnedSources();
            }
        }
        catch (Exception error) { Report(error); }
        finally { _receivingSources = false; Refresh(); }
    }

    internal async Task SaveSettings(object changes)
    {
        await _client.Send("settings", new { changes });
        RequestSnapshot();
    }

    private async Task SaveAlternative(bool enabled)
    {
        if (!CanEdit) return;
        try { await SaveSettings(new { alternative_limits = enabled }); Accepted("window", "alternative"); ClearError(); }
        catch (Exception error) { Report(error); }
    }

    internal static bool IsValidLimit(double value) => double.IsFinite(value) && value >= 0 && value <= int.MaxValue / 1024.0;

    public async Task<IReadOnlyDictionary<string, int>> SaveLimits(IReadOnlyDictionary<string, double> values)
    {
        if (!CanSave) throw new InvalidOperationException(Message.Length > 0 ? Message : Text.Get("connection", "unavailable"));
        var changes = new Dictionary<string, int>();
        foreach (var (name, value) in values)
        {
            if (!IsValidLimit(value))
                throw new CommandFailure("invalid_limits", null, Text);
            changes[name] = checked((int)Math.Round(value * 1024));
        }
        await SaveSettings(changes);
        Accepted("commands", "limits");
        ClearError();
        return changes;
    }

    private Task Queue(string direction) => Queue(new { torrent_ids = _selected.Select(torrent => torrent.TorrentId).ToArray(), direction });

    public Task Reorder(IEnumerable<Torrent> torrents, Torrent? before) => Queue(new
    {
        torrent_ids = torrents.Select(torrent => torrent.TorrentId).ToArray(), before_torrent_id = before?.TorrentId
    });

    private async Task Queue(object arguments)
    {
        if (!CanEdit) return;
        try
        {
            await _client.Send("queue", arguments);
            Accepted("outcomes", "queue");
            ClearError();
            RequestSnapshot();
        }
        catch (Exception error) { Report(error); }
    }

    private async Task SessionPause(bool paused)
    {
        if (!CanEdit) return;
        try { await _client.Send("session_pause", new { paused }); Accepted("commands", paused ? "pause_all" : "resume_all"); ClearError(); RequestSnapshot(); }
        catch (Exception error) { Report(error); }
    }

    public async Task RemoveTorrents(IEnumerable<Torrent> torrents)
    {
        if (!CanEdit) return;
        var identities = torrents.Select(torrent => torrent.TorrentId).ToArray();
        try { await _client.Send("remove", new { torrent_ids = identities }); Accepted("commands", "remove"); ClearError(); RequestSnapshot(); }
        catch (Exception error) { Report(error); }
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

    private Task Inspect(InspectorSection section)
    {
        if (_selected.Length != 1) return Task.CompletedTask;
        if (Inspector.Open(_selected[0])) Inspector.Select(section);
        Refresh();
        return Task.CompletedTask;
    }

    public void CloseInspector() { Inspector.Close(); Changed(nameof(HasInspector)); }
}
