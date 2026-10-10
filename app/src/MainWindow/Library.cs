using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
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
    private FocusState _navigationInput;
    private int _navigationRevision;
    private bool _navigationHeld;
    private WindowPage _navigationFace = WindowPage.Torrents;
    private Storyboard? _navigationFlip;
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
                nameof(LibraryBrowser.Selected) or nameof(LibraryBrowser.EditMetadataLabel) or nameof(LibraryBrowser.IsAvailable))
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
            if (Model.Torrents.FirstOrDefault(torrent => torrent.TorrentId == origin.OriginId) is not { } torrent)
                return;
            if (!await ShowTorrents())
                return;
            _fromLibrary = true;
            _returnPage = WindowPage.Library;
            if (!await SelectTorrent(new Syno.TableView.Selection([torrent], torrent)))
                return;
            Model.OpenInspector();
            UpdatePage();
            (InspectorContent.Content as InspectorPane)?.RevealFile(origin.FileIndex);
        };
        Model.Library.EnableRequested += async (_, _) => await EnableVideoInformation();
        Model.LibraryRequested += async (_, _) => await Navigate(WindowPage.Library);
        Model.Library.EditMetadataRequested += async (_, _) => await EditMetadata();
        Model.Library.ConfigureRequested += async (_, _) => await ConfigureVideo();
        Activated += (_, args) => Model.Library.ActivateInformation(args.WindowActivationState != WindowActivationState.Deactivated);
        AddShortcut(new KeyboardAccelerator { Key = VirtualKey.Number1, Modifiers = VirtualKeyModifiers.Control },
            Model.ShowTorrents);
        AddShortcut(new KeyboardAccelerator { Key = VirtualKey.Number2, Modifiers = VirtualKeyModifiers.Control },
            Model.ShowLibrary);
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
        if (await Navigate(NavigationPage == WindowPage.Library ? WindowPage.Torrents : WindowPage.Library))
        {
            _fromLibrary = false;
            UpdatePage();
        }
        else if (revision == _navigationRevision)
            _navigationHeld = false;
        RefreshNavigation();
    }

    private WindowPage NavigationPage => Model.Page is WindowPage.Torrents or WindowPage.Library
        ? Model.Page : _returnPage;

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
        var library = NavigationPage == WindowPage.Library;
        var destination = Model.Text.Get(library ? "window" : "library", library ? "torrents" : "title");
        NameButton(AppIcon, Model.Text.Format("shortcuts", "tip",
            Model.Text.Format("chrome", "switch_page", destination),
            ShortcutText(library ? Model.ShowTorrents : Model.ShowLibrary)));
        var active = _navigationInput != FocusState.Unfocused;
        if (!active)
            _navigationHeld = false;
        var face = active && !_navigationHeld
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
        var shown = TorrentsFace.Visibility == Visibility.Visible ? WindowPage.Torrents : WindowPage.Library;
        if (shown == face)
        {
            FlipNavigation(rotation, 0);
            return;
        }
        var edge = rotation == 0 ? (face == WindowPage.Library ? 90 : -90) : Math.CopySign(90, rotation);
        FlipNavigation(rotation, edge, () =>
        {
            ShowNavigationFace();
            NavigationProjection.RotationY = -edge;
            FlipNavigation(-edge, 0);
        });
    }

    private void ShowNavigationFace()
    {
        TorrentsFace.Visibility = _navigationFace == WindowPage.Torrents ? Visibility.Visible : Visibility.Collapsed;
        LibraryFace.Visibility = _navigationFace == WindowPage.Library ? Visibility.Visible : Visibility.Collapsed;
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
            EasingFunction = new CubicEase { EasingMode = to == 0 ? EasingMode.EaseOut : EasingMode.EaseIn },
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
        yield return new MenuFlyoutItem { Text = Model.Library.EditMetadataLabel, Command = Model.Library.EditMetadata, Icon = Icon(Model.Library.EditMetadataGlyph) };
        yield return new MenuFlyoutItem
        {
            Text = Model.Text.Get("library", "erase_metadata"),
            Command = Model.Library.EraseMetadata,
            Icon = Icon(Lucide.Eraser),
        };
        yield return OriginActions.Create(Model.Library);
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
        ViewMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var configuration in Enum.GetValues<LibraryConfiguration>())
        {
            var choice = new ToggleMenuFlyoutItem
            {
                Text = Model.Text.Get("library", configuration.ToString().ToLowerInvariant()),
                IsChecked = Model.Library.Configuration == configuration,
                Icon = Icon(configuration switch
                {
                    LibraryConfiguration.Videos => Syno.Lucide.Film,
                    LibraryConfiguration.Music => Syno.Lucide.Music,
                    _ => Syno.Lucide.Files,
                }),
            };
            choice.Click += (_, _) =>
            {
                choice.IsChecked = true;
                Model.Library.Configuration = configuration;
            };
            ViewMenu.Items.Add(choice);
        }
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

    private async Task EditMetadata(MetadataDialog? content = null)
    {
        if (Model.Library.Selected is not { } selected)
            return;
        content ??= new MetadataDialog(Model.Library, selected.Entry);
        await Interact(async interaction =>
        {
            interaction.Restore = () => EditMetadata(content);
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
                    dialog.Title = Model.Text.Get("library", "edit_metadata");
                    dialog.PrimaryButtonText = Model.Text.Get("dialog", "save");
                    dialog.PrimaryToolTip = Model.Text.Get("library", "save_metadata_tip");
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
