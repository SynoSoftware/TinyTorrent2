using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Windows.Graphics;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task CaptureFiles(List<object> outcomes, List<string> completed)
    {
        var store =
            Model.DataDirectory
            ?? throw new InvalidOperationException("The files capture has no store.");
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(store, "files-capture.json"))
        );
        var fixture = document.RootElement;
        var source =
            fixture.GetProperty("source").GetString()
            ?? throw new InvalidOperationException("The files capture has no source.");
        var collision =
            fixture.GetProperty("collision").GetString()
            ?? throw new InvalidOperationException("The files capture has no collision folder.");
        var destination =
            fixture.GetProperty("destination").GetString()
            ?? throw new InvalidOperationException("The files capture has no destination.");
        var torrentIds = fixture
            .GetProperty("torrents")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        var group = torrentIds
            .Select(torrentId => Model.Torrents.Single(torrent => torrent.TorrentId == torrentId))
            .ToArray();
        var outside = Model.Torrents.Single(torrent =>
            torrent.TorrentId == fixture.GetProperty("outside").GetString()
        );
        var root =
            Path.GetDirectoryName(store)
            ?? throw new InvalidOperationException("The files capture has no fixture root.");
        foreach (var path in new[] { source, collision, destination })
        {
            var relative = Path.GetRelativePath(root, path);
            if (
                Path.IsPathFullyQualified(relative)
                || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            )
                throw new InvalidOperationException(
                    "A files capture path is outside its disposable fixture."
                );
        }
        if (
            group.Length != 3
            || Model.Torrents.Count != 4
            || group.Any(torrent => !SamePath(torrent.SavePath, source))
            || !SamePath(outside.SavePath, collision)
        )
            throw new InvalidOperationException(
                "The files capture torrent scope does not match its disposable fixture."
            );
        var sourceFile = Path.Combine(source, "shared.bin");
        var collisionFile = Path.Combine(collision, "shared.bin");
        var movedFile = Path.Combine(destination, "shared.bin");
        var unrelated = Path.Combine(source, "keep.txt");
        var sourceHash = fixture.GetProperty("source_hash").GetString();
        var collisionHash = fixture.GetProperty("collision_hash").GetString();
        var unrelatedHash = fixture.GetProperty("unrelated_hash").GetString();
        CheckBytes(sourceFile, sourceHash);
        CheckBytes(collisionFile, collisionHash);
        CheckBytes(unrelated, unrelatedHash);
        await ShowTorrents();
        Model.CloseInspector();

        async Task Matrix(string name, FrameworkElement? content = null)
        {
            foreach (var language in new[] { "en", "es" })
            {
                Model.SelectLanguage(language);
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
                foreach (var theme in new[] { "light", "dark" })
                {
                    await Model.Settings.SelectTheme(theme);
                    await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                    foreach (
                        var size in new[]
                        {
                            new SizeInt32(720, 560),
                            new SizeInt32(1040, 680),
                            new SizeInt32(1280, 800),
                        }
                    )
                    {
                        var scale = Root.XamlRoot.RasterizationScale;
                        var minimum =
                            (
                                (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter
                            ).PreferredMinimumWidth
                            ?? 0;
                        AppWindow.Resize(
                            new SizeInt32(
                                Math.Max((int)(size.Width * scale), minimum),
                                (int)(size.Height * scale)
                            )
                        );
                        var prefix =
                            "files-"
                            + language
                            + "-"
                            + theme
                            + "-"
                            + size.Width
                            + "x"
                            + size.Height
                            + "-"
                            + name;
                        await CapturePage(prefix, content);
                        completed.Add(prefix);
                    }
                }
            }
        }

        async Task<(ContentDialog Dialog, Task Closed)> Open(Torrent[] torrents, FileAction action)
        {
            Torrents.Selection = new Syno.TableView.Selection(torrents, torrents[0]);
            await SelectTorrent();
            Run(action == FileAction.Move ? Model.MoveFiles : Model.DeleteFiles);
            // Delete opens its dialog only after the scope arrives.
            await CaptureReady(Model.FileDraft, () => !Model.FileDraft.IsPending);
            await CaptureLayout();
            var dialog =
                _interaction?.Dialog
                ?? throw new InvalidOperationException("The file command did not open its dialog.");
            var closed =
                _interaction?.Completion.Task
                ?? throw new InvalidOperationException("The file dialog has no completion.");
            if (Model.FileDraft.HasError)
                throw new InvalidOperationException(
                    "The file scope could not be read: " + Model.FileDraft.Message
                );
            return (dialog, closed);
        }

        void Choose(ContentDialog dialog, string path, bool existing)
        {
            Model.FileDraft.Destination = path;
            CaptureElements(dialog)
                .OfType<CheckBox>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "IncludeShared")
                .IsChecked = true;
            CaptureElements(dialog)
                .OfType<CheckBox>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "UseExisting")
                .IsChecked = existing;
        }

        var (dialog, closed) = await Open([group[0]], FileAction.Move);
        try
        {
            if (!Model.FileDraft.HasShared || Model.FileDraft.CanSubmit)
                throw new InvalidOperationException(
                    "The shared move did not require its outside owners."
                );
            await Matrix("move-shared", dialog.Content as FrameworkElement);
            Choose(dialog, collision, true);
            await CaptureLayout();
            if (!Model.FileDraft.CanSubmit || !dialog.IsPrimaryButtonEnabled)
                throw new InvalidOperationException(
                    "The complete shared scope cannot submit its move."
                );
            await Matrix("move-confirm", dialog.Content as FrameworkElement);
            CaptureInvoke(dialog);
            await CaptureReady(
                Model.FileDraft,
                () => Model.FileDraft.HasError || _interaction?.Dialog is null
            );
            if (
                _interaction?.Dialog is null
                || !SamePath(Model.FileDraft.Destination, collision)
                || !Model.FileDraft.IncludeShared
                || !Model.FileDraft.UseExisting
            )
                throw new InvalidOperationException(
                    "The destination ownership refusal lost the dialog choices."
                );
            await CaptureLayout();
            await CaptureUi("files-move-refused-post-submit");
            CheckBytes(sourceFile, sourceHash);
            CheckBytes(collisionFile, collisionHash);
            CheckBytes(unrelated, unrelatedHash);
            outcomes.Add(
                new
                {
                    journey = "destination owned by another torrent",
                    primaryInvoked = true,
                    dialogStayedOpen = true,
                    destinationRetained = true,
                    sharedChoiceRetained = true,
                    existingChoiceRetained = true,
                    message = Model.FileDraft.Message,
                    sourceHash,
                    collisionHash,
                }
            );
            await Matrix("move-refused");
        }
        finally
        {
            dialog.Hide();
            await closed;
        }

        if (ReviewMode == CaptureMode.FilesLayout)
        {
            outcomes.Add(
                new
                {
                    journey = "capture scope",
                    pickerExercised = false,
                    scope = "Layout correction captures and one native ownership refusal; no accepted move or deletion. All fixture membership and bytes remain.",
                }
            );
            return;
        }

        Torrents.Selection = new Syno.TableView.Selection([outside], outside);
        await SelectTorrent();
        Run(Model.Remove);
        await CaptureLayout();
        var removal =
            _interaction?.Dialog
            ?? throw new InvalidOperationException(
                "The outside owner removal confirmation did not open."
            );
        closed =
            _interaction?.Completion.Task
            ?? throw new InvalidOperationException("The removal confirmation has no completion.");
        try
        {
            CaptureInvoke(removal);
            await closed.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (_interaction?.Dialog is not null)
                removal.Hide();
        }
        await CaptureReady(Model, () => !Model.Torrents.Contains(outside));
        CheckBytes(collisionFile, collisionHash);

        (dialog, closed) = await Open([group[0]], FileAction.Move);
        try
        {
            Choose(dialog, collision, false);
            await CaptureLayout();
            CaptureInvoke(dialog);
            await closed.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (_interaction?.Dialog is not null)
            {
                dialog.Hide();
                await closed;
            }
        }
        await CaptureReady(
            Model,
            () =>
                group.All(torrent => !torrent.IsMoving && torrent.ErrorCode == "destination_exists")
        );
        CheckBytes(sourceFile, sourceHash);
        CheckBytes(collisionFile, collisionHash);
        CheckBytes(unrelated, unrelatedHash);
        if (group.Any(torrent => !SamePath(torrent.SavePath, source)))
            throw new InvalidOperationException(
                "The occupied-file collision changed the source location."
            );
        await Matrix("move-collision");
        (dialog, closed) = await Open([group[0]], FileAction.Move);
        try
        {
            outcomes.Add(
                new
                {
                    journey = "accepted move to an occupied file",
                    primaryInvoked = true,
                    rowError = group[0].ErrorCode,
                    sourceHash,
                    collisionHash,
                    chosenDestination = collision,
                    moveDestination = group[0].MoveDestination,
                    reopenedDestination = Model.FileDraft.Destination,
                    destinationRetained = SamePath(Model.FileDraft.Destination, collision),
                    scope = "Accepted asynchronous work closes the dialog; a cleared preflight marker requires choosing the folder again.",
                }
            );
            await CapturePage("files-collision-reopen", dialog.Content as FrameworkElement);
            Choose(dialog, destination, false);
            await CaptureLayout();
            CaptureInvoke(dialog);
            await closed.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (_interaction?.Dialog is not null)
            {
                dialog.Hide();
                await closed;
            }
        }
        await CaptureReady(
            Model,
            () =>
                group.All(torrent =>
                    !torrent.IsMoving
                    && torrent.MoveDestination.Length == 0
                    && SamePath(torrent.SavePath, destination)
                )
        );
        CheckBytes(movedFile, sourceHash);
        CheckBytes(collisionFile, collisionHash);
        CheckBytes(unrelated, unrelatedHash);
        if (File.Exists(sourceFile))
            throw new InvalidOperationException(
                "The completed move left its payload at the source."
            );
        outcomes.Add(
            new
            {
                journey = "normal shared move",
                primaryInvoked = true,
                destination,
                sourceHash,
                sourceRemoved = true,
                groupMoved = group.Length,
                unrelatedHash,
                collisionHash,
            }
        );
        await CapturePage("files-moved");

        (dialog, closed) = await Open([group[0]], FileAction.Delete);
        try
        {
            if (!Model.FileDraft.HasShared || dialog.DefaultButton != ContentDialogButton.Primary)
                throw new InvalidOperationException(
                    "The partial deletion omitted shared-file protection or Delete as default."
                );
            await Matrix("delete-shared-confirm", dialog.Content as FrameworkElement);
            CaptureInvoke(dialog);
            await closed.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (_interaction?.Dialog is not null)
            {
                dialog.Hide();
                await closed;
            }
        }
        await CaptureReady(Model, () => !Model.Torrents.Contains(group[0]));
        if (group.Skip(1).Any(torrent => !Model.Torrents.Contains(torrent)))
            throw new InvalidOperationException("The partial deletion removed an outside owner.");
        CheckBytes(movedFile, sourceHash);
        CheckBytes(collisionFile, collisionHash);
        CheckBytes(unrelated, unrelatedHash);
        outcomes.Add(
            new
            {
                journey = "delete one shared owner",
                primaryInvoked = true,
                removed = group[0].TorrentId,
                outsideOwnersRetained = 2,
                sharedBytesRetained = true,
                sourceHash,
                collisionHash,
                unrelatedHash,
            }
        );

        (dialog, closed) = await Open(group.Skip(1).ToArray(), FileAction.Delete);
        try
        {
            if (Model.FileDraft.HasShared || dialog.DefaultButton != ContentDialogButton.Primary)
                throw new InvalidOperationException(
                    "The full deletion scope or Delete as default is incorrect."
                );
            await Matrix("delete-group-confirm", dialog.Content as FrameworkElement);
            CaptureInvoke(dialog);
            await closed.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            if (_interaction?.Dialog is not null)
            {
                dialog.Hide();
                await closed;
            }
        }
        await CaptureReady(Model, () => Model.Torrents.Count == 0);
        var until = DateTime.UtcNow.AddSeconds(20);
        while (File.Exists(movedFile) && DateTime.UtcNow < until)
            await Task.Delay(100);
        if (File.Exists(movedFile))
            throw new InvalidOperationException(
                "The whole group deletion did not remove its payload."
            );
        CheckBytes(collisionFile, collisionHash);
        CheckBytes(unrelated, unrelatedHash);
        outcomes.Add(
            new
            {
                journey = "delete the remaining shared group",
                primaryInvoked = true,
                membershipRemoved = true,
                payloadRemoved = true,
                unrelatedHash,
                collisionHash,
            }
        );
        await CapturePage("files-deleted");
        outcomes.Add(
            new
            {
                journey = "capture scope",
                pickerExercised = false,
                scope = "Real file commands and native primary buttons; destination supplied to the existing owner because system pickers are outside offscreen XAML capture.",
            }
        );
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(
            first.TrimEnd(Path.DirectorySeparatorChar),
            second.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase
        );

    private static void CheckBytes(string path, string? expected)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The files capture bytes changed: " + path);
    }
}
