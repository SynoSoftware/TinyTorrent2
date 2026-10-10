using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
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

    internal void ShowLibrary(LibraryBrowser? library)
    {
        if (ReferenceEquals(_library, library))
            return;
        if (_library is not null)
        {
            _library.PropertyChanged -= OnLibrary;
            ClearLibraryActions();
            ClearLibraryContent();
            LibraryOpen.Command = null;
            _library.WatchInformation(false);
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
                    Margin = new Thickness(16,12,16,12),
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
        Refresh();
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
        if (args.PropertyName is nameof(LibraryBrowser.IsFetching) or nameof(LibraryBrowser.FetchLabel) or
            nameof(LibraryBrowser.FetchTip) or nameof(LibraryBrowser.FetchMessage))
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
        if (args.PropertyName is nameof(LibraryBrowser.Selected) or nameof(LibraryBrowser.EditMetadataLabel) or nameof(LibraryBrowser.Synopsis) or nameof(LibraryBrowser.IsEnrichmentEnabled) or
            nameof(LibraryBrowser.IsOnDemand) or "")
            RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        if (_library is not { } library || _libraryContent is null)
            return;
        var text = library.Text;
        var row = library.Selected;
        LibraryActions.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = text.Get("library", "none");
        HeadingName.Text = row?.Name ?? string.Empty;
        ToolTipService.SetToolTip(HeadingName, row?.Name ?? string.Empty);
        HeadingSize.Text = row?.Size ?? string.Empty;
        _refreshing = true;
        Sections.IsEnabled = row is not null;
        _informationSection.Text = text.Get("library", "info");
        _fileSection.Text = text.Get("library", "file");
        _informationSection.IsEnabled = row is null || row.Entry.Kind is FileKind.Video or FileKind.Audio;
        Sections.SelectedItem = _informationSection.IsEnabled ? _librarySection ?? _informationSection : _fileSection;
        _refreshing = false;
        library.WatchInformation(row is not null && ReferenceEquals(Sections.SelectedItem, _informationSection));
        RefreshLibraryFailure();
        Retry.Command = library.Retry;
        ClearLibraryContent();
        RefreshLibraryActions();
        _libraryContent.Content = new Details(library, ReferenceEquals(Sections.SelectedItem, _fileSection), _libraryContent);
        _motion.Show(row is null ? EmptyState : _libraryContent);
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
        var open = new MenuFlyoutItem { Text = text.Get("commands", "open"), Command = library.Open,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Play } };
        var edit = new MenuFlyoutItem { Text = library.EditMetadataLabel, Command = library.EditMetadata,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = library.EditMetadataGlyph } };
        var torrents = OriginActions.Create(library);
        more.Items.Add(open);
        more.Items.Add(torrents);
        more.Opening += (_, _) =>
        {
            var narrow = _libraryContent.ActualWidth < FieldColumns.WideMinimum;
            open.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            torrents.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            edit.Visibility = library.Selected?.Entry.Kind == FileKind.Video
                ? Visibility.Visible : Visibility.Collapsed;
        };
        more.Items.Add(new MenuFlyoutItem { Text = text.Get("commands", "open_folder"), Command = library.OpenFolder,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.FolderOpen } });
        more.Items.Add(edit);
        more.Items.Add(new MenuFlyoutItem { Text = text.Get("library", "erase_metadata"), Command = library.EraseMetadata,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Eraser } });
        LibraryMore.Flyout = more;
    }

    private void RefreshLibraryFailure()
    {
        if (_library is not { } library)
            return;
        FailureBar.IsOpen = library.Message.Length > 0;
        FailureBar.Message = library.Message;
        HeadingGlyph.Glyph = library.Selected?.Glyph ?? Syno.Lucide.File;
    }

    private void RefreshLibraryOpenTip()
    {
        if (_library is not { } library)
            return;
        var tooltip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (library.ApplicationIcon is { } icon)
            tooltip.Children.Add(new Image { Source = icon, Width = 24, Height = 24 });
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
