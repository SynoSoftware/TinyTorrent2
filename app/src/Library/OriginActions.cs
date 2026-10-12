using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Library;

internal static class OriginActions
{
    internal static MenuFlyoutItemBase Create(Browser browser)
    {
        var text = browser.Text.Get("library", "show_torrents");
        if (browser.Origins.Count <= 1)
        {
            var origin = browser.Origins.FirstOrDefault();
            return new MenuFlyoutItem
            {
                Text = text,
                Icon = Icon(),
                IsEnabled = browser.IsAvailable && origin is not null,
                Command = origin is null ? null : Command(browser, origin),
            };
        }
        var menu = new MenuFlyoutSubItem
        {
            Text = text,
            Icon = Icon(),
            IsEnabled = browser.IsAvailable,
        };
        foreach (var origin in browser.Origins)
            menu.Items.Add(
                new MenuFlyoutItem
                {
                    Text = origin.Name,
                    Icon = Icon(),
                    Command = Command(browser, origin),
                }
            );
        return menu;
    }

    internal static void Bind(Button button, Browser browser)
    {
        button.Command = null;
        button.Flyout?.Hide();
        button.Flyout = null;
        var action = Create(browser);
        button.IsEnabled = action.IsEnabled;
        if (action is MenuFlyoutItem command)
            button.Command = command.Command;
        else if (action is MenuFlyoutSubItem choices)
        {
            var menu = new MenuFlyout();
            while (choices.Items.Count > 0)
            {
                var item = choices.Items[0];
                choices.Items.RemoveAt(0);
                menu.Items.Add(item);
            }
            button.Flyout = menu;
        }
    }

    private static FontIcon Icon() =>
        new() { FontFamily = Lucide.Font, Glyph = Lucide.LocateFixed };

    private static RelayCommand Command(Browser browser, Origin origin) =>
        new(
            () =>
            {
                browser.ShowTorrent(origin.OriginId);
                return Task.CompletedTask;
            },
            () => browser.IsAvailable
        );
}
