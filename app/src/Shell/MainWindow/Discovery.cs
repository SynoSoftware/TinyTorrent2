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
    // claim, so only a file that no torrent lists can be hidden. Its folder
    // comes first, then each folder above it, so one choice hides a game or a
    // whole drive.
    private MenuSubmenu HideFolderMenu()
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
        return new MenuSubmenu(
            Model.Text.Get("discovery", "hide"),
            Segoe.CircleOff,
            [
                .. folders.Select(folder => new MenuCommand(
                    folder,
                    Segoe.Folder,
                    new RelayCommand(() => HideFolder(folder), () => Model.Disk is not null)
                )),
            ]
        )
        {
            IsEnabled = folders.Count > 0,
        };
    }

    // Settings undoes the choice, so it asks nothing.
    private async Task HideFolder(string folder)
    {
        if (Model.Disk is not { } disk)
            return;
        try
        {
            if (await disk.Hide(folder))
                Model.Notifications.Announce(Model.Text.Format("discovery", "hide_done", folder));
            else
                Model.Notifications.Report(
                    new InvalidOperationException("The list of hidden folders is full."),
                    "failed_hide",
                    () => folder,
                    () => Model.Text.Format("discovery", "hide_full", DiskSource.HiddenLimit)
                );
        }
        catch (Exception error)
        {
            Model.Notifications.Report(error, "failed_hide", () => folder, null);
        }
    }

    // The prompt stays open while gozler's setup runs, and closes when gozler
    // is installed. Cancel stops a download, not a running setup.
    private async Task InstallGozler()
    {
        var setup = Model.GozlerSetup;
        await Interact(async interaction =>
        {
            setup.Begin();
            var body = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var progress = new ProgressBar();
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
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
                dialog.PrimaryButtonText =
                    stage is InstallStage.Failed { Reason: not InstallFailure.Unavailable }
                        ? Model.Text.Get("inspector", "retry")
                        : Model.Text.Get("gozler", "install");
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
                        dialog.Title = Model.Text.Get("gozler", "title");
                        body.Text = Model.Text.Get("gozler", "body");
                        dialog.PrimaryToolTip = Model.Text.Get("gozler", "install_tip");
                        dialog.Footer = setup.Size is { } size
                            ? new DialogFooter.Detail(
                                Model.Text.Format("gozler", "source", Model.Text.Bytes(size))
                            )
                            : null;
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

    // A setup that leaves no service reads as Settings says it.
    private string Failure(InstallStage.Failed failed)
    {
        var message =
            failed.Reason == InstallFailure.NotInstalled
                ? Model.Text.Get("discovery", "notinstalled")
                : Model.Text.Get("gozler", "failure_" + failed.Reason.ToString().ToLowerInvariant());
        return failed.Detail is { } detail
            ? Model.Text.Format("errors", "detail", message, detail)
            : message;
    }
}
