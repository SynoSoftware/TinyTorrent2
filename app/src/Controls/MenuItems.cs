using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Shared;

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
/// An on, off or mixed choice; a group makes it mutually exclusive with its peers.
/// </summary>
internal sealed record MenuChoice(string Text, string? Glyph, bool? State, ICommand Command)
    : MenuEntry
{
    internal string AccessKey { get; init; } = string.Empty;

    internal string? Group { get; init; }

    /// <summary>The ISO 3166-1 alpha-2 region whose flag shows in place of the glyph.</summary>
    internal string? FlagRegion { get; init; }
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
        if (
            items.Count != lines.Length
            || !items.Zip(lines).All(pair => Matches(pair.First, pair.Second))
        )
        {
            Clear(items);
            foreach (var line in lines)
                items.Add(Create(line, items));
        }
        for (var index = 0; index < lines.Length; index++)
            Update(items[index], lines[index], text);
    }

    private static bool Matches(MenuFlyoutItemBase item, MenuEntry line) =>
        Equals(item.Tag, line.GetType().Name)
        && (line switch
        {
            MenuChoice { State: null } => item is MenuFlyoutItem and not ToggleMenuFlyoutItem and not RadioMenuFlyoutItem,
            MenuChoice { Group: not null } => item is RadioMenuFlyoutItem,
            MenuChoice => item is ToggleMenuFlyoutItem,
            _ => true,
        });

    private static MenuFlyoutItemBase Create(MenuEntry line, IList<MenuFlyoutItemBase> siblings)
    {
        MenuFlyoutItemBase item = line switch
        {
            MenuSeparator => new MenuFlyoutSeparator(),
            MenuSubmenu => new MenuFlyoutSubItem(),
            MenuChoice { State: null } => new MenuFlyoutItem(),
            MenuChoice { Group: not null } => new RadioMenuFlyoutItem(),
            MenuChoice => new ToggleMenuFlyoutItem(),
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
        // Navigation may be refused by an unfinished draft, so the owner's
        // state remains authoritative after WinUI changes a choice on Click.
        if (item is ToggleMenuFlyoutItem toggle)
            toggle.Click += (_, _) => toggle.IsChecked = ((MenuChoice)toggle.DataContext).State == true;
        if (item is RadioMenuFlyoutItem radio)
            radio.Click += (_, _) =>
            {
                foreach (var peer in siblings.OfType<RadioMenuFlyoutItem>())
                    if (peer.GroupName == radio.GroupName && peer.DataContext is MenuChoice choice)
                        peer.IsChecked = choice.State == true;
            };
        // A Type in Tag crosses WinRT as a type name, so the kind is kept as a string.
        item.Tag = line.GetType().Name;
        return item;
    }

    private static void Update(MenuFlyoutItemBase item, MenuEntry line, Strings text)
    {
        if ((line is MenuCommand or MenuHeading or MenuSeparator) && Equals(item.DataContext, line))
            return;
        item.DataContext = line;
        switch (line, item)
        {
            case (MenuHeading heading, MenuFlyoutItem shown):
                Show(shown, heading.Text, Lucide.Font, heading.Glyph, null, string.Empty);
                break;
            case (MenuCommand command, MenuFlyoutItem shown):
                Show(shown, command.Text, Lucide.Font, command.Glyph, command.Command, command.AccessKey);
                shown.KeyboardAcceleratorTextOverride = command.Shortcut;
                AutomationProperties.SetName(shown, command.Name ?? string.Empty);
                ToolTipService.SetToolTip(shown, command.ToolTip);
                break;
            case (MenuChoice choice, MenuFlyoutItem shown):
                if (choice.FlagRegion is { } region)
                    Show(shown, choice.Text, Flags.Font, Flags.Glyph(region), choice.Command, choice.AccessKey);
                else
                    Show(shown, choice.Text, Lucide.Font,
                        choice.State is null ? Lucide.SquareMinus : choice.Glyph,
                        choice.Command, choice.AccessKey);
                if (shown is ToggleMenuFlyoutItem toggle)
                    toggle.IsChecked = choice.State == true;
                if (shown is RadioMenuFlyoutItem radio)
                {
                    radio.GroupName = choice.Group;
                    radio.IsChecked = choice.State == true;
                }
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
                var icon = Icon(shown.Icon, Lucide.Font, submenu.Glyph);
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
        FontFamily font,
        string? glyph,
        ICommand? command,
        string accessKey
    )
    {
        item.Text = text;
        var icon = Icon(item.Icon, font, glyph);
        if (icon != item.Icon)
            item.Icon = icon;
        item.Command = command;
        item.AccessKey = accessKey;
    }

    // A menu bar menu refills while it is open, so a refill keeps the shown icon and
    // changes only its glyph instead of replacing the icon.
    private static IconElement? Icon(IconElement? icon, FontFamily font, string? glyph)
    {
        if (glyph is null)
            return null;
        if (icon is FontIcon shown)
        {
            shown.FontFamily = font;
            shown.Glyph = glyph;
            return shown;
        }
        return new FontIcon { FontFamily = font, Glyph = glyph };
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
