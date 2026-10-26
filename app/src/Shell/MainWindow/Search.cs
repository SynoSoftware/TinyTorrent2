using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Search;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using SearchInspector = Syno.TinyTorrent.Search.InspectorPane;
using SearchWorkspace = Syno.TinyTorrent.Search.Workspace;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private SearchWorkspace? _catalogueView;
    private SearchInspector? _catalogueInspector;
    private FiltersView? _catalogueFilters;
    private readonly Queue<DownloadChoice> _catalogueDownloads = [];
    private bool _preparingCatalogue;

    private void ConfigureSearch()
    {
        Model.SearchRequested += async (_, _) =>
        {
            if (await Model.Navigation.Navigate(WindowPage.Search))
                FocusRows(FocusState.Keyboard);
        };
        Model.Search.PropertyChanged += (_, args) =>
        {
            if (Model.Page == WindowPage.Search)
            {
                QueueStatus();
                QueueMenus();
            }
        };
        Model.Search.CopyRequested += (_, value) =>
        {
            try
            {
                var content = new DataPackage();
                content.SetText(value);
                Clipboard.SetContent(content);
            }
            catch (Exception error) { Model.Notifications.Report(error); }
        };
        Model.Search.Failed += (_, error) => Model.Notifications.Report(error);
        Model.Search.DownloadRequested += (_, choice) =>
        {
            _catalogueDownloads.Enqueue(choice);
            _ = ReceiveCatalogue();
        };
        AddShortcut(new KeyboardAccelerator
        {
            Key = VirtualKey.Number3,
            Modifiers = VirtualKeyModifiers.Control,
        }, Model.ShowSearch);
    }

    private void EnsureSearch()
    {
        if (_catalogueView is not null)
            return;
        _catalogueView = new SearchWorkspace(Model.Search, Model.Settings.Indexing);
        _catalogueView.ClearRequested += async (_, _) => await ShowIndexing(clear: true);
        _catalogueView.RebuildRequested += async (_, _) => await ConfirmIndexRebuild();
        _catalogueView.FolderRequested += async (_, folder) =>
        {
            var error = await OpenPath(folder);
            if (error is not null)
                Model.Notifications.Report(error);
        };
        CatalogueContent.Content = _catalogueView;
        _catalogueFilters = new FiltersView(Model.Search);
        CatalogueFilters.Content = _catalogueFilters;
    }

    private async Task ReceiveCatalogue()
    {
        if (_preparingCatalogue)
            return;
        _preparingCatalogue = true;
        try
        {
            while (_catalogueDownloads.TryDequeue(out var choice))
            {
                if (Model.IsClosing || !Model.CanEdit)
                    break;
                Task<System.Text.Json.JsonElement> Prepare(string destination) =>
                    Model.Catalogue.PrepareAsync(new()
                    {
                        Source = choice.Source,
                        Scope = choice.Scope,
                        WholeTorrent = choice.WholeTorrent || choice.Choices is not null,
                        Files = choice.Files,
                        Destination = destination,
                        DefaultPriority = choice.DefaultPriority,
                        Choices = choice.Choices,
                    });
                var destination = Model.AddDraft.Destination;
                var prepared = await Prepare(destination);
                if (Model.IsClosing)
                    break;
                var arrival = await Model.AddDraft.ReceiveCatalogue(prepared, choice.WholeTorrent, Prepare, destination);
                if (arrival == ArrivalOutcome.Dialog)
                    await ShowAdd();
                else if (arrival == ArrivalOutcome.Merge)
                    await ConfirmMerge();
            }
        }
        catch (Exception error)
        {
            if (!Model.IsClosing)
                Model.Notifications.Report(error);
        }
        finally
        {
            _catalogueDownloads.Clear();
            _preparingCatalogue = false;
        }
    }

    private Task ShowIndexing(bool clear = false) => clear
        ? ShowIndexingTask(new IndexingTask(Model, IndexingPage.Removal, clear: true))
        : ShowSettings(new(SettingsCategory.Indexing));

    private async Task ShowIndexingTask(IndexingTask content)
    {
        using (content)
            await Interact(async interaction =>
            {
                await ShowEditor(interaction, content.Dialog, content, content.RefreshText);
                if (_settingsPage is { } settings)
                    await settings.RefreshIndexing();
                else await Model.Search.RefreshStatus();
                return true;
            });
    }

    private async Task ConfirmIndexRebuild()
    {
        await Interact(async interaction =>
        {
            var dialog = new Dialog { Glyph = Syno.Lucide.RefreshCw, PrimaryGlyph = Syno.Lucide.RefreshCw };
            var choice = await ShowDialog(interaction, dialog, () =>
            {
                dialog.Title = Model.Text.Get("catalogue", "rebuild_title");
                dialog.Content = Model.Text.Get("catalogue", "rebuild_explanation");
                dialog.PrimaryButtonText = Model.Text.Get("catalogue", "rebuild");
            });
            if (choice == ContentDialogResult.Primary)
                await Model.Search.Rebuild();
            return true;
        });
    }

    private IReadOnlyList<MenuEntry> SearchMenu() =>
    [
        new MenuCommand(Model.Text.Get("catalogue", "download_selected"), Lucide.Download, Model.Search.DownloadSelected),
        new MenuCommand(Model.Text.Get("catalogue", "download_torrent"), Lucide.Package, Model.Search.DownloadWhole),
        new MenuCommand(Model.Text.Get("catalogue", "refresh"), Lucide.RefreshCw, Model.Search.Refresh),
        new MenuSeparator(),
        new MenuCommand(Model.Text.Get("catalogue", "settings"), Lucide.Settings,
            new RelayCommand(() => ShowIndexing(), () => !Model.IsClosing)),
    ];
}
