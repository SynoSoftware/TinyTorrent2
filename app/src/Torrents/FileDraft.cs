using System.ComponentModel;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

// What Move and Remove share: the selected torrents, the file scope the engine
// reads for them, and the submit that sends one command. Each subclass owns
// its own input and command.
public abstract class FileDraft : IDraft
{
    private string[] _torrentIds = [];
    private Dictionary<string, Torrent> _selection = [];
    private bool _reading;
    private bool _submitting;
    public virtual bool IsPending => _submitting;
    public abstract bool HasDraft { get; }
    public abstract bool CanSubmit { get; }
    public abstract ClosePolicy ClosePolicy { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public abstract string SubmitText { get; }
    public abstract string SubmitGlyph { get; }
    public abstract string SubmitToolTip { get; }
    public bool CanEdit => Owner.CanEdit && !IsPending;
    public bool CanRefresh => CanEdit && !_reading && _torrentIds.Length > 0;
    public bool HasShared => SharedLocations.Length > 0;
    public Torrent[] Torrents { get; private set; } = [];
    // Whether the bar shows a failed scope read, which Refresh retries. A
    // submit error shows without Refresh, because reading again cannot clear it.
    public virtual bool ShowsScopeError => false;
    public string Message =>
        (ShowsScopeError ? ScopeFailure : Failure) is { } failure
            ? Text.Error(failure)
            : string.Empty;
    public bool HasError => Message.Length > 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    private protected FileDraft(MainViewModel owner, PipeClient client)
    {
        Owner = owner;
        Client = client;
    }

    private protected MainViewModel Owner { get; }
    private protected PipeClient Client { get; }
    private protected Strings Text => Owner.Text;
    private protected string[] TorrentIds => _torrentIds;

    // What every submit needs; each subclass adds its own input's checks.
    private protected bool CanSend => Owner.CanSave && !IsPending && _torrentIds.Length > 0;
    private protected FileLocation[] Locations { get; private set; } = [];

    // The other torrents that use the selected torrents' files.
    private protected FileLocation[] SharedLocations { get; private set; } = [];

    // The last submit's error, until the input changes or a submit succeeds.
    private protected Exception? Failure { get; set; }

    // The last scope read's error, until a read succeeds.
    private protected Exception? ScopeFailure { get; private set; }
    private protected bool IsLoaded { get; private set; }
    private protected bool IsReading => _reading;

    // Starts a new dialog for these torrents; the subclass then sets its own
    // input and refreshes.
    private protected void Load(Torrent[] torrents)
    {
        _torrentIds = torrents.Select(torrent => torrent.TorrentId).ToArray();
        _selection = torrents.ToDictionary(torrent => torrent.TorrentId);
        Locations = torrents
            .Select(torrent => new FileLocation(
                torrent.TorrentId,
                torrent.Name,
                torrent.SavePath,
                string.Empty
            ))
            .ToArray();
        RefreshItems();
        SharedLocations = [];
        IsLoaded = false;
        _reading = false;
        Failure = null;
        ScopeFailure = null;
    }

    public async Task RefreshScope()
    {
        if (!CanRefresh)
            return;
        var torrentIds = _torrentIds;
        _reading = true;
        Refresh();
        try
        {
            var scope = await Client.Send(
                "file_scope",
                new { torrent_ids = torrentIds },
                Requester.Window
            );
            var torrents = await Task.Run(() =>
                MeasureTorrentFiles(ReadLocations(scope.GetProperty("torrents")))
            );
            if (torrentIds != _torrentIds)
                return;
            var changed = !Locations
                .Select(torrent => (torrent.TorrentId, torrent.SavePath))
                .SequenceEqual(torrents.Select(torrent => (torrent.TorrentId, torrent.SavePath)));
            Locations = torrents;
            if (changed)
                RefreshItems();
            SharedLocations = ReadLocations(scope.GetProperty("shared"));
            IsLoaded = true;
            ScopeFailure = null;
        }
        catch (Exception error)
        {
            if (torrentIds == _torrentIds)
            {
                IsLoaded = false;
                ScopeFailure = error;
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
        if (!CanSubmit)
            return false;
        _submitting = true;
        Refresh();
        try
        {
            var accepted = await Send();
            Failure = null;
            if (accepted)
                await Discard();
            return accepted;
        }
        catch (Exception error)
        {
            Failure = error;
            return false;
        }
        finally
        {
            _submitting = false;
            Refresh();
        }
    }

    // False when the engine refuses the person's input, which the subclass
    // then shows beside that input.
    private protected abstract Task<bool> Send();

    public Task Discard()
    {
        _torrentIds = [];
        _selection = [];
        Locations = [];
        SharedLocations = [];
        Torrents = [];
        _reading = false;
        ScopeFailure = null;
        Clear();
        Refresh();
        return Task.CompletedTask;
    }

    // Drops the subclass's input, as Discard drops the selection.
    private protected abstract void Clear();

    public void Stop() { }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private protected virtual void RefreshItems() =>
        Torrents = Locations.Select(Selected).ToArray();

    private protected Torrent Selected(FileLocation torrent) => _selection[torrent.TorrentId];

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
                    ? torrent with
                    {
                        TorrentFileSize = file.Length,
                    }
                    : torrent with
                    {
                        TorrentFile = string.Empty,
                    }
            )
            .ToArray();

    // TorrentFile is the .torrent file the torrent was added from, with its
    // size; empty for a magnet, for a file that has gone, and until the scope
    // is read.
    private protected sealed record FileLocation(
        string TorrentId,
        string Name,
        string SavePath,
        string TorrentFile,
        long TorrentFileSize = 0
    );
}
