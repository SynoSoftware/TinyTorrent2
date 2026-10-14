using System.ComponentModel;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

public sealed class FileDraft : IDraft
{
    private readonly MainViewModel _owner;
    private readonly PipeClient _client;
    private string[] _torrentIds = [];
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
    private DeletionMode _deletion;
    public FileAction Action { get; private set; }
    public bool IsPending => _submitting || IsMove && _reading;
    public bool HasDraft =>
        Action == FileAction.Move
        && !string.IsNullOrWhiteSpace(Destination)
        && (Destination != _originalDestination || IncludeShared);

    // Delete does not need the scope, so a failed scope neither blocks it nor
    // shows an error: the dialog lists the save folders, and the engine still
    // keeps files that other torrents use.
    private bool CanSave =>
        _owner.CanSave
        && !IsPending
        && _torrentIds.Length > 0
        && (
            Action == FileAction.Delete
            || _loaded && HasDestination && (!HasShared || IncludeShared)
        );
    public bool CanSubmit => CanSave && !_owner.IsClosing;
    public bool CanRefresh => _owner.CanEdit && !_reading && !_submitting && _torrentIds.Length > 0;
    public bool IsMove => Action == FileAction.Move;
    public bool HasShared => _shared.Length > 0;
    public bool HasDestination => !string.IsNullOrWhiteSpace(Destination);
    public string Title =>
        IsMove
            ? _owner.Text.FormatCount("file_action", "move_title", _torrents.Length)
            : _owner.Text.Get("file_action", "delete_title");
    public bool IsDelete => !IsMove;
    public DialogPreference Confirmation { get; }
    public string SubmitText => _owner.Text.Get("file_action", IsMove ? "move" : "delete");
    public string SubmitGlyph => IsMove ? Lucide.FolderInput : Lucide.Trash2;
    public string SubmitToolTip =>
        _owner.Text.Get(
            "file_action",
            IsMove ? "move_tip"
                : _deletion == DeletionMode.Recycle ? "recycle_tip"
                : "delete_tip"
        );
    public string Warning =>
        _owner.Text.Get(
            "file_action",
            _deletion == DeletionMode.Recycle ? "recycle_warning" : "delete_warning"
        );
    public string Locations =>
        _torrents
            .Select(torrent => torrent.SavePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == 1
            ? _torrents[0].SavePath
            : _owner.Text.Get("file_action", "multiple_locations");
    public Torrent[] Torrents { get; private set; } = [];
    public FolderGroup[] Folders { get; private set; } = [];
    public string Total =>
        IsMove && _torrents.Length == 1
            ? string.Empty
            : _owner.Text.Format(
                "units",
                "detail",
                _owner.Text.FormatCount("window", "torrents", _torrents.Length),
                _owner.Text.Bytes(_torrents.Sum(torrent => Selected(torrent).Size))
            );
    public string SharedText =>
        IsMove
            ? _owner.Text.Format(
                "file_action",
                "move_shared",
                string.Join(Environment.NewLine, _shared.Select(torrent => torrent.Name))
            )
            : _owner.Text.Format(
                "file_action",
                "delete_shared",
                string.Join(", ", _shared.Select(torrent => torrent.Name))
            );
    public string DestinationError =>
        IsMove && _failure is { } failure && AddDraft.IsDestinationError(failure)
            ? _owner.Text.Error(failure)
            : string.Empty;
    public string Message =>
        _failure is null || DestinationError.Length > 0
            ? string.Empty
            : _owner.Text.Error(_failure);
    public bool HasError => Message.Length > 0;
    public string Destination
    {
        get => _destination;
        set
        {
            if (_destination == value)
                return;
            _destination = value;
            _failure = null;
            Refresh();
        }
    }
    public bool IncludeShared
    {
        get => _includeShared;
        set
        {
            if (_includeShared == value)
                return;
            _includeShared = value;
            Refresh();
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal FileDraft(MainViewModel owner, PipeClient client)
    {
        _owner = owner;
        _client = client;
        Confirmation = new(owner.Settings);
    }

    internal void Begin(Torrent[] torrents, FileAction action)
    {
        Action = action;
        Confirmation.NeverShow = false;
        _deletion =
            _owner.Settings.Deletion.ConfirmedText == "permanent"
                ? DeletionMode.Permanent
                : DeletionMode.Recycle;
        _torrentIds = torrents.Select(torrent => torrent.TorrentId).ToArray();
        _selection = torrents.ToDictionary(torrent => torrent.TorrentId);
        _torrents = torrents
            .Select(torrent => new FileLocation(torrent.TorrentId, torrent.Name, torrent.SavePath))
            .ToArray();
        RefreshItems();
        _shared = [];
        _loaded = false;
        _reading = false;
        _failure = null;
        _destination =
            torrents
                .Select(torrent => torrent.MoveDestination)
                .FirstOrDefault(path => path.Length > 0)
            ?? string.Empty;
        _originalDestination = _destination;
        _includeShared = false;
        Refresh();
    }

    public async Task RefreshScope()
    {
        if (!CanRefresh)
            return;
        var torrentIds = _torrentIds;
        var failure = _failure;
        _reading = true;
        Refresh();
        try
        {
            var scope = await _client.Send("file_scope", new { torrent_ids = torrentIds });
            if (torrentIds != _torrentIds)
                return;
            var torrents = ReadLocations(scope.GetProperty("torrents"));
            var changed = !_torrents.Select(torrent => (torrent.TorrentId, torrent.SavePath))
                .SequenceEqual(torrents.Select(torrent => (torrent.TorrentId, torrent.SavePath)));
            _torrents = torrents;
            if (changed)
                RefreshItems();
            _shared = ReadLocations(scope.GetProperty("shared"));
            _loaded = true;
            if (IsMove && _failure == failure)
                _failure = null;
        }
        catch (Exception error)
        {
            if (torrentIds == _torrentIds)
            {
                _loaded = false;
                if (IsMove)
                    _failure = error;
            }
        }
        finally
        {
            if (torrentIds == _torrentIds)
            {
                _reading = false;
                Refresh();
            }
        }
    }

    public async Task<bool> Submit()
    {
        if (!CanSave)
            return false;
        _submitting = true;
        Refresh();
        try
        {
            var torrentIds =
                IsMove && IncludeShared
                    ? _torrentIds.Concat(_shared.Select(torrent => torrent.TorrentId)).ToArray()
                    : _torrentIds;
            if (IsDelete)
                await Confirmation.Save(_owner.Settings.ConfirmDelete);
            var subtitles = _owner.Subtitles;
            var subtitleDeletion =
                !IsMove && subtitles is not null
                    ? await subtitles.CaptureDeletion(torrentIds)
                    : null;
            var outcome = IsMove
                ? await _client.Send(
                    "move",
                    new { torrent_ids = torrentIds, destination = Destination }
                )
                : await _client.Send(
                    "delete_files",
                    new
                    {
                        torrent_ids = torrentIds,
                        deletion = _deletion == DeletionMode.Recycle ? "recycle" : "permanent",
                    }
                );
            _owner.AnnounceAccepted("commands", IsMove ? "move" : "delete_files");
            if (subtitleDeletion is not null && subtitles is not null)
                subtitles.Delete(subtitleDeletion, _deletion);
            if (!IsMove)
            {
                var count = outcome.GetProperty("kept_files");
                if (
                    count.ValueKind == JsonValueKind.Number
                    && count.TryGetInt32(out var kept)
                    && kept > 0
                )
                    _owner.Announce(_owner.Text.FormatCount("file_action", "kept", kept));
            }
            _failure = null;
            Cancel();
            _owner.RequestSnapshot();
            return true;
        }
        catch (Exception error)
        {
            _failure = error;
            return false;
        }
        finally
        {
            _submitting = false;
            Refresh();
        }
    }

    internal void Cancel()
    {
        _torrentIds = [];
        _selection = [];
        _torrents = [];
        _shared = [];
        Torrents = [];
        Folders = [];
        Confirmation.NeverShow = false;
        _reading = false;
        _destination = string.Empty;
        _originalDestination = string.Empty;
        _includeShared = false;
        Refresh();
    }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private void RefreshItems()
    {
        Torrents = _torrents.Select(Selected).ToArray();
        Folders = _torrents
            .GroupBy(torrent => torrent.SavePath, StringComparer.OrdinalIgnoreCase)
            .Select(folder => new FolderGroup(folder.Key, folder.Select(Selected).ToArray()))
            .ToArray();
    }

    private static FileLocation[] ReadLocations(JsonElement rows) =>
        rows.EnumerateArray()
            .Select(row => new FileLocation(
                row.GetProperty("torrent_id").GetString()!,
                row.GetProperty("name").GetString()!,
                row.GetProperty("save_path").GetString()!
            ))
            .ToArray();

    private Torrent Selected(FileLocation torrent) => _selection[torrent.TorrentId];

    private sealed record FileLocation(string TorrentId, string Name, string SavePath);
}

// The selected torrents saved in one folder, as Delete files lists them.
public sealed record FolderGroup(string Folder, Torrent[] Torrents);
