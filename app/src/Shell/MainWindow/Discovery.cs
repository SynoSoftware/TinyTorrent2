using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;
using Windows.System;

namespace Syno.TinyTorrent.Shell;

// Disk discovery's window parts: the facts that decide when it asks gozler,
// its Library commands and gozler's install prompt.
public sealed partial class MainWindow
{
    private void ConfigureDiscovery()
    {
        Model.GozlerRequested += async (_, _) => await InstallGozler();
        Activated += (_, args) =>
            Model.Features.SetActive(args.WindowActivationState != WindowActivationState.Deactivated);
        var refresh = new KeyboardAccelerator { Key = VirtualKey.F5 };
        _shortcuts.Add(Model.RefreshDiscovery, refresh);
        AddShortcut(
            refresh,
            () => Run(Model.RefreshDiscovery),
            canInvoke: () => Model.Page == WindowPage.Library
        );
    }

    // Discovery asks gozler only while the person can see Library.
    private void ObserveLibraryShown() =>
        Model.Features.SetLibraryShown(Model.Page == WindowPage.Library && !Model.IsMinimized);

    private bool Discovers => Model.Disk is { State.Status: not DiskStatus.Off };

    // The Library menu's page command, after its file commands.
    private IEnumerable<MenuEntry> DiscoveryMenu()
    {
        if (!Discovers)
            yield break;
        yield return new MenuSeparator();
        yield return new MenuCommand(
            Model.Text.Get("discovery", "refresh"),
            Segoe.FolderSearch,
            Model.RefreshDiscovery
        )
        {
            Shortcut = ShortcutText(Model.RefreshDiscovery),
        };
    }

    // A file that a torrent lists stays in Library by the torrent's own
    // claim, so only a file that no torrent lists offers this. Its folder
    // comes first, then each folder above it, so one choice leaves out a game
    // or a whole drive.
    private MenuSubmenu LeaveOutMenu()
    {
        var folders = new List<string>();
        if (
            Model.Library.Selected is { } selected
            && Model.Library.HasOrigins(selected.Entry.EntryId)
            && Model.Library.Origins.Count == 0
        )
        {
            for (
                var folder = selected.Entry.Folder;
                !string.IsNullOrEmpty(folder);
                folder = Path.GetDirectoryName(folder)
            )
                folders.Add(folder);
        }
        // The toast's Undo and Settings undo the choice, and the files return
        // with their matches, so it asks nothing.
        return new MenuSubmenu(
            Model.Text.Get("discovery", "leave_out"),
            Segoe.CircleOff,
            [
                .. folders.Select(folder => new MenuCommand(
                    folder,
                    Segoe.Folder,
                    new RelayCommand(() => Model.LeaveOut(folder), () => Model.Disk is not null)
                )),
            ]
        )
        {
            IsEnabled = folders.Count > 0,
        };
    }

    // The prompt stays open while gozler's setup runs, and closes when gozler
    // is installed. Cancel stops a download, not a running setup. An installed
    // gozler that is too old gets the same prompt in the words of an update;
    // the refresh that follows shows whether the update worked.
    private async Task InstallGozler()
    {
        var setup = Model.GozlerSetup;
        var updating = Gozler.Locate() == GozlerProgram.Installed;
        await Interact(async interaction =>
        {
            var body = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var progress = new ProgressBar();
            // Selectable, so the address that a failure names can be copied.
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            var dialog = new Dialog
            {
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        body,
                        new StackPanel { Spacing = 8, Children = { progress, status } },
                    },
                },
                Glyph = Segoe.FolderSearch,
                PrimaryGlyph = Segoe.Download,
            };
            void Show()
            {
                var stage = setup.Stage;
                // The bar keeps its place while hidden, so the dialog does not
                // grow when the installation starts.
                var working = stage is InstallStage.Downloading or InstallStage.Running;
                progress.Opacity = working ? 1 : 0;
                AutomationProperties.SetAccessibilityView(
                    progress,
                    working ? AccessibilityView.Content : AccessibilityView.Raw
                );
                if (stage is InstallStage.Downloading { Progress: WorkProgress.Fraction fraction })
                {
                    progress.IsIndeterminate = false;
                    progress.Maximum = fraction.Total;
                    progress.Value = fraction.Completed;
                }
                else
                    progress.IsIndeterminate = working;
                status.Text = stage switch
                {
                    InstallStage.Downloading => Model.Text.Get("gozler", "downloading"),
                    InstallStage.Running => Model.Text.Get("gozler", "running"),
                    InstallStage.Failed failed => Failure(failed),
                    _ => string.Empty,
                };
                // Until the owner publishes the release that TinyTorrent
                // names, asking again fails the same way, so only Close stays.
                var unreleased = stage is InstallStage.Failed { Reason: InstallFailure.NotReleased };
                dialog.PrimaryButtonText =
                    unreleased ? string.Empty
                    : stage is InstallStage.Failed ? Model.Text.Get("inspector", "retry")
                    : Model.Text.Get("gozler", updating ? "update" : "install");
                dialog.CloseButtonText = unreleased
                    ? Model.Text.Get("commands", "close")
                    : Model.Text.Get("dialog", "cancel");
            }
            void Follow(object? sender, PropertyChangedEventArgs args)
            {
                if (setup.Stage is InstallStage.Installed)
                    dialog.Hide();
                else
                    Show();
            }
            setup.PropertyChanged += Follow;
            try
            {
                await ShowEditor(
                    interaction,
                    dialog,
                    setup,
                    () =>
                    {
                        dialog.Title = Model.Text.Get("gozler", updating ? "update_title" : "title");
                        body.Text = Model.Text.Get("gozler", updating ? "update_body" : "body");
                        dialog.PrimaryToolTip = Model.Text.Get("gozler", "install_tip");
                        Show();
                    }
                );
            }
            finally
            {
                setup.PropertyChanged -= Follow;
            }
            return true;
        });
    }

    // A setup that leaves no service, or a gozler still too old, reads as
    // Settings says it.
    private string Failure(InstallStage.Failed failed)
    {
        var message = failed.Reason switch
        {
            InstallFailure.NotInstalled => Model.Text.Get("discovery", "notinstalled"),
            InstallFailure.Older => Model.Text.Get("discovery", "older"),
            InstallFailure.NotReleased => Model.Text.Format(
                "gozler",
                "failure_notreleased",
                Gozler.SetupRelease.ToString(3),
                Gozler.Releases
            ),
            _ => Model.Text.Get("gozler", "failure_" + failed.Reason.ToString().ToLowerInvariant()),
        };
        return failed.Detail is { } detail
            ? Model.Text.Format("errors", "detail", message, detail)
            : message;
    }
}
