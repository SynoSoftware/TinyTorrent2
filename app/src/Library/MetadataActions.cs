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
                            Lucide.Settings,
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
                Lucide.Database,
                new RelayCommand(
                    () => browser.Fetch(option),
                    () => ReferenceEquals(row, browser.Selected)
                        && browser.FetchMetadata.CanExecute(null)
                )
            )
        );
    }
}
