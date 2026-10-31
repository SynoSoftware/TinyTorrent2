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
        var moving = ShowMove(torrents, destination: destination);
        await CaptureReady(Model.MoveDraft, () => Model.MoveDraft.CanSubmit);
        await CaptureLayout();
        CaptureInvoke(_interaction?.Dialog ?? throw new InvalidOperationException("Move did not open."));
        await moving;
        await CaptureReady(Model, () => Model.Notifications.Toasts.OfType<Toast.CommandError>()
            .Any(toast => toast.Failure.Recovery is not null));
        var recovery = Model.Notifications.Toasts.OfType<Toast.CommandError>().Single(toast => toast.Failure.Recovery is not null);
        await CaptureReady(Model, () => recovery.Download.CanExecute(null));
        await CapturePage("move-recovery");
        recovery.Download.Execute(null);
        await CaptureReady(Model, () => torrents.All(torrent => torrent.SavePath == destination && !torrent.IsMoving && !torrent.IsError));
        if (_interaction is not null || Model.Notifications.Toasts.Contains(recovery))
            throw new InvalidOperationException("Download here opened an editor or retained the obsolete recovery toast.");
        outcomes.Add(new { journey = "direct batch recovery", torrents = torrents.Length, editorOpened = false, obsoleteToastRemoved = true });

        var absent = @"\\127.0.0.1\TinyTorrent-destination-absent-" + Guid.NewGuid();
        var changing = ShowMove(torrents, download: true, destination: absent);
        await CaptureReady(Model.MoveDraft, () => Model.MoveDraft.CanSubmit);
        await CaptureLayout();
        CaptureInvoke(_interaction?.Dialog ?? throw new InvalidOperationException("Set download folder did not open."));
        await changing;
        await CaptureReady(Model, () => torrents.All(torrent => !torrent.IsMoving && torrent.ErrorCode == "move_destination_unavailable") &&
            Model.Notifications.Toasts.OfType<Toast.CommandError>().Any(toast => toast.Folder == absent));
        if (torrents.Any(torrent => torrent.SavePath != destination))
            throw new InvalidOperationException("An unavailable destination replaced the current download folder.");
        await CapturePage("unavailable-recovery-destination");
        outcomes.Add(new { journey = "unavailable recovery destination", previousFolderRetained = true, failedDestination = absent });
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
