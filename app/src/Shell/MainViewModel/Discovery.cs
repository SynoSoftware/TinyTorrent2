using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    /// <summary>Disk discovery, while the Library database is open.</summary>
    internal DiskSource? Disk => Features.Disk;

    internal GozlerSetup GozlerSetup { get; }

    /// <summary>Raised to open gozler's install prompt.</summary>
    internal event EventHandler? GozlerRequested;

    /// <summary>
    /// Switches discovery on: Settings, Library's empty state and the
    /// message bar's Install.
    /// </summary>
    internal ICommand DiscoverMedia { get; }

    /// <summary>
    /// Opens gozler's prompt to update an installed gozler that is too old:
    /// Settings and the message bar's Update.
    /// </summary>
    internal ICommand UpdateGozler { get; }

    /// <summary>Asks gozler again: Library's Refresh files and each Retry.</summary>
    internal ICommand RefreshDiscovery { get; }

    /// <summary>
    /// Opens gozler's install prompt. After the installation, discovery
    /// switches on only when the request says so: the installer's plugin
    /// tree can install gozler alone.
    /// </summary>
    internal void RequestGozler(bool discover)
    {
        GozlerSetup.Discovers = discover;
        GozlerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Switches discovery on or off. While gozler is not installed, switching
    /// it on opens the install prompt instead, and the option stays off
    /// unless the installation succeeds.
    /// </summary>
    internal async Task SwitchDiscovery(bool on)
    {
        if (Disk is not { } disk)
            return;
        try
        {
            if (!on)
                await disk.Disable();
            else if (Gozler.Locate() == GozlerProgram.NotInstalled)
                RequestGozler(discover: true);
            else
                await disk.Enable();
        }
        catch (Exception error)
        {
            Notifications.Report(error, "failed_settings", null, null);
        }
    }

    /// <summary>
    /// The saved folder choices as last read or changed, for Settings; null
    /// while they are not read or Library's database is closed.
    /// </summary>
    internal FolderChoices? Folders { get; private set; }

    // The disk source whose database Folders came from.
    private DiskSource? _foldersSource;

    internal async Task ReadFolders()
    {
        var disk = Disk;
        // Set now, so a disk change while the read runs starts no second read.
        _foldersSource = disk;
        FolderChoices? folders = null;
        if (disk is not null)
        {
            try
            {
                folders = await disk.ReadFolders();
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine($"Folder choices not read: {error.Message}");
            }
        }
        if (ReferenceEquals(disk, Disk))
            ShowFolders(disk, folders);
    }

    /// <summary>
    /// Saves a change of the folder choices: a check box in Library folders,
    /// Library's Leave folder out and its Undo. Gives what it did, or null
    /// when it failed, which it reports.
    /// </summary>
    internal async Task<FolderChange?> ChangeFolders(Func<FolderChoices, FolderChoices> change, string subject)
    {
        if (Disk is not { } disk)
            return null;
        try
        {
            var result = await disk.ChangeFolders(change);
            if (ReferenceEquals(disk, Disk))
                ShowFolders(disk, result.After);
            return result;
        }
        catch (Exception error)
        {
            Notifications.Report(error, "failed_settings", () => subject, null);
            return null;
        }
    }

    /// <summary>
    /// Library's Leave folder out. One click can leave a whole drive out, so
    /// the toast's Undo puts back the choices it replaced with one click too.
    /// </summary>
    internal async Task LeaveOut(string folder)
    {
        var result = await ChangeFolders(choices => choices.Set(folder, shown: false), folder);
        if (result is not null)
            Notifications.ShowLeftOut(folder, result.Before.Within(folder));
    }

    private void ShowFolders(DiskSource? disk, FolderChoices? folders)
    {
        _foldersSource = disk;
        Folders = folders;
        Changed(nameof(Folders));
    }

    // What resolves a disk state, in Settings and in Library's message.
    // An older gozler needs the update, because asking again changes nothing.
    // Retry follows a repair made in gozler, and a drive that gozler cannot
    // keep current changes only when it is asked again.
    internal static DiskRecovery Recovery(DiskState state) =>
        state.Status switch
        {
            DiskStatus.NotInstalled => DiskRecovery.Install,
            DiskStatus.Older => DiskRecovery.Update,
            DiskStatus.OutOfDate => DiskRecovery.Retry,
            _ when state.IsFailure => DiskRecovery.Retry,
            _ => DiskRecovery.None,
        };

    // Library's message about discovery, which asks gozler only while
    // Library shows: a failure, or a drive whose files may be missing.
    private Status? DiscoveryStatus()
    {
        if (Page != WindowPage.Library || Disk?.State is not { } state)
            return null;
        var (title, severity) = state.Status switch
        {
            DiskStatus.Indexing => ("indexing_title", InfoBarSeverity.Informational),
            DiskStatus.OutOfDate => ("outofdate_title", InfoBarSeverity.Warning),
            _ when state.IsFailure => ("paused_title", InfoBarSeverity.Warning),
            _ => ((string?)null, InfoBarSeverity.Informational),
        };
        if (title is null)
            return null;
        return new(
            Text.Get("discovery", title),
            state.Describe(Text),
            severity,
            Recovery(state) switch
            {
                DiskRecovery.Install => new StatusAction(
                    DiscoverMedia,
                    Text.Get("gozler", "install"),
                    Syno.Segoe.Download,
                    Text.Get("discovery", "install_tip")
                ),
                DiskRecovery.Update => new StatusAction(
                    UpdateGozler,
                    Text.Get("gozler", "update"),
                    Syno.Segoe.Download,
                    Text.Get("discovery", "update_tip")
                ),
                DiskRecovery.Retry => new StatusAction(
                    RefreshDiscovery,
                    Text.Get("inspector", "retry"),
                    Syno.Segoe.RotateCcw,
                    Text.Get("discovery", "retry_tip")
                ),
                _ => null,
            }
        );
    }

    private void OnDiskChanged()
    {
        Library.CanDiscover = Disk is { State.Status: DiskStatus.Off };
        ((RelayCommand)DiscoverMedia).Refresh();
        ((RelayCommand)UpdateGozler).Refresh();
        ((RelayCommand)RefreshDiscovery).Refresh();
        Changed(nameof(Disk));
        // Choices that were read come from the database that Library opened
        // again or closed.
        if (Folders is not null && !ReferenceEquals(_foldersSource, Disk))
            _ = ReadFolders();
    }
}
