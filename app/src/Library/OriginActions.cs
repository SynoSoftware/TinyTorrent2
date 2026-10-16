using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Library;

internal static class OriginActions
{
    internal static MenuEntry Create(Browser browser)
    {
        var text = browser.Text.Get("library", "show_torrents");
        if (browser.Origins.Count <= 1)
            return new MenuCommand(
                text,
                Lucide.LocateFixed,
                Command(browser, browser.Origins.FirstOrDefault())
            );
        return new MenuSubmenu(
            text,
            Lucide.LocateFixed,
            [
                .. browser.Origins.Select(origin => new MenuCommand(
                    origin.Name,
                    Lucide.LocateFixed,
                    Command(browser, origin)
                )),
            ]
        )
        {
            IsEnabled = browser.CanShowTorrent,
        };
    }

    internal static void Bind(Button button, Browser browser)
    {
        button.Command = null;
        button.Flyout?.Hide();
        button.Flyout = null;
        var action = Create(browser);
        button.IsEnabled = action is not MenuSubmenu { IsEnabled: false };
        if (action is MenuCommand command)
            button.Command = command.Command;
        else if (action is MenuSubmenu choices)
            button.Flyout = MenuItems.Flyout(() => choices.Entries, () => browser.Text);
    }

    private static RelayCommand Command(Browser browser, Origin? origin) =>
        new(
            () =>
            {
                if (origin is not null)
                    browser.ShowTorrent(origin.OriginId);
            },
            () => browser.CanShowTorrent && origin is not null
        );
}
