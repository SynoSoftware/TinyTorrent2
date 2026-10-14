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
    private SelectorBarItem? _librarySection;
    private ICommand? _downloadSubtitles;
    private (LibraryBrowser Browser, Entry? Entry, bool File, string Language)? _libraryView;

    internal void ShowLibrary(LibraryBrowser? library, ICommand? downloadSubtitles)
    {
        _downloadSubtitles = downloadSubtitles;
        if (ReferenceEquals(_library, library))
            return;
        if (_library is not null)
        {
            _library.PropertyChanged -= OnLibrary;
            ClearLibraryActions();
            LibraryOpen.Command = null;
            _library.CancelInformation();
        }
        _library = library;
        PeerActions.Visibility = Visibility.Collapsed;
        ArrangePeers();
        if (library is not null)
        {
            Bindings.StopTracking();
            library.PropertyChanged += OnLibrary;
            if (_libraryContent is null)
            {
                _libraryContent = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Margin = new Thickness(16, 12, 16, 12),
                };
                _libraryContent.SizeChanged += OnGeneralSize;
                SectionViews.Children.Add(_libraryContent);
            }
        }
        Sections.Visibility = library is null ? Visibility.Visible : Visibility.Collapsed;
        LibrarySections.Visibility = library is null ? Visibility.Collapsed : Visibility.Visible;
        LibraryActions.Visibility = library is null ? Visibility.Collapsed : Visibility.Visible;
        if (library is null)
        {
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
        if (args.PropertyName == nameof(LibraryBrowser.IsInspectorVisible))
        {
            if (library.IsInspectorVisible)
                RefreshLibrary();
            else
                ClearLibraryActions();
            return;
        }
        if (!library.IsInspectorVisible)
            return;
        if (args.PropertyName == nameof(LibraryBrowser.Rows) && _libraryView?.Entry == library.Selected?.Entry)
            return;
        if (args.PropertyName == nameof(LibraryBrowser.IsAvailable))
        {
            (_libraryContent?.Content as Details)?.RefreshCommands();
            RefreshLibraryOrigins();
            LibraryMore.IsEnabled = library.IsAvailable;
            return;
        }
        if (args.PropertyName == nameof(LibraryBrowser.Origins))
        {
            RefreshLibraryOrigins();
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
            if (ReferenceEquals(LibrarySections.SelectedItem, LibraryFile))
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
                or nameof(LibraryBrowser.Rows)
                or nameof(LibraryBrowser.Synopsis)
                or nameof(LibraryBrowser.AutomaticWait)
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
        if (_library is not { IsInspectorVisible: true } library || _libraryContent is null)
            return;
        var text = library.Text;
        var row = library.Selected;
        LibraryActions.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshLibraryEmpty();
        HeadingName.Text = row?.Name ?? string.Empty;
        ToolTipService.SetToolTip(HeadingName, row?.Name ?? string.Empty);
        HeadingSize.Text = row?.Size ?? string.Empty;
        _refreshing = true;
        LibrarySections.IsEnabled = row is not null;
        LibraryInformation.Text = text.Get("library", "info");
        LibraryFile.Text = text.Get("library", "file");
        LibraryInformation.IsEnabled =
            row is null || row.Entry.Kind is FileKind.Video or FileKind.Audio;
        LibrarySections.SelectedItem = LibraryInformation.IsEnabled
            ? _librarySection ?? LibraryInformation
            : LibraryFile;
        _refreshing = false;
        if (row is null || !ReferenceEquals(LibrarySections.SelectedItem, LibraryInformation))
            library.CancelInformation();
        RefreshLibraryFailure();
        Retry.Command = library.Retry;
        var showsFile = ReferenceEquals(LibrarySections.SelectedItem, LibraryFile);
        var targetChanged = _libraryView?.Entry?.EntryId != row?.Entry.EntryId;
        if (_libraryView?.Language != text.Language || targetChanged || LibraryMore.Flyout is null)
            RefreshLibraryActions();
        var view = (library, row?.Entry, showsFile, text.Language);
        if (_libraryView == view && _libraryContent.Content is Details details)
            details.Refresh(library);
        else
        {
            ClearLibraryContent();
            _libraryContent.Content = new Details(library, showsFile, _libraryContent);
        }
        _libraryView = view;
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
        RefreshLibraryOrigins();
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
            Command = library.RefreshMetadata,
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
            var originIndex = more.Items.IndexOf(torrents);
            more.Items[originIndex] = torrents = OriginActions.Create(library);
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
        LibraryMore.IsEnabled = library.IsAvailable;
    }

    private void RefreshLibraryOrigins()
    {
        if (_library is not { IsInspectorVisible: true } library || LibraryTorrents.Flyout?.IsOpen == true)
            return;
        OriginActions.Bind(LibraryTorrents, library);
        if (LibraryTorrents.Flyout is { } flyout)
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(LibraryTorrents.Flyout, flyout))
                    RefreshLibraryOrigins();
            };
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
