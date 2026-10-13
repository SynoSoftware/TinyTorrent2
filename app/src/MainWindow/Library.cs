using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;
using Windows.System;
using LibraryBrowser = Syno.TinyTorrent.Library.Browser;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private LibraryTable _libraryTable = null!;
    private FocusState _navigationInput;
    private int _navigationRevision;
    private bool _navigationHeld;
    private WindowPage _navigationFace = WindowPage.Torrents;
    private Storyboard? _navigationFlip;
    private CancellationTokenSource? _associationRead;
    private MenuFlyoutSubItem? _libraryFetch;

    private void ConfigureLibrary()
    {
        _libraryTable = new LibraryTable(Model.Library);
        Model.Library.CommandFailed += (_, error) => Model.Report(error);
        Model.Library.BatchLimited += (_, files) => Model.ShowSearchLimit(files);
        // A submenu has no command, so the menu bar's Fetch metadata follows
        // the command in place rather than by rebuilding, which would close it.
        Model.Library.FetchMetadata.CanExecuteChanged += (_, _) =>
        {
            if (_libraryFetch is { } fetch)
                fetch.IsEnabled = Model.Library.FetchMetadata.CanExecute(null);
        };
        LibraryContent.Content = _libraryTable;
        _libraryTable.ItemContextRequested += (_, args) =>
        {
            var menu = LibraryContextMenu();
            menu.ShowAt(
                args.Target,
                new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                {
                    Position = args.Position,
                }
            );
        };
        Model.Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LibraryBrowser.Selected))
                UpdateAssociation();
            if (
                Model.Page == WindowPage.Library
                && args.PropertyName is nameof(LibraryBrowser.Facets) or nameof(LibraryBrowser.Filters)
                    or nameof(LibraryBrowser.Configuration) or ""
            )
                RefreshLibraryFilters();
            if (
                Model.Page == WindowPage.Library
                && args.PropertyName
                    is nameof(LibraryBrowser.Origins)
                        or nameof(LibraryBrowser.Selection)
                        or nameof(LibraryBrowser.Selected)
                        or nameof(LibraryBrowser.IsAvailable)
                        or nameof(LibraryBrowser.IsEnrichmentEnabled)
            )
                RefreshLibraryMenu();
            if (
                Model.Page == WindowPage.Library
                && args.PropertyName == nameof(LibraryBrowser.Configuration)
            )
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
            Model.Library.Opened(row, path, await OpenPath(path));
        };
        Model.Library.FolderRequested += async (_, path) =>
        {
            var failure = await OpenPath(path);
            if (failure is not null)
                Model.Report(failure);
        };
        Model.Library.TorrentRequested += async (_, origin) =>
        {
            if (
                Model.Torrents.FirstOrDefault(torrent => torrent.TorrentId == origin.OriginId)
                is not { } torrent
            )
                return;
            if (!await Model.Navigation.Open(WindowPage.Torrents))
                return;
            if (!Model.VisibleTorrents.Contains(torrent))
                await Model.ClearFinding();
            Torrents.Focus(FocusState.Programmatic);
            if (!await SelectTorrent(new Syno.TableView.Selection([torrent], torrent)))
                return;
            Model.OpenInspector();
            (InspectorContent.Content as InspectorPane)?.RevealFile(origin.FileIndex);
        };
        Model.Library.SettingsRequested += async (_, _) =>
            await ShowSettings(new(SettingsCategory.Plugins, "video_information"));
        Model.LibraryRequested += async (_, _) =>
        {
            if (await Model.Navigation.Navigate(WindowPage.Library))
                FocusPage();
        };
        Model.Library.FetchMetadataRequested += async (_, source) =>
        {
            if (Model.Library.Selected is { } selected)
                await FetchMetadata(new MetadataDialog(Model.Library, selected.Entry, source));
        };
        Model.SubtitlesRequested += async (_, entries) =>
        {
            if (entries.Count == 1)
                await ChooseSubtitle(entries.Single());
            else
                await DownloadSubtitles(entries);
        };
        AddShortcut(
            new KeyboardAccelerator
            {
                Key = VirtualKey.Number1,
                Modifiers = VirtualKeyModifiers.Control,
            },
            Model.ShowTorrents
        );
        AddShortcut(
            new KeyboardAccelerator
            {
                Key = VirtualKey.Number2,
                Modifiers = VirtualKeyModifiers.Control,
            },
            Model.ShowLibrary
        );
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

    private async void OnPageSwitch(object sender, RoutedEventArgs args)
    {
        var revision = _navigationRevision;
        _navigationHeld = _navigationInput != FocusState.Unfocused;
        if (
            !await Model.Navigation.Navigate(
                NavigationPage == WindowPage.Library ? WindowPage.Torrents : WindowPage.Library
            )
            && revision == _navigationRevision
        )
            _navigationHeld = false;
        RefreshNavigation();
    }

    private WindowPage NavigationPage => Model.Navigation.Workspace;

    private void OnNavigationEnter(object sender, PointerRoutedEventArgs args)
    {
        _navigationInput = FocusState.Pointer;
        _navigationRevision++;
        _navigationHeld = false;
        RefreshNavigation();
    }

    private void OnNavigationLeave(object sender, PointerRoutedEventArgs args)
    {
        if (_navigationInput != FocusState.Pointer)
            return;
        _navigationInput = FocusState.Unfocused;
        _navigationRevision++;
        _navigationHeld = false;
        RefreshNavigation();
    }

    private void OnNavigationInput(object sender, RoutedEventArgs args)
    {
        if (AppIcon.FocusState == FocusState.Keyboard)
            _navigationInput = FocusState.Keyboard;
        else if (_navigationInput == FocusState.Keyboard)
            _navigationInput = FocusState.Unfocused;
        else
            return;
        _navigationRevision++;
        _navigationHeld = false;
        RefreshNavigation();
    }

    private void UpdateLibraryPage()
    {
        if (Model.Page != WindowPage.Torrents)
            (InspectorContent.Content as InspectorPane)?.CancelReveal();
        var library = Model.Page == WindowPage.Library;
        if (!library)
            _associationRead?.Cancel();
        else
            UpdateAssociation();
        if (Model.Page is WindowPage.Torrents or WindowPage.Library)
            _workspaces.Show(library ? LibraryContent : TorrentContent, library ? 1 : -1);
        Filters.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
        LibraryFilters.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        Search.PlaceholderText = Model.Text.Get(
            library ? "library" : "finding",
            library ? "search" : "placeholder"
        );
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
        var library = NavigationPage == WindowPage.Library;
        var destination = Model.Text.Get(
            library ? "window" : "library",
            library ? "torrents" : "title"
        );
        NameButton(
            AppIcon,
            Model.Text.Format(
                "shortcuts",
                "tip",
                Model.Text.Format("chrome", "switch_page", destination),
                ShortcutText(library ? Model.ShowTorrents : Model.ShowLibrary)
            )
        );
        var active = _navigationInput != FocusState.Unfocused;
        if (!active)
            _navigationHeld = false;
        var face =
            active && !_navigationHeld
                ? (library ? WindowPage.Torrents : WindowPage.Library)
                : NavigationPage;
        if (_navigationFace == face)
            return;
        _navigationFace = face;
        var rotation = NavigationProjection.RotationY;
        _navigationFlip?.Stop();
        _navigationFlip = null;
        NavigationProjection.RotationY = rotation;
        if (!_uiSettings.AnimationsEnabled)
        {
            NavigationProjection.RotationY = 0;
            ShowNavigationFace();
            return;
        }
        var shown =
            TorrentsFace.Visibility == Visibility.Visible
                ? WindowPage.Torrents
                : WindowPage.Library;
        if (shown == face)
        {
            FlipNavigation(rotation, 0);
            return;
        }
        var edge =
            rotation == 0 ? (face == WindowPage.Library ? 90 : -90) : Math.CopySign(90, rotation);
        FlipNavigation(
            rotation,
            edge,
            () =>
            {
                ShowNavigationFace();
                NavigationProjection.RotationY = -edge;
                FlipNavigation(-edge, 0);
            }
        );
    }

    private void ShowNavigationFace()
    {
        TorrentsFace.Visibility =
            _navigationFace == WindowPage.Torrents ? Visibility.Visible : Visibility.Collapsed;
        LibraryFace.Visibility =
            _navigationFace == WindowPage.Library ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FlipNavigation(double from, double to, Action? completed = null)
    {
        if (from == to)
        {
            completed?.Invoke();
            return;
        }
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(110 * Math.Abs(to - from) / 90)),
            EasingFunction = new CubicEase
            {
                EasingMode = to == 0 ? EasingMode.EaseOut : EasingMode.EaseIn,
            },
        };
        Storyboard.SetTarget(animation, NavigationProjection);
        Storyboard.SetTargetProperty(animation, nameof(NavigationProjection.RotationY));
        var flip = new Storyboard();
        flip.Children.Add(animation);
        flip.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_navigationFlip, flip))
                return;
            flip.Stop();
            NavigationProjection.RotationY = to;
            _navigationFlip = null;
            completed?.Invoke();
        };
        _navigationFlip = flip;
        flip.Begin();
    }

    private async void UpdateAssociation()
    {
        _associationRead?.Cancel();
        if (
            Model.Page != WindowPage.Library
            || Model.Library.Selected is not { } selected
            || !Model.IsConnected
        )
            return;
        var path = selected.Entry.Path;
        using var cancellation = new CancellationTokenSource();
        _associationRead = cancellation;
        try
        {
            var association = await ReadAssociation(Path.GetExtension(path), cancellation.Token);
            if (
                !cancellation.IsCancellationRequested
                && Model.IsConnected
                && ReferenceEquals(selected, Model.Library.Selected)
                && selected.Entry.Path == path
            )
                Model.Library.Associate(association);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine("File association: " + error.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(_associationRead, cancellation))
                _associationRead = null;
        }
    }

    private MenuFlyout LibraryContextMenu()
    {
        var menu = new MenuFlyout();
        menu.Closed += (_, _) => ClearMenu(menu.Items);
        menu.Items.Add(
            new MenuFlyoutItem
            {
                Text =
                    Model.Library.Selected?.Title
                    ?? Model.Text.Format("window", "selected", Model.Library.Selection.Items.Count),
                Icon = Icon(Lucide.ListChecks),
                IsEnabled = false,
            }
        );
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var item in LibraryMenu(MetadataActions.Create(Model.Library)))
            menu.Items.Add(item);
        return menu;
    }

    private IEnumerable<MenuFlyoutItemBase> LibraryMenu(MenuFlyoutSubItem fetch)
    {
        yield return Menu("open", Model.Library.Open, Syno.Lucide.Play);
        yield return Menu("open_folder", Model.Library.OpenFolder, Syno.Lucide.FolderOpen);
        yield return Menu("properties", Model.Library.Properties, Syno.Lucide.Info);
        yield return OriginActions.Create(Model.Library);
        yield return new MenuFlyoutSeparator();
        yield return fetch;
        yield return new MenuFlyoutItem
        {
            Text = Model.Text.Get(
                "subtitles",
                Model.Library.Selection.Items.Count == 1 ? "choose" : "download_title"
            ),
            Command = Model.DownloadSubtitles,
            Icon = Icon(Lucide.Download),
        };
        yield return new MenuFlyoutSeparator();
        yield return new MenuFlyoutItem
        {
            Text = Model.Text.Get("library", "erase_metadata"),
            Command = Model.Library.EraseMetadata,
            Icon = Icon(Lucide.Eraser),
        };
    }

    private void RefreshLibraryMenu()
    {
        TorrentMenu.Title = Model.Text.Get("library", "title");
        TorrentMenu.AccessKey = MenuKey("library");
        ClearMenu(TorrentMenu.Items);
        _libraryFetch = MetadataActions.Create(Model.Library);
        foreach (var item in LibraryMenu(_libraryFetch))
            TorrentMenu.Items.Add(item);
    }

    private void RefreshLibraryViewMenu()
    {
        ViewMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var configuration in Enum.GetValues<LibraryConfiguration>())
        {
            var choice = new RadioMenuFlyoutItem
            {
                Text = Model.Text.Get("library", configuration.ToString().ToLowerInvariant()),
                GroupName = "Library",
                IsChecked = Model.Library.Configuration == configuration,
                Icon = Icon(
                    configuration switch
                    {
                        LibraryConfiguration.Videos => Syno.Lucide.Film,
                        LibraryConfiguration.Music => Syno.Lucide.Music,
                        _ => Syno.Lucide.Files,
                    }
                ),
            };
            choice.Click += (_, _) => Model.Library.Configuration = configuration;
            ViewMenu.Items.Add(choice);
        }
    }

    // Asks for the person's own API key for option. Saving also turns
    // information on with option, because Settings opens this dialog when the
    // person chooses a provider that has no usable key.
    private Task ShowKey(ProviderOption option)
    {
        var library = Model.Library;
        return ShowAccount(
            new AccountDialog(
                Model.Text,
                option.Name,
                option.Access,
                option.HasBuiltInKey,
                new(string.Empty, string.Empty, library.HasKey(option.ProviderId)),
                async input =>
                {
                    // Saving a key restarts the provider's lookups, so an
                    // unchanged choice saves nothing. No typed key and no kept
                    // key means the built-in key.
                    var key = input.Secret.Length > 0 ? input.Secret : null;
                    if (key is not null || (!input.KeepSecret && library.HasKey(option.ProviderId)))
                        await library.SaveKey(option.ProviderId, key);
                    if (!library.IsEnrichmentEnabled || library.Option != option)
                        await library.Enable(option.ProviderId);
                }
            )
        );
    }

    private async Task FetchMetadata(MetadataDialog content)
    {
        SettingTarget? settings = null;
        await Interact(async interaction =>
        {
            interaction.Restore = () => FetchMetadata(content);
            interaction.ResolveDraft = () => SaveOnClose(interaction, content, canSave: true);
            var dialog = new Controls.Dialog
            {
                Content = content,
                Glyph = Lucide.Film,
                PrimaryGlyph = Lucide.Check,
            };
            dialog.Opened += async (_, _) =>
            {
                if (!content.HasDraft)
                    await content.Find();
            };
            void OpenSettings(object? sender, SettingTarget target)
            {
                settings = target;
                dialog.Hide();
            }
            content.SettingsRequested += OpenSettings;
            try
            {
                await ShowEditor(
                    interaction,
                    dialog,
                    content,
                    () =>
                    {
                        dialog.Title = Model.Text.Get("library", "fetch_metadata");
                        dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                        dialog.PrimaryToolTip = Model.Text.Get("library", "save_metadata_tip");
                        content.RefreshText();
                    }
                );
            }
            finally
            {
                content.SettingsRequested -= OpenSettings;
                content.Cancel();
            }
            return true;
        });
        // Settings opens after the dialog has closed, because navigation is
        // refused while a dialog is open.
        if (settings is { } target)
            await ShowSettings(target);
    }

    private void RefreshLibraryFilters()
    {
        LibraryFilters.IsEnabled = Model.Library.RowsConfiguration == Model.Library.Configuration;
        if (!Model.IsFilterOpen || !LibraryFilters.IsEnabled)
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
                    choices = new ComboBox
                    {
                        Tag = section.Key,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                        choices,
                        "LibraryFilter-" + section.Key
                    );
                    choices.Items.Add(new ComboBoxItem());
                    choices.SelectionChanged += (_, _) =>
                    {
                        if (!_refreshingFilters && LibraryFilters.IsEnabled)
                            Model.Library.Filter(
                                section.Key,
                                (choices.SelectedItem as ComboBoxItem)?.Tag as string
                            );
                    };
                }
                choices.Header = Model.Text.Get("library", section.Key);
                var existing = choices
                    .Items.OfType<ComboBoxItem>()
                    .Where(item => item.Tag is string)
                    .ToDictionary(item => (string)item.Tag);
                var all = (ComboBoxItem)choices.Items[0];
                all.Content = Model.Text.Get("filters", "all");
                var items = new List<ComboBoxItem> { all };
                var selected = all;
                foreach (var facet in section)
                {
                    if (!existing.TryGetValue(facet.Value, out var item))
                        item = new ComboBoxItem { Tag = facet.Value };
                    var label = facet.Section == "series"
                        ? facet.Label ?? Model.Text.Get("library", "series")
                        : Model.Library.FilterText(facet.Section, facet.Value);
                    item.Content = Model.Text.Format("filters", "count", label, facet.Count);
                    items.Add(item);
                    if (Model.Library.Filters.GetValueOrDefault(section.Key) == facet.Value)
                        selected = item;
                }
                // WinUI throws ArgumentException when an item it still has
                // selected is removed, which happens when a filter is cleared
                // outside this box and its value has no files left.
                if (!choices.Items.SequenceEqual(items))
                    choices.SelectedItem = null;
                for (var index = 0; index < items.Count; index++)
                {
                    if (
                        index < choices.Items.Count
                        && ReferenceEquals(choices.Items[index], items[index])
                    )
                        continue;
                    choices.Items.Remove(items[index]);
                    choices.Items.Insert(index, items[index]);
                }
                while (choices.Items.Count > items.Count)
                    choices.Items.RemoveAt(choices.Items.Count - 1);
                choices.SelectedItem = selected;
                if (
                    position >= panel.Children.Count
                    || !ReferenceEquals(panel.Children[position], choices)
                )
                {
                    panel.Children.Remove(choices);
                    panel.Children.Insert(position, choices);
                }
                position++;
            }
            while (panel.Children.Count > position)
                panel.Children.RemoveAt(panel.Children.Count - 1);
        }
        finally
        {
            _refreshingFilters = false;
        }
    }
}
