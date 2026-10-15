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
    private bool _deleteDownloaded;
    private bool _deleteTorrentFiles;
    public FileAction Action { get; private set; }
    public bool IsPending => _submitting || IsMove && _reading;
    public bool HasDraft =>
        Action == FileAction.Move
        && !string.IsNullOrWhiteSpace(Destination)
        && (Destination != _originalDestination || IncludeShared);

    // Removal does not need the scope, so a failed scope neither blocks it nor
    // shows an error: the dialog lists the save folders, and the engine still
    // keeps files that other torrents use.
    private bool CanSave =>
        _owner.CanSave
        && !IsPending
        && _torrentIds.Length > 0
        && (IsRemoval || _loaded && HasDestination && (!HasShared || IncludeShared));
    public bool CanSubmit => CanSave && !_owner.IsClosing;
    public bool CanRefresh => _owner.CanEdit && !_reading && !_submitting && _torrentIds.Length > 0;
    public bool IsMove => Action == FileAction.Move;
    public bool HasShared => _shared.Length > 0;
    // Move asks about the shared files. Removal names them only when it
    // deletes the downloaded files, because otherwise every file stays.
    public bool ShowsMoveShared => IsMove && HasShared;
    public bool ShowsKeptShared => DeleteDownloaded && HasShared;
    public bool HasDestination => !string.IsNullOrWhiteSpace(Destination);
    public string Title =>
        IsMove
            ? _owner.Text.FormatCount("file_action", "move_title", _torrents.Length)
            : _owner.Text.FormatCount("remove", "title", _torrents.Length);
    public string Glyph => IsMove ? Lucide.FolderInput : Lucide.Trash2;
    public bool IsRemoval => !IsMove;

    // Removal's check boxes. The key that opened the dialog chose the
    // deletion mode, so the labels state it instead of asking.
    public bool DeleteDownloaded
    {
        get => _deleteDownloaded;
        set
        {
            if (_deleteDownloaded == value)
                return;
            _deleteDownloaded = value;
            _failure = null;
            Refresh();
        }
    }
    public bool DeleteTorrentFiles
    {
        get => _deleteTorrentFiles;
        set
        {
            if (_deleteTorrentFiles == value)
                return;
            _deleteTorrentFiles = value;
            Refresh();
        }
    }

    // A magnet has no .torrent file, so that check box shows only when a
    // selected torrent has one.
    public bool HasTorrentFiles => TorrentFileCount > 0;
    private int TorrentFileCount => _torrents.Count(torrent => torrent.TorrentFile.Length > 0);
    private bool Recycles => _deletion == DeletionMode.Recycle;
    public string DownloadedText =>
        _owner.Text.Format(
            "remove",
            Recycles ? "recycle_downloaded" : "delete_downloaded",
            _owner.Text.Bytes(_torrents.Sum(torrent => Selected(torrent).Size))
        );
    public string TorrentFilesText
    {
        get
        {
            var size = _owner.Text.Bytes(_torrents.Sum(torrent => torrent.TorrentFileSize));
            return TorrentFileCount == _torrents.Length
                ? _owner.Text.FormatCount(
                    "remove",
                    Recycles ? "recycle_torrent_files" : "delete_torrent_files",
                    TorrentFileCount,
                    size
                )
                : _owner.Text.Format(
                    "remove",
                    Recycles ? "recycle_some_torrent_files" : "delete_some_torrent_files",
                    TorrentFileCount,
                    size,
                    _torrents.Length
                );
        }
    }
    public DialogPreference Confirmation { get; }
    public string SubmitText =>
        IsMove ? _owner.Text.Get("file_action", "move") : _owner.Text.Get("commands", "remove");
    public string SubmitGlyph => IsMove ? Lucide.FolderInput : Lucide.ListX;
    public string SubmitToolTip =>
        IsMove ? _owner.Text.Get("file_action", "move_tip")
        : DeleteDownloaded || DeleteTorrentFiles && HasTorrentFiles
            ? _owner.Text.Get("remove", Recycles ? "recycle_tip" : "delete_tip")
        : _owner.Text.Get("remove", "tip");
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
        _torrents.Length == 1
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

    // Removal deletes the ticked files in the deletion mode, and its check
    // boxes start as the person last submitted them.
    internal void Begin(Torrent[] torrents, FileAction action, DeletionMode deletion)
    {
        Action = action;
        Confirmation.NeverShow = false;
        _deletion = deletion;
        _deleteDownloaded = _owner.Settings.DeleteDownloaded.ConfirmedOn;
        _deleteTorrentFiles = _owner.Settings.DeleteTorrentFile.ConfirmedOn;
        _torrentIds = torrents.Select(torrent => torrent.TorrentId).ToArray();
        _selection = torrents.ToDictionary(torrent => torrent.TorrentId);
        _torrents = torrents
            .Select(torrent => new FileLocation(
                torrent.TorrentId,
                torrent.Name,
                torrent.SavePath,
                string.Empty
            ))
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
            var torrents = await Task.Run(() =>
                MeasureTorrentFiles(ReadLocations(scope.GetProperty("torrents")))
            );
            if (torrentIds != _torrentIds)
                return;
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
            var deletesDownloaded = IsRemoval && DeleteDownloaded;
            if (IsRemoval)
            {
                await Confirmation.Save(_owner.Settings.ConfirmDelete);
                await SaveChoices();
            }
            var subtitles = _owner.Subtitles;
            var subtitleDeletion =
                deletesDownloaded && subtitles is not null
                    ? await subtitles.CaptureDeletion(torrentIds)
                    : null;
            var outcome = IsMove
                ? await _client.Send(
                    "move",
                    new { torrent_ids = torrentIds, destination = Destination }
                )
                : await _client.Send(
                    deletesDownloaded ? "delete_files" : "remove",
                    new
                    {
                        torrent_ids = torrentIds,
                        deletion = Recycles ? "recycle" : "permanent",
                        torrent_files = DeleteTorrentFiles,
                    }
                );
            _owner.AnnounceAccepted("commands", IsMove ? "move" : "remove");
            if (subtitleDeletion is not null && subtitles is not null)
                subtitles.Delete(subtitleDeletion, _deletion);
            if (deletesDownloaded)
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

    // Remove remembers the check boxes for the next removal; Cancel does not.
    private Task SaveChoices()
    {
        var settings = _owner.Settings;
        var choices = new Dictionary<string, bool>();
        if (DeleteDownloaded != settings.DeleteDownloaded.ConfirmedOn)
            choices[settings.DeleteDownloaded.Name] = DeleteDownloaded;
        if (DeleteTorrentFiles != settings.DeleteTorrentFile.ConfirmedOn)
            choices[settings.DeleteTorrentFile.Name] = DeleteTorrentFiles;
        return choices.Count > 0 ? settings.Save(choices) : Task.CompletedTask;
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
                row.GetProperty("save_path").GetString()!,
                row.GetProperty("torrent_file").GetString()!
            ))
            .ToArray();

    // The engine records each .torrent path without reading the disk. A file
    // that has gone since leaves nothing to delete, so it does not count.
    private static FileLocation[] MeasureTorrentFiles(FileLocation[] torrents) =>
        torrents
            .Select(torrent =>
                torrent.TorrentFile.Length > 0
                && new FileInfo(torrent.TorrentFile) is { Exists: true } file
                    ? torrent with { TorrentFileSize = file.Length }
                    : torrent with { TorrentFile = string.Empty }
            )
            .ToArray();

    private Torrent Selected(FileLocation torrent) => _selection[torrent.TorrentId];

    // TorrentFile is the .torrent file the torrent was added from, with its
    // size; empty for a magnet, for a file that has gone, and until the scope
    // is read.
    private sealed record FileLocation(
        string TorrentId,
        string Name,
        string SavePath,
        string TorrentFile,
        long TorrentFileSize = 0
    );
}

// The selected torrents saved in one folder, as Remove torrents lists them.
public sealed record FolderGroup(string Folder, Torrent[] Torrents);
