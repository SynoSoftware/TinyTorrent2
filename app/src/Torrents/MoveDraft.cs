using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

// The Move files dialog: a destination for the selected torrents and, when
// other torrents use the same files, whether they move too.
public sealed class MoveDraft : FileDraft
{
    private string _destination = string.Empty;
    private string _original = string.Empty;
    private bool _includeShared;

    // The engine refuses a folder, so a refused folder shows under the field
    // until the person changes it.
    private Exception? _destinationFailure;

    // The shared-file choice needs the scope, so the dialog waits while it reads.
    public override bool IsPending => base.IsPending || IsReading;
    public override bool HasDraft =>
        HasDestination && (Destination != _original || IncludeShared);
    public override bool CanSubmit =>
        CanSend && IsLoaded && HasDestination && (!HasShared || IncludeShared);
    public override ClosePolicy ClosePolicy =>
        new ClosePolicy.Ask("move", Lucide.FolderInput, () => Destination, () => SubmitText);
    public override string Title =>
        Text.FormatCount("file_action", "move_title", Locations.Length);
    public override string Glyph => Lucide.FolderInput;
    public override string SubmitText => Text.Get("file_action", "move");
    public override string SubmitGlyph => Lucide.FolderInput;
    public override string SubmitToolTip => Text.Get("file_action", "move_tip");
    public bool HasDestination => !string.IsNullOrWhiteSpace(Destination);

    // The one folder that holds the torrents, or null when they are in several.
    public string? Folder =>
        Locations
            .Select(torrent => torrent.SavePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == 1
            ? Locations[0].SavePath
            : null;
    public string FolderText => Folder ?? Text.Get("file_action", "multiple_locations");
    public string Total =>
        Locations.Length == 1
            ? string.Empty
            : Text.Format(
                "units",
                "detail",
                Text.FormatCount("window", "torrents", Locations.Length),
                Text.Bytes(Locations.Sum(torrent => Selected(torrent).Size))
            );
    public string SharedText =>
        Text.Format(
            "file_action",
            "move_shared",
            string.Join(Environment.NewLine, SharedLocations.Select(torrent => torrent.Name))
        );
    public string DestinationError =>
        _destinationFailure is { } failure ? Text.Error(failure) : string.Empty;

    // Move cannot submit without the scope, so a failed read shows. Remove
    // does not need the scope and hides it.
    public override bool ShowsScopeError => ScopeFailure is not null;
    public string Destination
    {
        get => _destination;
        set
        {
            if (_destination == value)
                return;
            _destination = value;
            _destinationFailure = null;
            Failure = null;
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

    internal MoveDraft(MainViewModel owner, PipeClient client)
        : base(owner, client) { }

    // The destination starts at a move the engine has not finished.
    internal void Begin(Torrent[] torrents)
    {
        Load(torrents);
        _destination =
            torrents
                .Select(torrent => torrent.MoveDestination)
                .FirstOrDefault(path => path.Length > 0)
            ?? string.Empty;
        _original = _destination;
        _includeShared = false;
        _destinationFailure = null;
        Refresh();
    }

    private protected override async Task<bool> Send()
    {
        var torrentIds = IncludeShared
            ? TorrentIds.Concat(SharedLocations.Select(torrent => torrent.TorrentId)).ToArray()
            : TorrentIds;
        _destinationFailure = null;
        try
        {
            await Client.Send(
                "move",
                new { torrent_ids = torrentIds, destination = Destination },
                Requester.Person
            );
        }
        catch (Exception error) when (AddDraft.IsDestinationError(error))
        {
            _destinationFailure = error;
            return false;
        }
        Owner.Notifications.AnnounceAccepted("commands", "move");
        return true;
    }

    private protected override void Clear()
    {
        _destination = string.Empty;
        _original = string.Empty;
        _includeShared = false;
        _destinationFailure = null;
    }
}
