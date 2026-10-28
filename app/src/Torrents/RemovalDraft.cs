using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Torrents;

// The Remove torrents dialog. The key that opened it chose the deletion mode,
// so the consequence line states it instead of asking, and the check boxes
// start as the person last submitted them.
public sealed class RemovalDraft : FileDraft
{
    private DeletionMode _deletion;
    private bool _deleteDownloaded;
    private bool _deleteTorrentFiles;

    // Closing the window drops a removal, so it holds no draft to resolve.
    public override bool HasDraft => false;

    // Removal does not need the scope, so a failed scope neither blocks it nor
    // shows an error: the dialog lists the save folders, and the engine still
    // keeps files that other torrents use.
    public override bool CanSubmit => CanSend;
    public override ClosePolicy ClosePolicy => new ClosePolicy.Discard();
    public override string Title => Text.FormatCount("remove", "title", Locations.Length);
    public override string Glyph => Lucide.Trash2;
    public override string SubmitText => Text.Get("commands", "remove");
    public override string SubmitGlyph => Lucide.ListX;
    public override string SubmitToolTip =>
        DeletesFiles ? Text.Get("remove", Recycles ? "recycle_tip" : "delete_tip")
        : Text.Get("remove", "tip");
    public DialogPreference Confirmation { get; }
    public FolderGroup[] Folders { get; private set; } = [];
    public bool DeleteDownloaded
    {
        get => _deleteDownloaded;
        set
        {
            if (_deleteDownloaded == value)
                return;
            _deleteDownloaded = value;
            Failure = null;
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
    private int TorrentFileCount => Locations.Count(torrent => torrent.TorrentFile.Length > 0);
    private bool Recycles => _deletion == DeletionMode.Recycle;

    private long DownloadedBytes => Locations.Sum(torrent => Selected(torrent).Size);
    private long TorrentFileBytes => Locations.Sum(torrent => torrent.TorrentFileSize);
    public string DownloadedText => Text.Get("remove", "files");
    public string DownloadedSize => Text.Format("remove", "size", Text.Bytes(DownloadedBytes));
    public string TorrentFilesText =>
        TorrentFileCount == Locations.Length
            ? Text.FormatCount("remove", "torrent_files", TorrentFileCount)
            : Text.Format("remove", "some_torrent_files", TorrentFileCount, Locations.Length);
    public string TorrentFilesSize => Text.Format("remove", "size", Text.Bytes(TorrentFileBytes));

    // The line under the check boxes says what Remove does to the files, so
    // the person need not know which key chose the deletion mode.
    private bool DeletesFiles => DeleteDownloaded || DeletesTorrentFiles;
    public bool DeletesPermanently => DeletesFiles && !Recycles;
    private bool DeletesTorrentFiles => DeleteTorrentFiles && HasTorrentFiles;
    public string ConsequenceGlyph =>
        !DeletesFiles ? Lucide.Info
        : Recycles ? Lucide.Trash2
        : Lucide.TriangleAlert;
    public string Consequence
    {
        get
        {
            if (!DeletesFiles)
                return Text.FormatCount("remove", "keeps_files", Locations.Length);
            // The engine keeps the files other torrents use, so the total
            // would overstate what goes.
            if (DeleteDownloaded && HasShared)
                return Text.Get(
                    "remove",
                    Recycles ? "recycles_except_shared" : "deletes_except_shared"
                );
            var size = Text.Bytes(
                (DeleteDownloaded ? DownloadedBytes : 0)
                    + (DeletesTorrentFiles ? TorrentFileBytes : 0)
            );
            return !Recycles ? Text.Format("remove", "deletes", size)
                : DeleteDownloaded ? Text.Format("remove", "recycles", size)
                : Text.FormatCount("remove", "recycles_torrent_files", TorrentFileCount);
        }
    }

    internal RemovalDraft(MainViewModel owner, PipeClient client)
        : base(owner, client)
    {
        Confirmation = new(owner.Settings);
    }

    internal void Begin(Torrent[] torrents, DeletionMode deletion)
    {
        Load(torrents);
        Confirmation.NeverShow = false;
        _deletion = deletion;
        // Remove opens only from a command that needs the engine, which has
        // confirmed every setting by then.
        _deleteDownloaded = Owner.Settings.DeleteDownloaded.ConfirmedOn == true;
        _deleteTorrentFiles = Owner.Settings.DeleteTorrentFile.ConfirmedOn == true;
        Refresh();
    }

    private protected override async Task<bool> Send()
    {
        var deletesDownloaded = DeleteDownloaded;
        await Confirmation.Save(Owner.Settings.ConfirmDelete);
        await SaveChoices();
        var subtitles = Owner.Subtitles;
        var subtitleDeletion =
            deletesDownloaded && subtitles is not null
                ? await subtitles.CaptureDeletion(TorrentIds)
                : null;
        var outcome = await Client.Send(
            deletesDownloaded ? "delete_files" : "remove",
            new
            {
                torrent_ids = TorrentIds,
                deletion = Recycles ? "recycle" : "permanent",
                torrent_files = DeleteTorrentFiles,
            },
            Requester.Person
        );
        Owner.Notifications.AnnounceAccepted("commands", "remove");
        if (subtitleDeletion is not null && subtitles is not null)
            subtitles.Delete(subtitleDeletion, _deletion);
        if (!deletesDownloaded)
            return true;
        var count = outcome.GetProperty("kept_files");
        if (count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var kept) && kept > 0)
            Owner.Notifications.Announce(Text.FormatCount("file_action", "kept", kept));
        return true;
    }

    // Remove remembers the check boxes for the next removal; Cancel does not.
    private Task SaveChoices()
    {
        var settings = Owner.Settings;
        var choices = new Dictionary<string, bool>();
        if (DeleteDownloaded != settings.DeleteDownloaded.ConfirmedOn)
            choices[settings.DeleteDownloaded.Name] = DeleteDownloaded;
        if (DeleteTorrentFiles != settings.DeleteTorrentFile.ConfirmedOn)
            choices[settings.DeleteTorrentFile.Name] = DeleteTorrentFiles;
        return choices.Count > 0 ? settings.Save(choices) : Task.CompletedTask;
    }

    private protected override void Clear()
    {
        Folders = [];
        Confirmation.NeverShow = false;
    }

    private protected override void RefreshItems()
    {
        base.RefreshItems();
        Folders = Locations
            .GroupBy(torrent => torrent.SavePath, StringComparer.OrdinalIgnoreCase)
            .Select(folder => new FolderGroup(folder.Key, folder.Select(Selected).ToArray()))
            .ToArray();
    }
}

// The selected torrents saved in one folder, as Remove torrents lists them.
public sealed record FolderGroup(string Folder, Torrent[] Torrents);
