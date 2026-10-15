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
    private ICommand? _downloadSubtitles;
    private (LibraryBrowser Browser, Entry? Entry, bool File, string Language)? _libraryView;
    private readonly Button _libraryOpen = CreateAction(Lucide.Play, "LibraryOpen");
    private readonly Button _libraryTorrents = CreateAction(Lucide.LocateFixed, "LibraryTorrents");
    private readonly Button _libraryMore = CreateAction(Lucide.Ellipsis, "LibraryMore");

    internal void ShowLibrary(LibraryBrowser? library, ICommand? downloadSubtitles)
    {
        _downloadSubtitles = downloadSubtitles;
        if (ReferenceEquals(_library, library))
            return;
        if (_library is not null)
        {
            _library.PropertyChanged -= OnLibrary;
            ClearLibraryActions();
        }
        _library = library;
        ArrangePeers();
        if (library is not null)
        {
            Bindings.StopTracking();
            if (IsLoaded)
                library.PropertyChanged += OnLibrary;
            if (_libraryContent is null)
            {
                _libraryContent = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Margin = new Thickness(16, 12, 16, 12),
                };
                SectionViews.Children.Add(_libraryContent);
            }
        }
        Sections.Visibility = library is null ? Visibility.Visible : Visibility.Collapsed;
        LibrarySections.Visibility = library is null ? Visibility.Collapsed : Visibility.Visible;
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
            {
                ClearLibraryActions();
                ShowActions(null);
            }
            return;
        }
        if (!library.IsInspectorVisible)
            return;
        if (args.PropertyName == nameof(LibraryBrowser.Section))
        {
            var showsFile = library.Section == LibrarySection.File;
            if (_libraryContent is not null && _libraryView is { } view && view.File != showsFile)
                _arrival.Enter(_libraryContent, forward: showsFile, update: RefreshLibrary);
            else
                RefreshLibrary();
            return;
        }
        if (args.PropertyName == nameof(LibraryBrowser.Rows) && _libraryView?.Entry == library.Selected?.Entry)
            return;
        if (args.PropertyName == nameof(LibraryBrowser.IsAvailable))
        {
            (_libraryContent?.Content as Details)?.RefreshCommands();
            RefreshLibraryOrigins();
            _libraryMore.IsEnabled = library.IsAvailable;
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
            if (library.Section == LibrarySection.File)
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
        {
            ShowActions(null);
            return;
        }
        var text = library.Text;
        var row = library.Selected;
        if (row is null)
            ShowActions(null);
        else
            ShowActions(_libraryMore, _libraryOpen, _libraryTorrents);
        RefreshLibraryEmpty();
        HeadingName.Text = row?.Name ?? string.Empty;
        ToolTipService.SetToolTip(HeadingName, row?.Name ?? string.Empty);
        HeadingSize.Text = row?.Size ?? string.Empty;
        _refreshing = true;
        LibrarySections.IsEnabled = row is not null;
        LibraryInformation.Text = text.Get("library", "info");
        LibraryFile.Text = text.Get("library", "file");
        LibraryInformation.IsEnabled = library.CanShowInformation;
        LibrarySections.SelectedItem = library.Section == LibrarySection.File
            ? LibraryFile : LibraryInformation;
        _refreshing = false;
        RefreshLibraryFailure();
        Retry.Command = library.Retry;
        var showsFile = library.Section == LibrarySection.File;
        var targetChanged = _libraryView?.Entry?.EntryId != row?.Entry.EntryId;
        if (_libraryView?.Language != text.Language || targetChanged || _libraryMore.Flyout is null)
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
        AutomationProperties.SetName(_libraryOpen, text.Get("commands", "open"));
        _libraryOpen.Command = library.Open;
        RefreshLibraryOpenTip();
        AutomationProperties.SetName(_libraryTorrents, text.Get("library", "show_torrents"));
        ToolTipService.SetToolTip(_libraryTorrents, text.Get("library", "show_torrents"));
        RefreshLibraryOrigins();
        AutomationProperties.SetName(_libraryMore, text.Get("library", "more"));
        ToolTipService.SetToolTip(_libraryMore, text.Get("library", "more"));
        _libraryMore.Flyout = MenuItems.Flyout(LibraryMenu, () => Model.Text);
        _libraryMore.IsEnabled = library.IsAvailable;
    }

    // Providers and their availability can change while the inspector stays open.
    private List<MenuEntry> LibraryMenu()
    {
        if (
            _library is not { IsInspectorVisible: true } library
            || _downloadSubtitles is not { } subtitles
        )
            return [];
        var text = library.Text;
        List<MenuEntry> menu = [];
        if (ActionsOverflow)
            menu.Add(new MenuCommand(text.Get("commands", "open"), Lucide.Play, library.Open));
        menu.Add(
            new MenuCommand(text.Get("commands", "open_folder"), Lucide.FolderOpen, library.OpenFolder)
        );
        if (ActionsOverflow)
            menu.Add(OriginActions.Create(library));
        if (!library.SupportsMetadata)
            return menu;
        menu.AddRange(
        [
            new MenuSeparator(),
            MetadataActions.Create(library),
            new MenuCommand(
                text.Get("library", "refresh_metadata"),
                Lucide.RefreshCw,
                library.RefreshMetadata
            ),
            new MenuCommand(text.Get("subtitles", "choose"), Lucide.Download, subtitles),
            new MenuSeparator(),
            new MenuCommand(
                text.Get("library", "erase_metadata"),
                Lucide.Eraser,
                library.EraseMetadata
            ),
        ]);
        return menu;
    }

    private void RefreshLibraryOrigins()
    {
        if (_library is not { IsInspectorVisible: true } library || _libraryTorrents.Flyout?.IsOpen == true)
            return;
        OriginActions.Bind(_libraryTorrents, library);
        if (_libraryTorrents.Flyout is { } flyout)
            flyout.Closed += (_, _) =>
            {
                if (ReferenceEquals(_libraryTorrents.Flyout, flyout))
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
        ToolTipService.SetToolTip(_libraryOpen, tooltip);
    }

    // Hiding a menu releases its items.
    private void ClearLibraryActions()
    {
        _libraryOpen.Command = null;
        var more = _libraryMore.Flyout;
        _libraryMore.Flyout = null;
        more?.Hide();
        _libraryTorrents.Command = null;
        var origins = _libraryTorrents.Flyout;
        // Detach first so Closed cannot rebind the origins menu during cleanup.
        _libraryTorrents.Flyout = null;
        origins?.Hide();
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
