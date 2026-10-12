using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Models;
using LibraryBrowser = Syno.TinyTorrent.Library.Browser;

namespace Syno.TinyTorrent.Views;

public sealed partial class InspectorPane
{
    private LibraryBrowser? _library;
    private ScrollViewer? _libraryContent;
    private readonly SelectorBarItem _informationSection = new();
    private readonly SelectorBarItem _fileSection = new();
    private SelectorBarItem? _librarySection;
    private ICommand? _downloadSubtitles;

    internal void ShowLibrary(LibraryBrowser? library, ICommand? downloadSubtitles)
    {
        _downloadSubtitles = downloadSubtitles;
        if (ReferenceEquals(_library, library))
            return;
        if (_library is not null)
        {
            _library.PropertyChanged -= OnLibrary;
            ClearLibraryActions();
            (_libraryContent?.Content as Details)?.Dispose();
            LibraryOpen.Command = null;
            _library.CancelInformation();
        }
        _library = library;
        if (library is not null)
        {
            Bindings.StopTracking();
            library.PropertyChanged += OnLibrary;
            if (_libraryContent is null)
            {
                AutomationProperties.SetAutomationId(_informationSection, "LibraryInformation");
                AutomationProperties.SetAutomationId(_fileSection, "LibraryFile");
                _libraryContent = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Margin = new Thickness(16, 12, 16, 12),
                };
                _libraryContent.SizeChanged += OnGeneralSize;
                SectionViews.Children.Add(_libraryContent);
            }
        }
        _refreshing = true;
        Sections.SelectedItem = null;
        Sections.Items.Clear();
        if (library is null)
        {
            foreach (var section in _torrentSections)
                Sections.Items.Add(section);
            Sections.SelectedItem = _torrentSections[(int)Model.Section];
        }
        else
        {
            Sections.Items.Add(_informationSection);
            Sections.Items.Add(_fileSection);
            Sections.SelectedItem = _librarySection ?? _informationSection;
        }
        _refreshing = false;
        LibraryActions.Visibility = library is null ? Visibility.Collapsed : Visibility.Visible;
        if (library is null)
        {
            Bindings.Update();
            Retry.Command = Model.Retry;
        }
        _arrival.Stop();
        _awaitingArrival = false;
        _target = Model.Target;
        _section = Model.Section;
        Refresh(workspace: true);
    }

    private void OnLibrary(object? sender, PropertyChangedEventArgs args)
    {
        if (_library is not { } library)
            return;
        if (args.PropertyName == nameof(LibraryBrowser.IsAvailable))
        {
            (_libraryContent?.Content as Details)?.RefreshCommands();
            RefreshLibraryActions();
            return;
        }
        if (args.PropertyName == nameof(LibraryBrowser.Origins))
        {
            RefreshLibraryActions();
            return;
        }
        if (args.PropertyName == nameof(LibraryBrowser.Message))
        {
            RefreshLibraryFailure();
            return;
        }
        if (args.PropertyName == nameof(LibraryBrowser.FetchMessage))
        {
            (_libraryContent?.Content as Details)?.RefreshFetch(library);
            return;
        }
        if (args.PropertyName == nameof(LibraryBrowser.FileType))
        {
            RefreshLibraryOpenTip();
            if (ReferenceEquals(Sections.SelectedItem, _fileSection))
                RefreshLibrary();
            return;
        }
        if (args.PropertyName == nameof(LibraryBrowser.Selection))
        {
            RefreshLibraryEmpty();
            return;
        }
        if (
            args.PropertyName
            is nameof(LibraryBrowser.Selected)
                or nameof(LibraryBrowser.Synopsis)
                or nameof(LibraryBrowser.IsEnrichmentEnabled)
                or nameof(LibraryBrowser.Waiting)
                or ""
        )
        {
            RefreshLibrary();
            if (
                args.PropertyName == nameof(LibraryBrowser.Selected)
                && library.Selected is not null
            )
                _arrival.Fade(SectionViews, Math.Min(SectionViews.Opacity, 0.65), 1);
        }
    }

    private void RefreshLibrary()
    {
        if (_library is not { } library || _libraryContent is null)
            return;
        var text = library.Text;
        var row = library.Selected;
        LibraryActions.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshLibraryEmpty();
        HeadingName.Text = row?.Name ?? string.Empty;
        ToolTipService.SetToolTip(HeadingName, row?.Name ?? string.Empty);
        HeadingSize.Text = row?.Size ?? string.Empty;
        _refreshing = true;
        Sections.IsEnabled = row is not null;
        _informationSection.Text = text.Get("library", "info");
        _fileSection.Text = text.Get("library", "file");
        _informationSection.IsEnabled =
            row is null || row.Entry.Kind is FileKind.Video or FileKind.Audio;
        Sections.SelectedItem = _informationSection.IsEnabled
            ? _librarySection ?? _informationSection
            : _fileSection;
        _refreshing = false;
        if (row is null || !ReferenceEquals(Sections.SelectedItem, _informationSection))
            library.CancelInformation();
        RefreshLibraryFailure();
        Retry.Command = library.Retry;
        ClearLibraryContent();
        RefreshLibraryActions();
        _libraryContent.Content = new Details(
            library,
            ReferenceEquals(Sections.SelectedItem, _fileSection),
            _libraryContent
        );
        _motion.CrossFade(row is null ? EmptyState : _libraryContent);
    }

    private void RefreshLibraryActions()
    {
        if (_library is not { } library || _libraryContent is null)
            return;
        var text = library.Text;
        ClearLibraryActions();
        AutomationProperties.SetName(LibraryOpen, text.Get("commands", "open"));
        LibraryOpen.Command = library.Open;
        RefreshLibraryOpenTip();
        AutomationProperties.SetName(LibraryTorrents, text.Get("library", "show_torrents"));
        ToolTipService.SetToolTip(LibraryTorrents, text.Get("library", "show_torrents"));
        OriginActions.Bind(LibraryTorrents, library);
        AutomationProperties.SetName(LibraryMore, text.Get("library", "more"));
        ToolTipService.SetToolTip(LibraryMore, text.Get("library", "more"));
        var more = new MenuFlyout();
        var open = new MenuFlyoutItem
        {
            Text = text.Get("commands", "open"),
            Command = library.Open,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Play },
        };
        var fetch = MetadataActions.Create(library);
        var refresh = new MenuFlyoutItem
        {
            Text = text.Get("library", "refresh_metadata"),
            Command = library.FetchDetails,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.RefreshCw },
        };
        var subtitle = new MenuFlyoutItem
        {
            Text = text.Get("subtitles", "choose"),
            Command = _downloadSubtitles,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Download },
        };
        var torrents = OriginActions.Create(library);
        var information = new MenuFlyoutSeparator();
        var removal = new MenuFlyoutSeparator();
        var erase = new MenuFlyoutItem
        {
            Text = text.Get("library", "erase_metadata"),
            Command = library.EraseMetadata,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Eraser },
        };
        more.Items.Add(open);
        more.Opening += (_, _) =>
        {
            var narrow = _libraryContent.ActualWidth < FieldColumns.WideMinimum;
            open.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            torrents.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            // Providers and their availability can change after the menu is
            // built, so the submenu is built again each time it opens.
            var index = more.Items.IndexOf(fetch);
            more.Items[index] = fetch = MetadataActions.Create(library);
            fetch.Visibility =
                library.Selected?.Entry.Kind == FileKind.Video
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            refresh.Visibility = subtitle.Visibility = fetch.Visibility;
            information.Visibility = removal.Visibility = erase.Visibility = fetch.Visibility;
        };
        more.Items.Add(
            new MenuFlyoutItem
            {
                Text = text.Get("commands", "open_folder"),
                Command = library.OpenFolder,
                Icon = new FontIcon
                {
                    FontFamily = Syno.Lucide.Font,
                    Glyph = Syno.Lucide.FolderOpen,
                },
            }
        );
        more.Items.Add(torrents);
        more.Items.Add(information);
        more.Items.Add(fetch);
        more.Items.Add(refresh);
        more.Items.Add(subtitle);
        more.Items.Add(removal);
        more.Items.Add(erase);
        LibraryMore.Flyout = more;
    }

    private void RefreshLibraryFailure()
    {
        if (_library is not { } library)
            return;
        FailureBar.IsOpen = library.Message.Length > 0;
        FailureBar.Message = library.Message;
        ToolTipService.SetToolTip(Retry, library.RetryTip);
        HeadingGlyph.Glyph = library.Selected?.Glyph ?? Syno.Lucide.File;
    }

    private void RefreshLibraryEmpty()
    {
        if (_library is not { } library)
            return;
        var count = library.Selection.Items.Count;
        EmptyText.Text =
            count == 0
                ? library.Text.Get("library", "none")
                : library.Text.FormatCount("library", "selection", count);
    }

    private void RefreshLibraryOpenTip()
    {
        if (_library is not { } library)
            return;
        var tooltip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (library.ApplicationIcon is { } icon)
            tooltip.Children.Add(
                new Image
                {
                    Source = icon,
                    Width = 24,
                    Height = 24,
                }
            );
        tooltip.Children.Add(new TextBlock { Text = library.OpenTip });
        ToolTipService.SetToolTip(LibraryOpen, tooltip);
    }

    private void ClearLibraryActions()
    {
        LibraryMore.Flyout?.Hide();
        if (LibraryMore.Flyout is MenuFlyout menu)
            ClearLibraryMenu(menu.Items);
        LibraryMore.Flyout = null;
        LibraryTorrents.Command = null;
        LibraryTorrents.Flyout?.Hide();
        if (LibraryTorrents.Flyout is MenuFlyout origins)
            ClearLibraryMenu(origins.Items);
        LibraryTorrents.Flyout = null;
    }

    private static void ClearLibraryMenu(IList<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            if (item is MenuFlyoutItem command)
                command.Command = null;
            else if (item is MenuFlyoutSubItem submenu)
                ClearLibraryMenu(submenu.Items);
        }
        items.Clear();
    }

    private void ClearLibraryContent()
    {
        if (_libraryContent?.Content is Details details)
        {
            details.Dispose();
            _libraryContent.Content = null;
        }
    }
}
