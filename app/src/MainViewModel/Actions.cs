using System.Text.Json;
using System.Windows.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    private bool _receivingSources;
    private bool _sourcesPending;
    public bool AllPaused { get; private set; }
    public bool HasIncoming { get; private set; }
    public string MissingInterface { get; private set; } = string.Empty;
    private bool _alternativeLimits;
    public string Incoming => MissingInterface.Length > 0 ? Text.Format("preferences", "unavailable_interface", MissingInterface) :
        Text.Get("window", HasIncoming ? "incoming" : "no_incoming");
    public string SelectionText => Text.Format("window", "selected", _selected.Length);
    public string ErrorCount => Text.Format("window", "errors", Torrents.Count(torrent => torrent.IsError));
    public bool HasSelection => _selected.Length > 0;
    public bool CanReorder => CanEdit && VisibleTorrents.Count(torrent => torrent.Queue >= 0) > 1;
    private bool CanMove => CanEdit && _selected.Length > 0 && _selected.All(torrent => torrent.Queue >= 0);
    public string EmptyTitle => Text.Get("window", Torrents.Count > 0 ? "no_matches" : "empty_title");
    public string EmptyInstruction => Text.Get("window", Torrents.Count > 0 ? "no_matches_detail" : "empty");
    public string EmptyActionText => Text.Get("window", Torrents.Count > 0 ? "clear_filters" : "add");
    public string EmptyActionGlyph => Torrents.Count > 0 ? Syno.Lucide.FunnelX : Syno.Lucide.FilePlus;
    public string EmptyActionToolTip => Text.Get("window", Torrents.Count > 0 ? "clear_filters_tip" : "add_tip");
    public ICommand EmptyAction => Torrents.Count > 0 ? ClearFilters : Add;
    public ICommand ClearFilters { get; }
    public bool ShowAdd => Preferences.ShowAdd.ConfirmedOn;
    public bool AlternativeLimits
    {
        get => _alternativeLimits;
        set { if (value != AlternativeLimits) _ = SaveAlternative(value); }
    }
    public bool CanEditSelection => CanEdit && _selected.Length > 0;
    // Null when the selected torrents differ. Switching a mixed selection
    // turns the choice on for all of them.
    public bool? Sequential => Shared(torrent => torrent.Sequential);
    public bool? FirstLast => Shared(torrent => torrent.FirstLast);
    public ICommand SwitchSequential { get; }
    public ICommand SwitchFirstLast { get; }
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
    public event EventHandler? MergeRequested;
    public event EventHandler<Torrent[]>? MoveRequested;
    public event EventHandler<Torrent[]>? DeleteRequested;
    public event EventHandler<OpenRequestedEventArgs>? OpenRequested;
    public event EventHandler<string>? CopyRequested;

    internal Torrent? Find(IEnumerable<string> hashes) => Torrents.FirstOrDefault(torrent =>
        torrent.Hashes.Intersect(hashes, StringComparer.OrdinalIgnoreCase).Any());

    // Dropped or pasted sources.
    public Task AddSources(IEnumerable<string> sources)
    {
        Draft.Own(sources);
        return AddArrived();
    }

    // Picked sources: choosing Add torrent file asks for the form, however
    // many files the person picks.
    public Task AddPicked(IEnumerable<string> sources)
    {
        Draft.Own(sources);
        return AddOwnedSources();
    }

    private Task AddArrived() => !IsAddOpen && Draft.Sources.Count > 1 ? AddTogether() : AddOwnedSources();

    // Several sources arriving at once are added without the form or any
    // question, so one drop never opens a cascade of dialogs. A torrent already
    // in the list is shown where it is, and a source that fails is reported and
    // dropped rather than left for a later form. Sources that could not be
    // tried stay in the draft, so none is lost silently.
    private async Task AddTogether()
    {
        await Draft.PrepareAll();
        if (await Draft.Submit() || Draft.Failure is not { } failure) return;
        Report(failure);
        await Draft.Cancel();
    }

    private async Task AddOwnedSources()
    {
        if (Draft.Sources.Count == 0) return;
        if (IsAddOpen)
        {
            await Draft.PrepareAll();
            return;
        }
        await Draft.PrepareAll();
        // A torrent already in the list needs no form: Submit shows it where
        // it is, and only trackers it lacks are worth a question.
        if (Draft.Sources.All(source => source.Duplicate.Length > 0))
        {
            if (Draft.Sources.Any(source => source.MergeAvailable)) MergeRequested?.Invoke(this, EventArgs.Empty);
            else if (await Draft.Submit()) Announce(Text.Get("errors", "duplicate"));
            return;
        }
        if (ShowAdd)
        {
            AddRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!Draft.Sources.Any(source => source.MergeAvailable)) await Draft.Submit();
        if (Draft.Sources.Count > 0) AddRequested?.Invoke(this, EventArgs.Empty);
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
                await AddArrived();
            }
        }
        catch (Exception error) { Report(error); }
        finally { _receivingSources = false; Refresh(); }
    }

    private async Task SaveAlternative(bool enabled)
    {
        if (!CanEdit) return;
        try { await Preferences.SetAlternative(enabled); Accepted("window", "alternative"); ClearError(); }
        catch (Exception error) { Report(error); }
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

    private bool? Shared(Func<Torrent, bool> choice) =>
        _selected.All(choice) ? _selected.Length > 0 : _selected.Any(choice) ? null : false;

    private static string PieceOrderKey(PieceOrder order, bool enabled) => (order, enabled) switch
    {
        (PieceOrder.Sequential, true) => "sequential",
        (PieceOrder.Sequential, false) => "sequential_off",
        (PieceOrder.FirstLast, true) => "first_last",
        (PieceOrder.FirstLast, false) => "first_last_off",
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };

    private async Task SetPieceOrder(PieceOrder order, bool enabled)
    {
        if (!CanEditSelection) return;
        var arguments = new Dictionary<string, object>
        {
            ["torrent_ids"] = _selected.Select(torrent => torrent.TorrentId).ToArray(),
            [order == PieceOrder.Sequential ? "sequential" : "first_last"] = enabled
        };
        try
        {
            await _client.Send("piece_order", arguments);
            Accepted("commands", PieceOrderKey(order, enabled));
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

    private Task OpenTorrent(bool folder) => _selected.Length == 1 ? OpenTorrent(_selected[0], folder) : Task.CompletedTask;

    private async Task OpenTorrent(Torrent torrent, bool folder)
    {
        try
        {
            var detail = await Detail(torrent);
            var destination = detail.GetProperty("save_path").GetString()!;
            var files = detail.GetProperty("files").EnumerateArray().Where(file => !file.GetProperty("padding").GetBoolean()).ToArray();
            var path = detail.GetProperty("folder").GetString()!;
            string? extension = null;
            if (!folder && files.Length == 1)
            {
                var logical = files[0].GetProperty("path").GetString()!;
                var physical = files[0].TryGetProperty("disk_path", out var disk) ? disk.GetString()! : logical;
                path = Path.Combine(destination, physical);
                if (logical != physical) extension = Path.GetExtension(logical);
            }
            OpenRequested?.Invoke(this, new(path, extension));
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

    public bool CloseInspector()
    {
        if (!Inspector.Close()) return false;
        Changed(nameof(HasInspector));
        return true;
    }
}

public sealed class OpenRequestedEventArgs(string path, string? extension = null) : EventArgs
{
    public string Path { get; } = path;
    public string? Extension { get; } = extension;
}
