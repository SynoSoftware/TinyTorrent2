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
            ClearLibraryContent();
            LibraryOpen.Command = LibraryEdit.Command = null;
            _library.WatchInformation(false);
        }
        _library = library;
        if (library is not null)
        {
            library.PropertyChanged += OnLibrary;
            if (_libraryContent is null)
            {
                AutomationProperties.SetAutomationId(_informationSection, "LibraryInformation");
                AutomationProperties.SetAutomationId(_fileSection, "LibraryFile");
                _libraryContent = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
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
        if (args.PropertyName is nameof(LibraryBrowser.Selected) or nameof(LibraryBrowser.IdentifyLabel) or nameof(LibraryBrowser.Synopsis) or nameof(LibraryBrowser.Message) or nameof(LibraryBrowser.IsEnrichmentEnabled) or nameof(LibraryBrowser.IsAvailable) or nameof(LibraryBrowser.FileType) or
            nameof(LibraryBrowser.IsOnDemand) or nameof(LibraryBrowser.IsFetching) or nameof(LibraryBrowser.FetchLabel) or nameof(LibraryBrowser.FetchMessage) or "")
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
        HeadingGlyph.Glyph = row?.Glyph ?? Syno.Lucide.File;
        _refreshing = true;
        Sections.IsEnabled = row is not null;
        _informationSection.Text = text.Get("library", row?.Entry.Kind == FileKind.Audio ? "music_information" : "video_information");
        _fileSection.Text = text.Get("library", "file");
        _informationSection.IsEnabled = row is null || row.Entry.Kind is FileKind.Video or FileKind.Audio;
        Sections.SelectedItem = _informationSection.IsEnabled ? _librarySection ?? _informationSection : _fileSection;
        _refreshing = false;
        library.WatchInformation(row is not null && ReferenceEquals(Sections.SelectedItem, _informationSection));
        FailureBar.IsOpen = library.Message.Length > 0;
        FailureBar.Message = library.Message;
        Retry.Command = library.Retry;
        AutomationProperties.SetName(LibraryOpen, text.Get("commands", "open"));
        LibraryOpen.Command = library.Open;
        var tooltip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (library.ApplicationIcon is { } icon)
            tooltip.Children.Add(new Image { Source = icon, Width = 24, Height = 24 });
        tooltip.Children.Add(new TextBlock { Text = library.OpenTip });
        ToolTipService.SetToolTip(LibraryOpen, tooltip);
        AutomationProperties.SetName(LibraryEdit, library.IdentifyLabel);
        LibraryEditIcon.Glyph = library.IdentifyGlyph;
        LibraryEdit.Command = library.Identify;
        LibraryEdit.Visibility = row?.Entry.Kind == FileKind.Video && row.Entry.Type.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(LibraryEdit, library.IdentifyLabel);
        AutomationProperties.SetName(LibraryMore, text.Get("library", "more"));
        ToolTipService.SetToolTip(LibraryMore, text.Get("library", "more"));
        ClearLibraryContent();
        var more = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = text.Get("commands", "open"), Command = library.Open,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Play } };
        var edit = new MenuFlyoutItem { Text = library.IdentifyLabel, Command = library.Identify,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = library.IdentifyGlyph } };
        more.Items.Add(open);
        more.Items.Add(edit);
        more.Opening += (_, _) =>
        {
            var narrow = _libraryContent.ActualWidth < FieldColumns.WideMinimum;
            open.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            edit.Visibility = row?.Entry.Kind == FileKind.Video && (narrow || row.Entry.Type.Length == 0)
                ? Visibility.Visible : Visibility.Collapsed;
        };
        more.Items.Add(new MenuFlyoutItem { Text = text.Get("commands", "open_folder"), Command = library.OpenFolder,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.FolderOpen } });
        more.Items.Add(new MenuFlyoutItem { Text = text.Get("library", "clear_identification"), Command = library.ClearIdentification,
            Icon = new FontIcon { FontFamily = Syno.Lucide.Font, Glyph = Syno.Lucide.Eraser } });
        LibraryMore.Flyout = more;
        _libraryContent.Content = new Details(library, ReferenceEquals(Sections.SelectedItem, _fileSection));
        _motion.Show(row is null ? EmptyState : _libraryContent);
    }

    private void ClearLibraryContent()
    {
        if (LibraryMore.Flyout is MenuFlyout menu)
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
                item.Command = null;
        LibraryMore.Flyout = null;
        if (_libraryContent?.Content is Details details)
        {
            details.Dispose();
            _libraryContent.Content = null;
        }
    }
}
