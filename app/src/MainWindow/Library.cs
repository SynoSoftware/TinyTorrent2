using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;
using Windows.System;
using LibraryBrowser = Syno.TinyTorrent.Library.Browser;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private LibraryTable? _libraryTable;
    private CancellationTokenSource? _associationRead;
    private Storyboard? _navigationRelease;

    private void ConfigureLibrary()
    {
        AppIcon.Loaded += WatchNavigationPress;
        Model.Library.ShowsFacets = Model.IsFilterOpen;
        Model.Library.CommandFailed += (_, error) => Model.Report(error);
        Model.Library.BatchLimited += (_, files) => Model.ShowSearchLimit(files);
        // A submenu has no command, so the menu bar's Fetch metadata follows it here.
        Model.Library.FetchMetadata.CanExecuteChanged += (_, _) =>
        {
            if (Model.Page == WindowPage.Library)
                QueueMenus();
        };
        Model.Library.PropertyChanged += (_, args) =>
        {
            QueueStatus();
            if (args.PropertyName == nameof(LibraryBrowser.Selected))
                UpdateAssociation();
            if (
                Model.Page == WindowPage.Library
                && args.PropertyName
                    is nameof(LibraryBrowser.Facets)
                        or nameof(LibraryBrowser.Filters)
                        or nameof(LibraryBrowser.Configuration)
                        or ""
            )
                RefreshLibraryFilters();
            if (
                Model.Page == WindowPage.Library
                && args.PropertyName
                    is nameof(LibraryBrowser.Origins)
                        or nameof(LibraryBrowser.Selection)
                        or nameof(LibraryBrowser.Selected)
                        or nameof(LibraryBrowser.IsAvailable)
                        or nameof(LibraryBrowser.CanShowTorrent)
                        or nameof(LibraryBrowser.Providers)
                        or nameof(LibraryBrowser.Configuration)
            )
                QueueMenus();
        };
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsFilterOpen))
                Model.Library.ShowsFacets = Model.IsFilterOpen;
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
            await ShowSettings(new(SettingsCategory.Plugins, "video_provider"));
        Model.LibraryRequested += async (_, _) =>
        {
            if (await Model.Navigation.Navigate(WindowPage.Library))
                FocusPage();
        };
        Model.Library.FetchMetadataRequested += async (_, source) =>
        {
            if (Model.Library.Selection.Items.Count == 1 && Model.Library.Selected is { } selected)
                await FetchMetadata(new MetadataDialog(Model.Library, selected.Entry, source));
            else
                await FetchBatch(source);
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
                _libraryTable?.FocusRows();
                args.Handled = true;
            }
        };
    }

    private WindowPage NavigationPage => Model.Navigation.Workspace;

    // The switch's own Pressed visual state drives this feedback, so it starts
    // and ends exactly when the button shows itself pressed. Its colour icons
    // ignore the template's pressed foreground, so the content dims instead.
    private void WatchNavigationPress(object sender, RoutedEventArgs args)
    {
        AppIcon.Loaded -= WatchNavigationPress;
        if (VisualTreeHelper.GetChild(AppIcon, 0) is not FrameworkElement root)
            return;
        var states = VisualStateManager
            .GetVisualStateGroups(root)
            .FirstOrDefault(group => group.Name == "CommonStates");
        if (states is not null)
            states.CurrentStateChanged += (_, change) =>
                ShowNavigationPress(change.NewState?.Name == "Pressed");
    }

    private void ShowNavigationPress(bool pressed)
    {
        _navigationRelease?.Stop();
        _navigationRelease = null;
        NavigationContent.Opacity = pressed ? 0.55 : 1;
        var from = NavigationPress.ScaleX;
        var to = pressed && _uiSettings.AnimationsEnabled ? 0.8 : 1;
        NavigationPress.ScaleX = to;
        NavigationPress.ScaleY = to;
        if (pressed || from == to || !_uiSettings.AnimationsEnabled)
            return;
        var release = new Storyboard();
        string[] properties = [nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY)];
        foreach (var property in properties)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
            };
            Storyboard.SetTarget(animation, NavigationPress);
            Storyboard.SetTargetProperty(animation, property);
            release.Children.Add(animation);
        }
        _navigationRelease = release;
        release.Begin();
    }

    private void UpdateLibraryPage()
    {
        if (Model.Page != WindowPage.Torrents)
            (InspectorContent.Content as InspectorPane)?.CancelReveal();
        var library = Model.Page == WindowPage.Library;
        if (library && _libraryTable is null)
        {
            _libraryTable = new LibraryTable(Model.Library);
            if (_placement?.LibraryLayouts is { } layouts)
                _libraryTable.RestoreLayouts(layouts);
            _libraryTable.ItemContextRequested += (_, args) =>
                LibraryContextMenu()
                    .ShowAt(
                        args.Target,
                        new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                        {
                            Position = args.Position,
                        }
                    );
            LibraryContent.Content = _libraryTable;
        }
        if (!library)
            _associationRead?.Cancel();
        else
            UpdateAssociation();
        Workspace.OpenPaneLength = library ? 280 : 196;
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
        if (Model.Page is WindowPage.Torrents or WindowPage.Library)
        {
            _workspaces.Show(library ? LibraryContent : TorrentContent, forward: library);
            _filters.Show(library ? LibraryFilters : Filters, forward: library);
        }
    }

    private void RefreshNavigation()
    {
        var library = NavigationPage == WindowPage.Library;
        var torrentsName = Model.Text.Get("window", "torrents");
        var libraryName = Model.Text.Get("library", "title");
        var command = library ? Model.ShowTorrents : Model.ShowLibrary;
        AppIcon.Command = command;
        NameButton(
            AppIcon,
            Model.Text.Format(
                "shortcuts",
                "tip",
                Model.Text.Format("chrome", "switch_page", library ? torrentsName : libraryName),
                ShortcutText(command)
            )
        );
        TorrentsName.Text = torrentsName;
        LibraryName.Text = libraryName;
        TorrentsFace.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
        LibraryFace.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
        TorrentsName.Opacity = library ? 0 : 1;
        LibraryName.Opacity = library ? 1 : 0;
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
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Library file association read cancelled.");
        }
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

    private MenuFlyout LibraryContextMenu() =>
        MenuItems.Flyout(
            () =>
                [
                    new MenuHeading(
                        Model.Library.Selected?.Title
                            ?? Model.Text.Format(
                                "window",
                                "selected",
                                Model.Library.Selection.Items.Count
                            ),
                        Syno.Lucide.ListChecks
                    ),
                    .. LibraryMenu(),
                ],
            () => Model.Text
        );

    private IEnumerable<MenuEntry> LibraryMenu()
    {
        yield return Command("open", Model.Library.Open, Syno.Lucide.Play);
        yield return Command("open_folder", Model.Library.OpenFolder, Syno.Lucide.FolderOpen);
        yield return Command("properties", Model.Library.Properties, Syno.Lucide.Info);
        yield return OriginActions.Create(Model.Library);
        yield return new MenuSeparator();
        yield return MetadataActions.Create(Model.Library);
        yield return new MenuCommand(
            Model.Text.Get("library", "refresh_metadata"),
            Lucide.RefreshCw,
            Model.Library.RefreshMetadata
        );
        yield return new MenuCommand(
            Model.Text.Get(
                "subtitles",
                Model.Library.Selection.Items.Count == 1 ? "choose" : "download_title"
            ),
            Lucide.Download,
            Model.DownloadSubtitles
        );
        yield return new MenuSeparator();
        yield return new MenuCommand(
            Model.Text.Get("library", "erase_metadata"),
            Lucide.Eraser,
            Model.Library.EraseMetadata
        );
    }

    private IEnumerable<MenuEntry> LibraryViewMenu()
    {
        yield return new MenuSeparator();
        foreach (var configuration in Enum.GetValues<LibraryConfiguration>())
            yield return new MenuChoice(
                Model.Text.Get("library", configuration.ToString().ToLowerInvariant()),
                configuration switch
                {
                    LibraryConfiguration.Videos => Syno.Lucide.Film,
                    LibraryConfiguration.Music => Syno.Lucide.Music,
                    _ => Syno.Lucide.Files,
                },
                Model.Library.Configuration == configuration,
                new RelayCommand(() => Model.Library.Configuration = configuration, () => true)
            );
    }

    private Task ShowKey(ProviderOption option, bool enableAutomaticDownload = false)
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
                    if (enableAutomaticDownload)
                        await library.EnableAutomaticDownload(option.ProviderId);
                }
            )
        );
    }

    private async Task FetchBatch(ProviderOption source)
    {
        try
        {
            var entries = Model
                .Library.Selection.Items.Cast<LibraryRow>()
                .Select(row => row.Entry)
                .ToArray();
            var plan = await Model.Library.Plan(entries, source, CancellationToken.None);
            var videos = entries
                .Where(entry => entry.Kind == FileKind.Video)
                .DistinctBy(entry => entry.EntryId)
                .Count();
            var content = new Library.BatchDialog(Model.Library, source, plan, videos);
            await Interact(async interaction =>
            {
                var dialog = new Controls.Dialog
                {
                    Content = content,
                    Glyph = Lucide.Film,
                    PrimaryGlyph = Lucide.Download,
                };
                await ShowEditor(
                    interaction,
                    dialog,
                    content,
                    () =>
                    {
                        dialog.Title = Model.Text.Get("library", "fetch_metadata");
                        dialog.PrimaryButtonText = Model.Text.Get("library", "fetch");
                        content.RefreshText();
                    }
                );
                return true;
            });
        }
        catch (Exception error)
        {
            Model.Report(error);
        }
    }

    private async Task FetchMetadata(MetadataDialog content)
    {
        SettingTarget? settings = null;
        await Interact(async interaction =>
        {
            interaction.Restore = () => FetchMetadata(content);
            interaction.ResolveDraft = () =>
                SaveOnClose(interaction, content, canSave: !content.RequiresForce);
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
            void UpdateSubmit(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                dialog.PrimaryButtonText = content.SubmitText;
                dialog.PrimaryToolTip = content.SubmitTip;
            }
            content.PropertyChanged += UpdateSubmit;
            try
            {
                await ShowEditor(
                    interaction,
                    dialog,
                    content,
                    () =>
                    {
                        dialog.Title = Model.Text.Get("library", "fetch_metadata");
                        dialog.PrimaryButtonText = content.SubmitText;
                        dialog.PrimaryToolTip = content.SubmitTip;
                        content.RefreshText();
                    },
                    async () =>
                    {
                        if (!await content.Submit())
                            return false;
                        Model.Announce(Model.Text.Get("library", "metadata_saved"));
                        return true;
                    }
                );
            }
            finally
            {
                content.SettingsRequested -= OpenSettings;
                content.PropertyChanged -= UpdateSubmit;
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
        LibraryFilters.IsEnabled = Model.Library.FacetsConfiguration == Model.Library.Configuration;
        if (!Model.IsFilterOpen || !LibraryFilters.IsEnabled)
            return;
        if (LibraryFilters.Content is not StackPanel panel)
        {
            panel = new StackPanel { Spacing = 12 };
            LibraryFilters.Content = panel;
        }
        var rows = panel.Children.OfType<Grid>().ToDictionary(row => (string)row.Tag);
        var headings = panel
            .Children.OfType<TextBlock>()
            .ToDictionary(heading => (string)heading.Tag);
        _refreshingFilters = true;
        try
        {
            var position = 0;
            string? previousGroup = null;
            foreach (var section in Model.Library.Facets.GroupBy(facet => facet.Section))
            {
                var group = section.Key switch
                {
                    "resolution" or "subtitles" => "playback_filters",
                    "metadata" => "organisation_filters",
                    "kind" or "size" or "modified" => "file_filters",
                    _ => "content_filters",
                };
                if (group != previousGroup)
                {
                    if (!headings.TryGetValue(group, out var title))
                        title = new TextBlock
                        {
                            Tag = group,
                            Style = (Style)
                                Application.Current.Resources["TinyTorrentGroupTitleTextStyle"],
                        };
                    title.Text = Model.Text.Get("library", group);
                    title.Margin = new Thickness(0, position == 0 ? 0 : 8, 0, 0);
                    Place(title);
                    previousGroup = group;
                }
                if (!rows.TryGetValue(section.Key, out var row))
                {
                    var icon = new FontIcon
                    {
                        FontFamily = Lucide.Font,
                        Glyph = section.Key switch
                        {
                            "type" => Lucide.Clapperboard,
                            "genres" => Lucide.Tags,
                            "year" or "modified" => Lucide.Calendar,
                            "series" => Lucide.Tv,
                            "season" => Lucide.ListOrdered,
                            "artist" => Lucide.Users,
                            "album" => Lucide.Disc,
                            "size" => Lucide.HardDrive,
                            "resolution" => Lucide.Monitor,
                            "subtitles" => Lucide.Captions,
                            "metadata" => Lucide.FileSearch,
                            _ => Lucide.File,
                        },
                        Style = (Style)Application.Current.Resources["TinyTorrentStatusIconStyle"],
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                        icon,
                        Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw
                    );
                    var label = new TextBlock
                    {
                        Style = (Style)Application.Current.Resources["TinyTorrentBodyTextStyle"],
                    };
                    var selector = new ComboBox
                    {
                        Tag = section.Key,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        MinWidth = 0,
                    };
                    row = new Grid
                    {
                        Tag = section.Key,
                        ColumnSpacing = 8,
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = GridLength.Auto },
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                            new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) },
                        },
                        Children = { icon, label, selector },
                    };
                    Grid.SetColumn(label, 1);
                    Grid.SetColumn(selector, 2);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                        selector,
                        "LibraryFilter-" + section.Key
                    );
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetLabeledBy(selector, label);
                    selector.Items.Add(new ComboBoxItem());
                    selector.SelectionChanged += (_, _) =>
                    {
                        if (!_refreshingFilters && LibraryFilters.IsEnabled)
                            Model.Library.Filter(
                                section.Key,
                                (selector.SelectedItem as ComboBoxItem)?.Tag as string
                            );
                    };
                }
                var choices = (ComboBox)row.Children[2];
                var heading = (TextBlock)row.Children[1];
                heading.Text = Model.Text.Get("library", section.Key);
                ToolTipService.SetToolTip(heading, heading.Text);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choices, heading.Text);
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
                    var label =
                        facet.Section == "series"
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
                    if (items[index].Tag is string value && existing.ContainsKey(value))
                        choices.Items.Remove(items[index]);
                    choices.Items.Insert(index, items[index]);
                }
                while (choices.Items.Count > items.Count)
                    choices.Items.RemoveAt(choices.Items.Count - 1);
                choices.SelectedItem = selected;
                var tip =
                    section.Key == "subtitles"
                        ? Model.Text.Format("library", "subtitle_filter_tip", selected.Content)
                        : selected.Content;
                ToolTipService.SetToolTip(choices, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
                    choices,
                    tip?.ToString() ?? string.Empty
                );
                Place(row);
            }
            while (panel.Children.Count > position)
                panel.Children.RemoveAt(panel.Children.Count - 1);

            void Place(UIElement element)
            {
                if (
                    position >= panel.Children.Count
                    || !ReferenceEquals(panel.Children[position], element)
                )
                {
                    panel.Children.Remove(element);
                    panel.Children.Insert(position, element);
                }
                position++;
            }
        }
        finally
        {
            _refreshingFilters = false;
        }
    }
}
