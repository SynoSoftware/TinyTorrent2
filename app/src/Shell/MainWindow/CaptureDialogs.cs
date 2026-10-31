using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureRecovery(List<object> outcomes, List<string> completed)
    {
        await ShowTorrents();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 680));
        var torrents = Model.Torrents.ToArray();
        var directory = Model.DataDirectory ?? throw new InvalidOperationException("Recovery has no disposable store.");
        var destination = Path.Combine(Path.GetDirectoryName(directory)!, "payload");
        var source = torrents[0].SavePath;
        var absent = @"\\127.0.0.1\TinyTorrent-destination-absent-" + Guid.NewGuid();
        foreach (var download in new[] { false, true })
        {
            var invalid = download ? absent : Path.Combine(source, "another-folder");
            var editing = ShowMove(torrents, download: download, destination: invalid);
            await CaptureReady(Model.MoveDraft, () => Model.MoveDraft.CanSubmit);
            await CaptureLayout();
            CaptureInvoke(_interaction?.Dialog ?? throw new InvalidOperationException("The folder editor did not open."));
            await CaptureReady(Model.MoveDraft, () => !Model.MoveDraft.IsPending && Model.MoveDraft.DestinationError.Length > 0);
            if (_interaction?.Dialog is not { } dialog || editing.IsCompleted ||
                torrents.Any(torrent => torrent.SavePath != source || torrent.IsMoving || torrent.MoveDestination.Length > 0))
                throw new InvalidOperationException("An invalid destination closed the editor or changed a torrent.");
            await CapturePage(download ? "invalid-download-folder" : "invalid-move-folder");
            dialog.Hide();
            await editing;
            outcomes.Add(new { journey = "invalid destination stays inline", download, unchanged = true });
        }
        var moving = ShowMove(torrents, destination: destination);
        await CaptureReady(Model.MoveDraft, () => Model.MoveDraft.CanSubmit);
        await CaptureLayout();
        CaptureInvoke(_interaction?.Dialog ?? throw new InvalidOperationException("Move did not open."));
        await moving;
        await CaptureReady(Model, () => Model.Notifications.Toasts.OfType<Toast.CommandError>()
            .Any(toast => toast.Failure.Recovery is not null));
        var recovery = Model.Notifications.Toasts.OfType<Toast.CommandError>().Single(toast => toast.Failure.Recovery is not null);
        await CaptureReady(Model, () => recovery.Download.CanExecute(null));
        var original = recovery.Failure;
        var root = Path.GetPathRoot(source)!;
        const string location = "location:capture-recovery";
        recovery.Failure = original with { Recovery = original.Recovery! with { Destination = Path.Combine(source, "new-folder") } };
        Model.ObserveLocation(location, new(root, "download_path_unavailable", torrents.Length));
        if (recovery.Action != recovery.Choose || recovery.Download.CanExecute(null)
            || !recovery.Choose.CanExecute(null) || Model.IsLocationUnavailable(root.TrimEnd('\\') + "-other"))
            throw new InvalidOperationException("A known unavailable share still offered Download here, or matched another share.");
        await CapturePage("known-unavailable-recovery");
        Model.ObserveLocation(location, null);
        if (recovery.Action != recovery.Download)
            throw new InvalidOperationException("The recovery action did not follow the resolved location.");
        recovery.Failure = original with { Recovery = original.Recovery! with { Destination = absent } };
        Model.Notifications.Sync();
        recovery.Download.Execute(null);
        await CaptureReady(recovery, () => recovery.IsDestinationError && recovery.Action == recovery.Choose);
        if (torrents.Any(torrent => torrent.SavePath != source || torrent.IsMoving) ||
            Model.Notifications.Toasts.OfType<Toast.CommandError>().Count(toast => toast.Failure.Recovery is not null) != 1)
            throw new InvalidOperationException("A refused recovery changed storage or added another recovery toast.");
        await CapturePage("unavailable-recovery-destination");
        outcomes.Add(new { journey = "unavailable recovery destination", previousFolderRetained = true, oneToast = true });
        recovery.Failure = original;
        Model.Notifications.Sync();
        await CapturePage("move-recovery");
        recovery.Download.Execute(null);
        await CaptureReady(Model, () => torrents.All(torrent => torrent.SavePath == destination && !torrent.IsMoving && !torrent.IsError));
        if (_interaction is not null || Model.Notifications.Toasts.Contains(recovery))
            throw new InvalidOperationException("Download here opened an editor or retained the obsolete recovery toast.");
        outcomes.Add(new { journey = "direct batch recovery", torrents = torrents.Length, editorOpened = false, obsoleteToastRemoved = true });

        completed.Add("move-recovery");
        completed.Add("unavailable-recovery-destination");
    }

    private async Task CaptureDialogs(List<object> outcomes, List<string> completed)
    {
        await ShowTorrents();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 680));
        Model.AddDraft.EditingMagnet = true;
        var adding = ShowAdd();
        await CaptureLayout();
        var add = _interaction?.Dialog ?? throw new InvalidOperationException("Add did not open.");
        Model.AddDraft.Destination = @"\\127.0.0.1\TinyTorrent-absent-" + Guid.NewGuid();
        await CaptureReady(Model.AddDraft, () => Model.AddDraft.HasDestinationError);
        await CapturePage("unavailable-folder", add.Content as FrameworkElement);
        outcomes.Add(new { journey = "unavailable destination", warning = true });
        add.Hide();
        await adding;

        var target = Model.Torrents.First();
        var count = Model.Torrents.Count;
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        Torrents.FocusRows(FocusState.Keyboard);
        var removing = ShowRemoval([target], DeletionMode.Recycle);
        await CaptureLayout();
        await CaptureReady(Model.RemovalDraft, () => Model.RemovalDraft.CanSubmit && Model.RemovalDraft.CanRefresh);
        var dialog = _interaction?.Dialog ?? throw new InvalidOperationException("Remove did not open.");
        Model.RemovalDraft.DeleteDownloaded = false;
        Model.RemovalDraft.DeleteTorrentFiles = false;
        await CapturePage("removal-focus", dialog.Content as FrameworkElement);
        var focus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        if (focus?.Name != "PrimaryButton")
            throw new InvalidOperationException($"Remove started with focus on {focus?.GetType().Name}:{focus?.Name}.");
        var closed = false;
        var removedWhileOpen = false;
        var closing = 0;
        var closes = 0;
        dialog.Closing += (_, _) => closing++;
        dialog.Closed += (_, _) => { closed = true; closes++; };
        void MembershipChanged(object? sender, PropertyChangedEventArgs args) =>
            removedWhileOpen |= !closed && !Model.Contains(target);
        Model.PropertyChanged += MembershipChanged;
        try
        {
            CaptureInvoke(dialog);
            await removing;
        }
        finally
        {
            Model.PropertyChanged -= MembershipChanged;
        }
        if (removedWhileOpen || closing != 1 || closes != 1 || Model.Contains(target) || Model.Torrents.Count != count - 1)
            throw new InvalidOperationException("Removal changed membership before the modal closed, reopened, or did not remove its target.");
        if (!IsInside(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject, Torrents))
            throw new InvalidOperationException("Removal did not leave focus on the torrent table.");
        outcomes.Add(new { journey = "removal lifecycle", initialFocus = focus.Name,
            removedAfterClose = true, closes, focusReturnedToTable = true });
        completed.Add("unavailable-folder");
        completed.Add("removal-focus");
    }
}
