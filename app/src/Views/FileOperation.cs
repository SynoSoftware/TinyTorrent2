using System.ComponentModel;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed class FileOperation : INotifyPropertyChanged
{
    private readonly MainViewModel owner;
    private readonly PipeClient client;
    private string[] _identities = [];
    private FileLocation[] _torrents = [];
    private FileLocation[] _shared = [];
    private Exception? _failure;
    private bool _loaded;
    private string _destination = string.Empty;
    private string _originalDestination = string.Empty;
    private bool _includeShared;
    private bool _useExisting;
    private int _keptFiles;
    public FileAction Action { get; private set; }
    public bool IsPending { get; private set; }
    public bool HasDraft => Action == FileAction.Move && (Destination != _originalDestination || IncludeShared || UseExisting);
    public bool CanSubmit => owner.CanEdit && !IsPending && _loaded &&
        (Action == FileAction.Delete || HasDestination && (!HasShared || IncludeShared));
    public bool CanRefresh => owner.CanEdit && !IsPending;
    public bool IsMove => Action == FileAction.Move;
    public bool HasShared => _shared.Length > 0;
    public bool HasDestination => Destination.Length > 0;
    public bool HasKeptFiles => !IsMove && _keptFiles > 0;
    public string Title => owner.Text.Get("commands", IsMove ? "move" : "delete_files");
    public string Instruction => owner.Text.Get("file_operation", IsMove ? "move_detail" : "delete_detail");
    public string Locations => string.Join(Environment.NewLine + Environment.NewLine,
        _torrents.Concat(IncludeShared ? _shared : []).Select(torrent => torrent.Name + Environment.NewLine + torrent.Folder));
    public string ResultingFolders => !HasDestination ? string.Empty : string.Join(Environment.NewLine,
        _torrents.Concat(IncludeShared ? _shared : []).Select(torrent =>
        {
            var relative = Path.GetRelativePath(torrent.SavePath, torrent.Folder);
            return relative == "." ? Destination : Path.Combine(Destination, relative);
        }).Distinct(StringComparer.OrdinalIgnoreCase));
    public string SharedText => owner.Text.Format("file_operation", IsMove ? "move_shared" : "delete_shared",
        string.Join(Environment.NewLine, _shared.Select(torrent => torrent.Name)));
    public string KeptText => HasKeptFiles ? owner.Text.FormatCount("file_operation", "kept", _keptFiles) : string.Empty;
    public string Message => _failure is null ? string.Empty : owner.FormatError(_failure);
    public bool HasError => _failure is not null;
    public string Destination
    {
        get => _destination;
        set { if (_destination == value) return; _destination = value; Refresh(); }
    }
    public bool IncludeShared
    {
        get => _includeShared;
        set { if (_includeShared == value) return; _includeShared = value; Refresh(); }
    }
    public bool UseExisting
    {
        get => _useExisting;
        set { if (_useExisting == value) return; _useExisting = value; Refresh(); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal FileOperation(MainViewModel owner, PipeClient client)
    {
        this.owner = owner;
        this.client = client;
    }

    internal void Begin(Torrent[] torrents, FileAction action)
    {
        Action = action;
        _identities = torrents.Select(torrent => torrent.TorrentId).ToArray();
        _torrents = torrents.Select(torrent => new FileLocation(torrent.TorrentId, torrent.Name, torrent.SavePath, torrent.SavePath)).ToArray();
        _shared = [];
        _loaded = false;
        _failure = null;
        _destination = torrents.Select(torrent => torrent.MoveDestination).FirstOrDefault(path => path.Length > 0) ?? string.Empty;
        _originalDestination = _destination;
        _includeShared = false;
        _useExisting = false;
        _keptFiles = 0;
        Refresh();
    }

    public async Task RefreshScope()
    {
        if (!CanRefresh) return;
        IsPending = true;
        Refresh();
        try
        {
            var scope = await client.Send("file_scope", new { torrent_ids = _identities });
            _torrents = ReadLocations(scope.GetProperty("torrents"));
            _shared = ReadLocations(scope.GetProperty("shared"));
            _keptFiles = scope.GetProperty("kept_files").GetInt32();
            _loaded = true;
            _failure = null;
        }
        catch (Exception error) { _loaded = false; _failure = error; }
        finally { IsPending = false; Refresh(); }
    }

    public async Task<bool> Submit()
    {
        if (!CanSubmit) return false;
        IsPending = true;
        Refresh();
        try
        {
            var identities = IsMove && IncludeShared ? _identities.Concat(_shared.Select(torrent => torrent.TorrentId)).ToArray() : _identities;
            var outcome = IsMove ? await client.Send("move", new { torrent_ids = identities, destination = Destination, use_existing = UseExisting }) :
                await client.Send("delete_files", new { torrent_ids = identities });
            owner.Accepted("commands", IsMove ? "move" : "delete_files");
            if (!IsMove)
            {
                var kept = outcome.GetProperty("kept_files").GetInt32();
                if (kept > 0) owner.Announce(owner.Text.FormatCount("file_operation", "kept", kept));
            }
            _failure = null;
            Cancel();
            owner.RequestSnapshot();
            return true;
        }
        catch (Exception error) { _failure = error; return false; }
        finally { IsPending = false; Refresh(); }
    }

    internal void Cancel()
    {
        _destination = string.Empty;
        _originalDestination = string.Empty;
        _includeShared = false;
        _useExisting = false;
        Refresh();
    }

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private static FileLocation[] ReadLocations(JsonElement rows) => rows.EnumerateArray().Select(row => new FileLocation(
        row.GetProperty("torrent_id").GetString()!, row.GetProperty("name").GetString()!, row.GetProperty("save_path").GetString()!,
        row.GetProperty("folder").GetString()!)).ToArray();

    private sealed record FileLocation(string TorrentId, string Name, string SavePath, string Folder);
}
