using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;
using Syno.TinyTorrent.Library;
using LibraryBrowser = Syno.TinyTorrent.Library.Browser;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private LibraryTable _libraryTable = null!;
    private WindowPage _returnPage;
    private bool _fromLibrary;
    private bool _selectingPage;
    private CancellationTokenSource? _associationRead;

    private void ConfigureLibrary()
    {
        _libraryTable = new LibraryTable(Model.Library);
        Model.Library.CommandFailed += (_, error) => Model.Report(error);
        LibraryContent.Content = _libraryTable;
        _libraryTable.ItemContextRequested += (_, args) =>
        {
            var menu = new MenuFlyout();
            menu.Closed += (_, _) => ClearMenu(menu.Items);
            foreach (var item in LibraryMenu())
                menu.Items.Add(item);
            menu.ShowAt(args.Target, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = args.Position });
        };
        Model.Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LibraryBrowser.Selected))
                UpdateAssociation();
            if (Model.Page == WindowPage.Library && args.PropertyName is nameof(LibraryBrowser.Facets) or "")
                RefreshLibraryFilters();
            if (Model.Page == WindowPage.Library && args.PropertyName is nameof(LibraryBrowser.Origins) or
                nameof(LibraryBrowser.Selected) or nameof(LibraryBrowser.IdentifyLabel))
                RefreshLibraryMenu();
            if (Model.Page == WindowPage.Library && args.PropertyName == nameof(LibraryBrowser.Configuration))
                RefreshMenus();
        };
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsClosing) && Model.IsClosing)
                _associationRead?.Cancel();
        };
        Model.Library.OpenRequested += async (_, row) =>
        {
            var path = row.Entry.Path;
            var failure = await OpenPath(path);
            Model.Library.Opened(row, path, failure);
            if (failure is not null)
                Model.Report(failure);
        };
        Model.Library.FolderRequested += async (_, path) =>
        {
            var failure = await OpenPath(path);
            if (failure is not null)
                Model.Report(failure);
        };
        Model.Library.TorrentRequested += async (_, origin) =>
        {
            if (Model.Torrents.FirstOrDefault(torrent => torrent.TorrentId == origin) is not { } torrent)
                return;
            if (!await ShowTorrents())
                return;
            _fromLibrary = true;
            _returnPage = WindowPage.Library;
            await SelectTorrent(new Syno.TableView.Selection([torrent], torrent));
            Model.OpenInspector();
            UpdatePage();
        };
        Model.Library.EnableRequested += async (_, _) => await EnableVideoInformation();
        Model.LibraryRequested += async (_, _) => await Navigate(WindowPage.Library);
        Model.Library.IdentifyRequested += async (_, _) => await IdentifyVideo();
        Model.Library.ConfigureRequested += async (_, _) => await ConfigureVideo();
        Activated += (_, args) => Model.Library.ActivateInformation(args.WindowActivationState != WindowActivationState.Deactivated);
        AddShortcut(new KeyboardAccelerator { Key = VirtualKey.Number1, Modifiers = VirtualKeyModifiers.Control },
            Model.ShowTorrents);
        AddShortcut(new KeyboardAccelerator { Key = VirtualKey.Number2, Modifiers = VirtualKeyModifiers.Control },
            Model.ShowLibrary);
        Pages.SizeChanged += (_, _) => UpdateChrome();
        Search.KeyDown += (_, args) =>
        {
            if (Model.Page != WindowPage.Library)
                return;
            if (args.Key == VirtualKey.Escape)
            {
                Model.Query = string.Empty;
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.Down)
            {
                _libraryTable.FocusRows();
                args.Handled = true;
            }
        };
    }

    private async void OnPageSelected(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_selectingPage || _libraryTable is null || sender.SelectedItem is null)
            return;
        _fromLibrary = false;
        await Navigate(ReferenceEquals(sender.SelectedItem, LibraryPage) ? WindowPage.Library : WindowPage.Torrents);
        UpdateLibraryPage();
    }

    private void UpdateLibraryPage()
    {
        var library = Model.Page == WindowPage.Library;
        if (!library)
            _associationRead?.Cancel();
        else
            UpdateAssociation();
        _selectingPage = true;
        Pages.SelectedItem = library ? LibraryPage : TorrentsPage;
        _selectingPage = false;
        Torrents.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
        LibraryContent.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        Filters.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
        LibraryFilters.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = !library && Model.IsToolbarOpen ? Visibility.Visible : Visibility.Collapsed;
        Search.PlaceholderText = Model.Text.Get(library ? "library" : "finding", library ? "search" : "placeholder");
        Search.IsSuggestionListOpen = false;
        Search.ItemsSource = null;
        Search.Text = Model.Query;
        RefreshNavigation();
        RefreshMenus();
        if (library)
            RefreshLibraryFilters();
        UpdateInspectorSize();
        UpdateChrome();
    }

    private void RefreshNavigation()
    {
        TorrentsPage.Text = Model.Text.Get("window", "torrents");
        LibraryPage.Text = Model.Text.Get("library", "title");
        ToolTipService.SetToolTip(TorrentsPage, Model.Text.Format("shortcuts", "tip", TorrentsPage.Text, ShortcutText(Model.ShowTorrents)));
        ToolTipService.SetToolTip(LibraryPage, Model.Text.Format("shortcuts", "tip", LibraryPage.Text, ShortcutText(Model.ShowLibrary)));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TorrentsPage, TorrentsPage.Text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LibraryPage, LibraryPage.Text);
    }

    private async void UpdateAssociation()
    {
        _associationRead?.Cancel();
        if (Model.Page != WindowPage.Library || Model.Library.Selected is not { } selected || !Model.IsConnected)
            return;
        var path = selected.Entry.Path;
        using var cancellation = new CancellationTokenSource();
        _associationRead = cancellation;
        try
        {
            var association = await ReadAssociation(Path.GetExtension(path), cancellation.Token);
            if (!cancellation.IsCancellationRequested && Model.IsConnected &&
                ReferenceEquals(selected, Model.Library.Selected) && selected.Entry.Path == path)
                Model.Library.Associate(association);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine("File association: " + error.GetType().Name); }
        finally
        {
            if (ReferenceEquals(_associationRead, cancellation))
                _associationRead = null;
        }
    }

    private IEnumerable<MenuFlyoutItemBase> LibraryMenu()
    {
        yield return Menu("open", Model.Library.Open, Syno.Lucide.Play);
        yield return Menu("open_folder", Model.Library.OpenFolder, Syno.Lucide.FolderOpen);
        yield return Menu("properties", Model.Library.Properties, Syno.Lucide.Info);
        yield return new MenuFlyoutSeparator();
        yield return new MenuFlyoutItem { Text = Model.Library.IdentifyLabel, Command = Model.Library.Identify, Icon = Icon(Model.Library.IdentifyGlyph) };
        yield return new MenuFlyoutItem
        {
            Text = Model.Text.Get("library", "clear_identification"),
            Command = Model.Library.ClearIdentification,
            Icon = Icon(Lucide.Eraser),
        };
        var torrents = new MenuFlyoutSubItem { Text = Model.Text.Get("library", "show_torrents") };
        foreach (var origin in Model.Library.Origins)
        {
            var item = new MenuFlyoutItem { Text = origin.Name, IsEnabled = Model.Library.IsAvailable };
            item.Click += (_, _) => Model.Library.ShowTorrent(origin.OriginId);
            torrents.Items.Add(item);
        }
        torrents.IsEnabled = torrents.Items.Count > 0;
        yield return torrents;
    }

    private void RefreshLibraryMenu()
    {
        TorrentMenu.Title = Model.Text.Get("library", "title");
        ClearMenu(TorrentMenu.Items);
        foreach (var item in LibraryMenu())
            TorrentMenu.Items.Add(item);
    }

    private void RefreshLibraryViewMenu()
    {
        foreach (var configuration in Enum.GetValues<LibraryConfiguration>())
        {
            var choice = new ToggleMenuFlyoutItem
            {
                Text = Model.Text.Get("library", configuration.ToString().ToLowerInvariant()),
                IsChecked = Model.Library.Configuration == configuration,
            };
            choice.Click += (_, _) =>
            {
                choice.IsChecked = true;
                Model.Library.Configuration = configuration;
            };
            ViewMenu.Items.Add(choice);
        }
        ViewMenu.Items.Add(new MenuFlyoutSeparator());
        ViewMenu.Items.Add(Toggle(() => Model.Text.Get("filters", "title"), "filters",
            Model.SwitchFilters, () => Model.IsFilterOpen, Syno.Lucide.Funnel));
    }

    private async Task EnableVideoInformation()
    {
        if (Model.Library.IsEnrichmentEnabled)
        {
            try { await Model.Library.SetEnabled(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { Model.Library.ReportCommand(error); }
            return;
        }
        if (Model.Library.Option is not { } option)
            return;
        await Interact(async interaction =>
        {
            var dialog = new Controls.Dialog { Glyph = Lucide.Film, PrimaryGlyph = Lucide.Check };
            var choice = await ShowDialog(interaction, dialog, () =>
            {
                dialog.Title = Model.Text.Get("library", "video_information");
                dialog.Content = Model.Text.Get(option.TextSection, "consent");
                dialog.PrimaryButtonText = Model.Text.Get("library", "enable").TrimEnd('…');
                dialog.PrimaryToolTip = Model.Text.Get("library", "enable_tip");
            });
            if (choice == ContentDialogResult.Primary)
                await Model.Library.SetEnabled(true);
            return true;
        });
    }

    private async Task IdentifyVideo(IdentificationDialog? content = null)
    {
        if (Model.Library.Selected is not { } selected)
            return;
        content ??= new IdentificationDialog(Model.Library, selected.Entry);
        await Interact(async interaction =>
        {
            interaction.Restore = () => IdentifyVideo(content);
            interaction.ResolveDraft = () => SaveOnClose(interaction, content, canSave: true);
            var dialog = new Controls.Dialog { Content = content, Glyph = Lucide.Film, PrimaryGlyph = Lucide.Check };
            dialog.Opened += async (_, _) =>
            {
                if (!content.HasDraft)
                    await content.Find();
            };
            try
            {
                await ShowEditor(interaction, dialog, content, () =>
                {
                    dialog.Title = Model.Text.Get("library", content.IsCorrection ? "edit" : "identify");
                    dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                    dialog.PrimaryToolTip = Model.Text.Get("library", "identify_tip");
                    content.RefreshText();
                });
            }
            finally { content.Cancel(); }
            return true;
        });
    }

    private Task<bool> ConfigureVideo(VideoEditor? editor = null, string? section = null)
    {
        section ??= Model.Library.Option?.TextSection;
        editor ??= Model.Library.CreateEditor();
        if (editor is null || section is null)
            return Task.FromResult(false);
        return Interact(async interaction =>
        {
            interaction.Restore = () => ConfigureVideo(editor, section);
            interaction.ResolveDraft = () => SaveOnClose(interaction, editor.Draft, canSave: true);
            var dialog = new Controls.Dialog { Content = editor.Content, Glyph = Lucide.Globe, PrimaryGlyph = Lucide.Check };
            await ShowEditor(interaction, dialog, editor.Draft, () =>
            {
                dialog.Title = Model.Text.Get(section, "configure_title");
                dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                dialog.PrimaryToolTip = Model.Text.Get(section, "save_hint");
                editor.RefreshText();
            });
            return true;
        });
    }

    private void RefreshLibraryFilters()
    {
        if (!Model.IsFilterOpen)
            return;
        if (LibraryFilters.Content is not StackPanel panel)
        {
            panel = new StackPanel { Spacing = 12 };
            LibraryFilters.Content = panel;
        }
        var controls = panel.Children.OfType<ComboBox>().ToDictionary(choice => (string)choice.Tag);
        _refreshingFilters = true;
        try
        {
            var position = 0;
            foreach (var section in Model.Library.Facets.GroupBy(facet => facet.Section))
            {
                if (!controls.TryGetValue(section.Key, out var choices))
                {
                    choices = new ComboBox { Tag = section.Key, HorizontalAlignment = HorizontalAlignment.Stretch };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(choices, "LibraryFilter-" + section.Key);
                    choices.Items.Add(new ComboBoxItem());
                    choices.SelectionChanged += (_, _) =>
                    {
                        if (!_refreshingFilters)
                            Model.Library.Filter(section.Key, (choices.SelectedItem as ComboBoxItem)?.Tag as string);
                    };
                }
                choices.Header = Model.Text.Get("library", section.Key);
                var existing = choices.Items.OfType<ComboBoxItem>().Where(item => item.Tag is string)
                    .ToDictionary(item => (string)item.Tag);
                var all = (ComboBoxItem)choices.Items[0];
                all.Content = Model.Text.Get("filters", "all");
                var items = new List<ComboBoxItem> { all };
                var selected = all;
                foreach (var facet in section)
                {
                    if (!existing.TryGetValue(facet.Value, out var item))
                        item = new ComboBoxItem { Tag = facet.Value };
                    var label = Model.Library.FilterText(facet.Section, facet.Value);
                    item.Content = Model.Text.Format("filters", "count", label, facet.Count);
                    items.Add(item);
                    if (Model.Library.Filters.GetValueOrDefault(section.Key) == facet.Value)
                        selected = item;
                }
                for (var index = 0; index < items.Count; index++)
                {
                    if (index < choices.Items.Count && ReferenceEquals(choices.Items[index], items[index]))
                        continue;
                    choices.Items.Remove(items[index]);
                    choices.Items.Insert(index, items[index]);
                }
                while (choices.Items.Count > items.Count)
                    choices.Items.RemoveAt(choices.Items.Count - 1);
                choices.SelectedItem = selected;
                if (position >= panel.Children.Count || !ReferenceEquals(panel.Children[position], choices))
                {
                    panel.Children.Remove(choices);
                    panel.Children.Insert(position, choices);
                }
                position++;
            }
            while (panel.Children.Count > position)
                panel.Children.RemoveAt(panel.Children.Count - 1);
        }
        finally { _refreshingFilters = false; }
    }
}
