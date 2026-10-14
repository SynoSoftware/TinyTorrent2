using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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
    private LibraryTable? _libraryTable;
    private FocusState _navigationInput;
    private int _navigationRevision;
    private bool _navigationHeld;
    private WindowPage _navigationFace = WindowPage.Torrents;
    private Storyboard? _navigationFlip;
    private CancellationTokenSource? _associationRead;
    private MenuFlyoutSubItem? _libraryFetch;

    private void ConfigureLibrary()
    {
        Model.Library.CommandFailed += (_, error) => Model.Report(error);
        Model.Library.BatchLimited += (_, files) => Model.ShowSearchLimit(files);
        // A submenu has no command, so the menu bar's Fetch metadata follows
        // the command in place rather than by rebuilding, which would close it.
        Model.Library.FetchMetadata.CanExecuteChanged += (_, _) =>
        {
            if (_libraryFetch is { } fetch)
                fetch.IsEnabled = Model.Library.FetchMetadata.CanExecute(null);
        };
        Model.Library.PropertyChanged += (_, args) =>
        {
            QueueStatus();
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
        if (library && _libraryTable is null)
        {
            _libraryTable = new LibraryTable(Model.Library);
            if (_placement?.LibraryLayouts is { } layouts)
                _libraryTable.RestoreLayouts(layouts);
            _libraryTable.ItemContextRequested += (_, args) =>
                LibraryContextMenu().ShowAt(
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
        if (Model.Page is WindowPage.Torrents or WindowPage.Library)
            _workspaces.Show(library ? LibraryContent : TorrentContent, library ? 1 : -1);
        Filters.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
        LibraryFilters.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
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
        menu.Items.Add(MenuHeader(
            Model.Library.Selected?.Title
                ?? Model.Text.Format("window", "selected", Model.Library.Selection.Items.Count)
        ));
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

    // Settings opens this dialog when the person chooses a provider whose
    // data is not downloaded, and the finished download turns the provider
    // on. It also opens when the person updates installed data, and then
    // states the data's age. The download continues after the dialog closes,
    // and Settings shows its progress.
    private async Task ConfirmDownload(ProviderOption option)
    {
        var installed = option.Dataset?.Inspect();
        var current = option.Dataset?.IsCurrent == true;
        await Interact(async interaction =>
        {
            var dialog = new Controls.Dialog { Glyph = Lucide.Database, PrimaryGlyph = Lucide.Download };
            dialog.SetBinding(
                ContentDialog.IsPrimaryButtonEnabledProperty,
                new Binding
                {
                    Source = Model.Library,
                    Path = new PropertyPath(nameof(LibraryBrowser.CanDownload)),
                    Mode = BindingMode.OneWay,
                }
            );
            dialog.PrimaryButtonClick += (sender, args) =>
            {
                args.Cancel = !Model.Library.CanDownload;
                if (!args.Cancel)
                    _ = Model.DownloadData(enable: installed is null);
            };
            var choice = await ShowDialog(
                interaction,
                dialog,
                () =>
                {
                    var id = option.ProviderId;
                    if (installed is null)
                    {
                        dialog.Title = Model.Text.Get(id, "download_title");
                        dialog.Content = DataConfirmation(
                            [Model.Text.Get(id, "download_body"), Model.Text.Get(id, "download_local")],
                            [Model.Text.Get(id, "preparation_time")]);
                        dialog.PrimaryButtonText = Model.Text.Get(id, "download");
                        dialog.PrimaryToolTip = Model.Text.Get(id, "download_tip");
                        return;
                    }
                    // Current data warns, as Dataset.IsCurrent explains; data
                    // less than a week old notes that little has changed.
                    var days = (int)(DateTime.UtcNow - installed.LastWriteTimeUtc).TotalDays;
                    dialog.Title = Model.Text.Get(id, "update_title");
                    var body =
                        current ? Model.Text.Get(id, "update_current")
                        : days < 7 ? Model.Text.FormatCount(id, "update_recent", days)
                        : Model.Text.Format(id, "update_age", days);
                    dialog.Content = DataConfirmation(
                        [body, Model.Text.Get(id, "update_frequency")],
                        [Model.Text.Get(id, "preparation_time")]);
                    dialog.PrimaryButtonText = Model.Text.Get(id, "update");
                    dialog.PrimaryToolTip = Model.Text.Get(id, "update_tip");
                }
            );
            return choice == ContentDialogResult.Primary;
        });
    }

    private async Task ConfirmDataDeletion(ProviderOption option)
    {
        if (option.Dataset?.Inspect() is not { } data)
            return;
        await Interact(async interaction =>
        {
            var dialog = new Controls.Dialog { Glyph = Lucide.Database, PrimaryGlyph = Lucide.Trash2 };
            var choice = await ShowDialog(
                interaction,
                dialog,
                () =>
                {
                    dialog.Title = Model.Text.Get(option.ProviderId, "delete_title");
                    dialog.Content = DataConfirmation(
                        [Model.Text.Format(option.ProviderId, "delete_body", Model.Text.Bytes(data.Length)),
                            Model.Text.Get(option.ProviderId, "delete_kept")],
                        [Model.Text.Get(option.ProviderId, "restore_time"),
                            Model.Text.Get(option.ProviderId, "preparation_time")]);
                    dialog.PrimaryButtonText = Model.Text.Get("file_action", "delete");
                    dialog.PrimaryToolTip = Model.Text.Get(option.ProviderId, "delete_tip");
                }
            );
            if (choice != ContentDialogResult.Primary)
                return false;
            await Model.Library.DeleteData();
            return true;
        });
    }

    private StackPanel DataConfirmation(IEnumerable<string> facts, IEnumerable<string> caution)
    {
        var icon = new FontIcon
        {
            FontFamily = Lucide.Font,
            Glyph = Lucide.TriangleAlert,
            Style = (Style)Root.Resources["DataCautionIconStyle"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var message = Lines(caution);
        Grid.SetColumn(message, 1);
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Lines(facts),
                new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    },
                    Children = { icon, message },
                },
            },
        };
    }

    private async Task FetchBatch(ProviderOption source)
    {
        try
        {
            var entries = Model.Library.Selection.Items.Cast<LibraryRow>().Select(row => row.Entry).ToArray();
            var plan = await Model.Library.Plan(entries, source, CancellationToken.None);
            var content = new Library.BatchDialog(Model.Library, source, plan);
            await Interact(async interaction =>
            {
                var dialog = new Controls.Dialog { Content = content, Glyph = Lucide.Film, PrimaryGlyph = Lucide.Download };
                await ShowEditor(interaction, dialog, content, () =>
                {
                    dialog.Title = Model.Text.Get("library", "fetch_metadata");
                    dialog.PrimaryButtonText = Model.Text.Get("library", "fetch");
                    content.RefreshText();
                });
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
            interaction.ResolveDraft = () => SaveOnClose(interaction, content, canSave: !content.RequiresForce);
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
        LibraryFilters.IsEnabled = Model.Library.RowsConfiguration == Model.Library.Configuration;
        if (!Model.IsFilterOpen || !LibraryFilters.IsEnabled)
            return;
        if (LibraryFilters.Content is not StackPanel panel)
        {
            panel = new StackPanel { Spacing = 12 };
            LibraryFilters.Content = panel;
        }
        var rows = panel.Children.OfType<Grid>().ToDictionary(row => (string)row.Tag);
        var headings = panel.Children.OfType<TextBlock>().ToDictionary(heading => (string)heading.Tag);
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
                            Style = (Style)Application.Current.Resources["TinyTorrentGroupTitleTextStyle"],
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
                        icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
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
                var tip = section.Key == "subtitles"
                    ? Model.Text.Format("library", "subtitle_filter_tip", selected.Content)
                    : selected.Content;
                ToolTipService.SetToolTip(choices, tip);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(choices, tip?.ToString() ?? string.Empty);
                Place(row);
            }
            while (panel.Children.Count > position)
                panel.Children.RemoveAt(panel.Children.Count - 1);

            void Place(UIElement element)
            {
                if (position >= panel.Children.Count || !ReferenceEquals(panel.Children[position], element))
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
