using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Library;

internal static class MetadataActions
{
    /// <summary>The Fetch metadata submenu, which lists providers with per-movie downloads.</summary>
    internal static MenuFlyoutSubItem Create(Browser browser)
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = browser.FetchMetadataLabel,
            Icon = new FontIcon { FontFamily = Lucide.Font, Glyph = browser.FetchMetadataGlyph },
            IsEnabled = browser.FetchMetadata.CanExecute(null),
        };
        foreach (var item in Providers(browser))
            menu.Items.Add(item);
        return menu;
    }

    /// <summary>
    /// The Info page's Fetch button: per-movie providers, then Settings, where
    /// downloading turns on. The menu is built each time it opens, because
    /// providers and their availability change after the page is built.
    /// </summary>
    internal static ActionButton Button(Browser browser)
    {
        var menu = new MenuFlyout();
        var button = new ActionButton
        {
            Glyph = browser.FetchMetadataGlyph,
            Text = browser.Text.Get("library", "fetch"),
            Flyout = menu,
        };
        ToolTipService.SetToolTip(button, browser.FetchMetadataLabel);
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var available = browser.FetchMetadata.CanExecute(null);
            foreach (var item in Providers(browser))
            {
                item.IsEnabled = available;
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var settings = new MenuFlyoutItem
            {
                Text = browser.Text.Get("commands", "settings"),
                Icon = new FontIcon { FontFamily = Lucide.Font, Glyph = Lucide.Settings },
                Command = browser.ShowSettings,
            };
            ToolTipService.SetToolTip(settings, browser.Text.Get("library", "settings_tip"));
            menu.Items.Add(settings);
        };
        return button;
    }

    private static IEnumerable<MenuFlyoutItem> Providers(Browser browser) =>
        browser.DownloadProviders.Select(option =>
        {
            var item = new MenuFlyoutItem { Text = option.Name };
            item.Click += (_, _) => browser.Fetch(option);
            return item;
        });
}
