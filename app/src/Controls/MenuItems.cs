using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Controls;

/// <summary>
/// One line of a menu, as the menu's owner describes it. <see cref="MenuItems"/> renders every
/// menu, so an owner says what its menu holds and never how the menu looks.
/// </summary>
internal abstract record MenuEntry;

/// <summary>What the menu acts on, such as the selected torrent. A separator always follows it.</summary>
internal sealed record MenuHeading(string Text, string Glyph) : MenuEntry;

internal sealed record MenuSeparator : MenuEntry;

internal sealed record MenuCommand(string Text, string Glyph, ICommand Command) : MenuEntry
{
    internal string AccessKey { get; init; } = string.Empty;

    /// <summary>The keyboard shortcut shown beside the text.</summary>
    internal string Shortcut { get; init; } = string.Empty;

    /// <summary>The spoken name, when the text alone does not say what the command acts on.</summary>
    internal string? Name { get; init; }

    internal string? ToolTip { get; init; }
}

/// <summary>
/// An on, off or mixed choice. In a menu of only choices its glyph shows beside a check mark;
/// in any other menu its state replaces the glyph.
/// </summary>
internal sealed record MenuChoice(string Text, string? Glyph, bool? State, ICommand Command)
    : MenuEntry
{
    internal string AccessKey { get; init; } = string.Empty;
}

internal sealed record MenuSubmenu(string Text, string Glyph, IReadOnlyList<MenuEntry> Entries)
    : MenuEntry
{
    internal string AccessKey { get; init; } = string.Empty;

    internal bool IsEnabled { get; init; } = true;
}

internal static class MenuItems
{
    private static readonly MenuSeparator Separator = new();

