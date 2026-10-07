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
    private Dictionary<string, Torrent> _selection = [];
    private FileLocation[] _torrents = [];
    private FileLocation[] _shared = [];
    private Exception? _failure;
    private bool _loaded;
    private bool _reading;
    private bool _submitting;
    private string _destination = string.Empty;
    private string _originalDestination = string.Empty;
    private bool _includeShared;
    private bool _useExisting;
    public FileAction Action { get; private set; }
    public bool IsPending => _submitting || IsMove && _reading;
    public bool HasDraft => Action == FileAction.Move && !string.IsNullOrWhiteSpace(Destination) &&
        (Destination != _originalDestination || IncludeShared || UseExisting);
    // Delete does not need the scope, so a failed scope neither blocks it nor
    // shows an error: the dialog lists the save folders, and the engine still
    // keeps files that other torrents use.
    private bool CanSave => owner.CanSave && !IsPending && _identities.Length > 0 &&
        (Action == FileAction.Delete || _loaded && HasDestination && (!HasShared || IncludeShared));
    public bool CanSubmit => CanSave && !owner.IsClosing;
    public bool CanRefresh => owner.CanEdit && !_reading && !_submitting && _identities.Length > 0;
    public bool IsMove => Action == FileAction.Move;
    public bool HasShared => _shared.Length > 0;
    public bool HasDestination => Destination.Length > 0;
    public string Title => IsMove ? owner.Text.Get("commands", "move") : owner.Text.Get("file_operation", "delete_title");
    public bool IsDelete => !IsMove;
    public string SubmitText => owner.Text.Get("file_operation", IsMove ? "move" : "delete");
    public string SubmitGlyph => IsMove ? Lucide.FolderInput : Lucide.Trash2;
    public string SubmitToolTip => owner.Text.Get("file_operation", IsMove ? "move_tip" : "delete_tip");
    public string Warning => owner.Text.Get("file_operation", "delete_warning");
    public string Locations => string.Join(Environment.NewLine,
        _torrents.Select(torrent => torrent.Name + Environment.NewLine + torrent.Folder));
    public FolderGroup[] Folders => _torrents.GroupBy(torrent => torrent.SavePath, StringComparer.OrdinalIgnoreCase)
        .Select(folder => new FolderGroup(folder.Key, folder.Select(torrent => new TorrentSize(torrent.Name, Selected(torrent).SizeText)).ToArray()))
        .ToArray();
    public string Total => owner.Text.Format("units", "detail", owner.Text.FormatCount("window", "torrents", _torrents.Length),
        owner.Text.Bytes(_torrents.Sum(torrent => Selected(torrent).Size)));
    public string ResultingFolders => !HasDestination ? string.Empty : string.Join(Environment.NewLine,
        _torrents.Concat(IncludeShared ? _shared : []).Select(torrent =>
        {
            var relative = Path.GetRelativePath(torrent.SavePath, torrent.Folder);
            return relative == "." ? Destination : Path.Combine(Destination, relative);
        }).Distinct(StringComparer.OrdinalIgnoreCase));
    public string SharedText => IsMove ?
        owner.Text.Format("file_operation", "move_shared", string.Join(Environment.NewLine, _shared.Select(torrent => torrent.Name))) :
        owner.Text.Format("file_operation", "delete_shared", string.Join(", ", _shared.Select(torrent => torrent.Name)));
    public string Message => _failure is null ? string.Empty : owner.Text.Error(_failure);
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
        _selection = torrents.ToDictionary(torrent => torrent.TorrentId);
        _torrents = torrents.Select(torrent => new FileLocation(torrent.TorrentId, torrent.Name, torrent.SavePath, torrent.SavePath)).ToArray();
        _shared = [];
        _loaded = false;
        _reading = false;
        _failure = null;
        _destination = torrents.Select(torrent => torrent.MoveDestination).FirstOrDefault(path => path.Length > 0) ?? string.Empty;
        _originalDestination = _destination;
        _includeShared = false;
        _useExisting = false;
        Refresh();
    }

    public async Task RefreshScope()
    {
        if (!CanRefresh) return;
        var identities = _identities;
        var failure = _failure;
        _reading = true;
        Refresh();
        try
        {
            var scope = await client.Send("file_scope", new { torrent_ids = identities });
            if (identities != _identities) return;
            _torrents = ReadLocations(scope.GetProperty("torrents"));
            _shared = ReadLocations(scope.GetProperty("shared"));
            _loaded = true;
            if (_failure == failure) _failure = null;
        }
        catch (Exception error)
        {
            if (identities == _identities)
            {
                _loaded = false;
                if (IsMove) _failure = error;
            }
        }
        finally
        {
            if (identities == _identities) { _reading = false; Refresh(); }
        }
    }

    public async Task<bool> Submit()
    {
        if (!CanSave) return false;
        _submitting = true;
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
        finally { _submitting = false; Refresh(); }
    }

    internal void Cancel()
    {
        _identities = [];
        _reading = false;
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

    private Torrent Selected(FileLocation torrent) => _selection[torrent.TorrentId];

    private sealed record FileLocation(string TorrentId, string Name, string SavePath, string Folder);
}

// The selected torrents saved in one folder, as Delete files lists them.
public sealed record FolderGroup(string Folder, TorrentSize[] Torrents);

public sealed record TorrentSize(string Name, string Size);
