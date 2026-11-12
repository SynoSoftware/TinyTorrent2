using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    /// <summary>Disk discovery, while the Library database is open.</summary>
    internal DiskSource? Disk => Features.Disk;

    internal GozlerSetup GozlerSetup { get; }

    /// <summary>Raised when switching discovery on needs gozler installed first.</summary>
    internal event EventHandler? GozlerRequested;

    /// <summary>
    /// Switches discovery on: Settings, Library's empty state and the
    /// message bar's Install.
    /// </summary>
    internal ICommand DiscoverMedia { get; }

    /// <summary>Asks gozler again: Library's Refresh files and each Retry.</summary>
    internal ICommand RefreshDiscovery { get; }

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
                GozlerRequested?.Invoke(this, EventArgs.Empty);
            else
                await disk.Enable();
        }
        catch (Exception error)
        {
            Notifications.Report(error, "failed_settings", null, null);
        }
    }

    // What resolves a disk state, in Settings and in Library's message.
    // Retry follows a repair or an update made in gozler, and a drive that
    // gozler cannot keep current changes only when it is asked again.
    internal static DiskRecovery Recovery(DiskState state) =>
        state.Status switch
        {
            DiskStatus.NotInstalled => DiskRecovery.Install,
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
        ((RelayCommand)RefreshDiscovery).Refresh();
        Changed(nameof(Disk));
    }
}