    /// <summary>
    /// A menu described again each time it opens, so it shows the current state. Closing it
    /// releases its commands.
    /// </summary>
    internal static MenuFlyout Flyout(Func<IReadOnlyList<MenuEntry>> describe, Func<Strings> text)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => Fill(menu.Items, describe(), text());
        menu.Closed += (_, _) => Clear(menu.Items);
        return menu;
    }

    /// <summary>
    /// Shows the entries in a menu that stays built, such as a MenuBar menu. While the entries
    /// keep their kinds and order, the items change in place, so an open menu stays open as
    /// its state changes.
    /// </summary>
    internal static void Fill(
        IList<MenuFlyoutItemBase> items,
        IReadOnlyList<MenuEntry> entries,
        Strings text
    )
    {
        var lines = entries
            .SelectMany(entry => entry is MenuHeading ? new[] { entry, Separator } : [entry])
            .ToArray();
        // One check mark gives every item in its menu a check column, so only a menu of
        // nothing but choices uses check marks; elsewhere a choice shows its state as its icon.
        var checks =
            lines.Any(line => line is MenuChoice)
            && lines.All(line => line is MenuChoice or MenuSeparator);
        if (
            items.Count != lines.Length
            || !items.Zip(lines).All(pair => Matches(pair.First, pair.Second, checks))
        )
        {
            Clear(items);
            foreach (var line in lines)
                items.Add(Create(line, checks));
        }
        for (var index = 0; index < lines.Length; index++)
            Update(items[index], lines[index], text);
    }

    private static bool Matches(MenuFlyoutItemBase item, MenuEntry line, bool checks) =>
        Equals(item.Tag, line.GetType().Name)
        && (item is ToggleMenuFlyoutItem) == (checks && line is MenuChoice);

    private static MenuFlyoutItemBase Create(MenuEntry line, bool checks)
    {
        MenuFlyoutItemBase item = line switch
        {
            MenuSeparator => new MenuFlyoutSeparator(),
            MenuSubmenu => new MenuFlyoutSubItem(),
            MenuChoice when checks => new ToggleMenuFlyoutItem(),
            MenuHeading => new MenuFlyoutItem
            {
                IsEnabled = false,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MaxWidth = 320,
            },
            _ => new MenuFlyoutItem(),
        };
        // A heading can name a long torrent.
        if (line is MenuHeading)
            item.Resources["MenuFlyoutItemTextTrimming"] = TextTrimming.CharacterEllipsis;
        // WinUI flips the check mark before Click. Flipping it back keeps the described
        // state, so choosing the current Library view leaves it checked.
        if (item is ToggleMenuFlyoutItem toggle)
            toggle.Click += (_, _) => toggle.IsChecked = !toggle.IsChecked;
        // A Type in Tag crosses WinRT as a type name, so the kind is kept as a string.
        item.Tag = line.GetType().Name;
        return item;
    }

    private static void Update(MenuFlyoutItemBase item, MenuEntry line, Strings text)
    {
        switch (line, item)
        {
            case (MenuHeading heading, MenuFlyoutItem shown):
                Show(shown, heading.Text, heading.Glyph, null, string.Empty);
                break;
            case (MenuCommand command, MenuFlyoutItem shown):
                Show(shown, command.Text, command.Glyph, command.Command, command.AccessKey);
                shown.KeyboardAcceleratorTextOverride = command.Shortcut;
                AutomationProperties.SetName(shown, command.Name ?? string.Empty);
                ToolTipService.SetToolTip(shown, command.ToolTip);
                break;
            case (MenuChoice choice, ToggleMenuFlyoutItem shown):
                Show(shown, choice.Text, choice.Glyph, choice.Command, choice.AccessKey);
                shown.IsChecked = choice.State == true;
                break;
            case (MenuChoice choice, MenuFlyoutItem shown):
                var glyph = choice.State switch
                {
                    true => Lucide.SquareCheck,
                    false => Lucide.Square,
                    null => Lucide.SquareMinus,
                };
                Show(shown, choice.Text, glyph, choice.Command, choice.AccessKey);
                AutomationProperties.SetItemStatus(
                    shown,
                    text.Get(
                        "menus",
                        choice.State switch
                        {
                            true => "on",
                            false => "off",
                            null => "mixed",
                        }
                    )
                );
                break;
            case (MenuSubmenu submenu, MenuFlyoutSubItem shown):
                shown.Text = submenu.Text;
                var icon = Icon(shown.Icon, submenu.Glyph);
                if (icon != shown.Icon)
                    shown.Icon = icon;
                shown.AccessKey = submenu.AccessKey;
                shown.IsEnabled = submenu.IsEnabled;
                Fill(shown.Items, submenu.Entries, text);
                break;
        }
    }

    private static void Show(
        MenuFlyoutItem item,
        string text,
        string? glyph,
        ICommand? command,
        string accessKey
    )
    {
        item.Text = text;
        var icon = Icon(item.Icon, glyph);
        if (icon != item.Icon)
            item.Icon = icon;
        item.Command = command;
        item.AccessKey = accessKey;
    }

    // A menu bar menu refills while it is open, so a refill keeps the shown icon and
    // changes only its glyph instead of replacing the icon.
    private static IconElement? Icon(IconElement? icon, string? glyph)
    {
        if (glyph is null)
            return null;
        if (icon is FontIcon font)
        {
            font.Glyph = glyph;
            return font;
        }
        return new FontIcon { FontFamily = Lucide.Font, Glyph = glyph };
    }

    // Removes items one at a time: a MenuBarItem mirrors each removal into its flyout but
    // ignores Clear, which left old entries behind after a language change. Dropping each
    // command ends the item's subscription to it.
    private static void Clear(IList<MenuFlyoutItemBase> items)
    {
        while (items.Count > 0)
        {
            switch (items[^1])
            {
                case MenuFlyoutItem item:
                    item.Command = null;
                    break;
                case MenuFlyoutSubItem submenu:
                    Clear(submenu.Items);
                    break;
            }
            items.RemoveAt(items.Count - 1);
        }
    }
}
