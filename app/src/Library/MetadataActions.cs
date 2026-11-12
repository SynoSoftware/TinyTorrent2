using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Library;

internal static class MetadataActions
{
    /// <summary>The Fetch metadata submenu, which lists providers with per-movie downloads.</summary>
    internal static MenuSubmenu Create(Browser browser) =>
        new(browser.FetchMetadataLabel, browser.FetchMetadataGlyph, [.. Providers(browser)])
        {
            IsEnabled = browser.FetchMetadata.CanExecute(null),
        };

    /// <summary>
    /// The metadata groups that the Library menu, the row context menu and the
    /// Inspector's More menu share; none when the selection holds no video.
    /// <paramref name="subtitles"/> downloads the selection's subtitles, a
    /// command of the window, which owns the subtitle dialogs.
    /// </summary>
    internal static IEnumerable<MenuEntry> Menu(Browser browser, ICommand subtitles)
    {
        if (!browser.SupportsMetadata)
        {
            return [];
        }
        var text = browser.Text;
        return
        [
            new MenuSeparator(),
            Create(browser),
            new MenuCommand(text.Get("library", "refresh_metadata"), Segoe.RefreshCw, browser.RefreshMetadata),
            new MenuCommand(
                text.Get("subtitles", browser.Selection.Items.Count == 1 ? "choose" : "download_title"),
                Segoe.Download,
                subtitles
            ),
            new MenuSeparator(),
            new MenuCommand(text.Get("library", "erase_metadata"), Segoe.Eraser, browser.EraseMetadata),
        ];
    }

    /// <summary>The Info page's Fetch button: per-movie providers, then provider settings.</summary>
    internal static ActionButton Button(Browser browser)
    {
        var row = browser.Selected;
        var button = new ActionButton
        {
            Glyph = browser.FetchMetadataGlyph,
            Text = browser.Text.Get("library", "fetch"),
            Flyout = MenuItems.Flyout(
                () =>
                {
                    if (!ReferenceEquals(row, browser.Selected))
                        return [];
                    return [
                        .. Providers(browser),
                        new MenuSeparator(),
                        new MenuCommand(
                            browser.Text.Get("commands", "settings"),
                            Segoe.Settings,
                            browser.ShowSettings
                        )
                        {
                            ToolTip = browser.Text.Get("library", "settings_tip"),
                        },
                    ];
                },
                () => browser.Text
            ),
        };
        ToolTipService.SetToolTip(button, browser.FetchMetadataLabel);
        return button;
    }

    private static IEnumerable<MenuEntry> Providers(Browser browser)
    {
        var row = browser.Selected;
        return browser.DownloadProviders.Select(option =>
            new MenuCommand(
                option.Name,
                Segoe.Database,
                new RelayCommand(
                    () => browser.Fetch(option),
                    () => ReferenceEquals(row, browser.Selected)
                        && browser.FetchMetadata.CanExecute(null)
                )
            )
        );
    }
}
