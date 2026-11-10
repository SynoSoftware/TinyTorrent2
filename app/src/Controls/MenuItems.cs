using System.Runtime.CompilerServices;
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

    /// <summary>The state the glyph shows, spoken after the name.</summary>
    internal string? Status { get; init; }

    /// <summary>
    /// Whether a click leaves a <see cref="MenuItems.Flyout"/> menu open, as a choice does. A
    /// MenuBar menu closes, because WinUI gives no access to its close.
    /// </summary>
    internal bool KeepsMenuOpen { get; init; }
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

internal static partial class MenuItems
{
    private static readonly MenuSeparator Separator = new();

    private static readonly ConditionalWeakTable<MenuFlyout, Action> Refills = new();

    /// <summary>
    /// A menu described again each time it opens, so it shows the current state. Closing it
    /// releases its commands. A choice, in the menu or one of its submenus, leaves it open and
    /// describes it again, as does a command that keeps it open; a click outside closes it.
    /// </summary>
    internal static MenuFlyout Flyout(Func<IReadOnlyList<MenuEntry>> describe, Func<Strings> text) =>
        Menu(describe, text, choicesKeepOpen: true);

    /// <summary>
    /// A button's menu that chooses one value, such as the language. A choice closes it, as it
    /// closes a ComboBox.
    /// </summary>
    internal static MenuFlyout Picker(Func<IReadOnlyList<MenuEntry>> describe, Func<Strings> text) =>
        Menu(describe, text, choicesKeepOpen: false);

    private static MenuFlyout Menu(
        Func<IReadOnlyList<MenuEntry>> describe,
        Func<Strings> text,
        bool choicesKeepOpen
    )
    {
        var menu = new MenuFlyout();
        // Until a click keeps the menu open, it stays as it opened, so no entry moves
        // under the pointer.
        var kept = false;
        Action? Retain(MenuEntry entry) => entry switch
        {
            MenuChoice when choicesKeepOpen => Changed,
            MenuCommand { KeepsMenuOpen: true } => Changed,
            _ => null,
        };
        void Changed()
        {
            kept = true;
            Refill();
        }
        void Refill()
        {
            if (kept && menu.IsOpen)
                Fill(menu.Items, describe(), text(), Retain);
        }
        menu.Opening += (_, _) =>
        {
            kept = false;
            Fill(menu.Items, describe(), text(), Retain);
        };
        menu.Closed += (_, _) => Remove(menu.Items, 0, menu.Items.Count);
        Refills.Add(menu, Refill);
        return menu;
    }

    /// <summary>
    /// Describes an open <see cref="Flyout"/> menu again after a click kept it open, for state
    /// that changes after that click.
    /// </summary>
    internal static void Refresh(MenuFlyout menu)
    {
        if (Refills.TryGetValue(menu, out var refill))
            refill();
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
    ) => Fill(items, entries, text, retain: null);

    private static void Fill(
        IList<MenuFlyoutItemBase> items,
        IReadOnlyList<MenuEntry> entries,
        Strings text,
        Func<MenuEntry, Action?>? retain
    )
    {
        var lines = entries
            .SelectMany(entry => entry is MenuHeading ? new[] { entry, Separator } : [entry])
            .ToArray();
        // A native check reserves a second column for every row. Keep it only
        // when choices have content icons to show beside their state.
        var checks = lines.OfType<MenuChoice>().All(choice =>
            choice.Glyph is not null || choice.FlagRegion is not null);
        // The items before the first change and after the last one stay, so the
        // item that a click kept open stays under the pointer and keeps keyboard
        // focus, and a line that comes or goes re-creates no other item.
        bool Keeps(MenuFlyoutItemBase item, MenuEntry line) =>
            Matches(item, line, checks, retain?.Invoke(line) is not null);
        var head = 0;
        while (head < items.Count && head < lines.Length && Keeps(items[head], lines[head]))
            head++;
        var tail = 0;
        while (
            tail < items.Count - head
            && tail < lines.Length - head
            && Keeps(items[items.Count - 1 - tail], lines[lines.Length - 1 - tail])
        )
            tail++;
        Remove(items, head, items.Count - tail);
        for (var index = head; index < lines.Length - tail; index++)
            items.Insert(index, Create(lines[index], items, checks, retain?.Invoke(lines[index])));
        for (var index = 0; index < lines.Length; index++)
            Update(items[index], lines[index], text, retain);
    }

    private static bool Matches(MenuFlyoutItemBase item, MenuEntry line, bool checks, bool retained) =>
        Equals(item.Tag, line.GetType().Name)
        && retained == (item is Item or Toggle)
        && (line switch
        {
            MenuChoice choice when !checks || choice.State is null => item is MenuFlyoutItem and not ToggleMenuFlyoutItem and not RadioMenuFlyoutItem,
            MenuChoice { Group: not null } => item is RadioMenuFlyoutItem or Toggle { IsRadio: true },
            MenuChoice => item is ToggleMenuFlyoutItem and not Toggle { IsRadio: true },
            _ => true,
        });

    private static MenuFlyoutItemBase Create(
        MenuEntry line,
        IList<MenuFlyoutItemBase> siblings,
        bool checks,
        Action? changed
    )
    {
        MenuFlyoutItemBase item = line switch
        {
            MenuSeparator => new MenuFlyoutSeparator(),
            MenuSubmenu => new MenuFlyoutSubItem(),
            MenuChoice choice when !checks || choice.State is null =>
                changed is null ? new MenuFlyoutItem() : new Item(changed),
            MenuChoice { Group: not null } =>
                changed is null ? new RadioMenuFlyoutItem() : new Toggle(changed, radio: true),
            MenuChoice => changed is null ? new ToggleMenuFlyoutItem() : new Toggle(changed),
            MenuHeading => new MenuFlyoutItem
            {
                IsEnabled = false,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MaxWidth = 320,
            },
            _ => changed is null ? new MenuFlyoutItem() : new Item(changed),
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

    private static void Update(MenuFlyoutItemBase item, MenuEntry line, Strings text,
        Func<MenuEntry, Action?>? retain)
    {
        if ((line is MenuCommand or MenuHeading or MenuSeparator) && Equals(item.DataContext, line))
            return;
        // A choice's spoken state is in the current language, which its entry
        // does not hold. Its check needs no reset: each item's Click restores it.
        if (
            line is MenuChoice same
            && Equals(item.DataContext, line)
            && AutomationProperties.GetItemStatus(item) == Status(same, text)
        )
            return;
        item.DataContext = line;
        switch (line, item)
        {
            case (MenuHeading heading, MenuFlyoutItem shown):
                Show(shown, heading.Text, Segoe.Font, heading.Glyph, null, string.Empty);
                break;
            case (MenuCommand command, MenuFlyoutItem shown):
                Show(shown, command.Text, Segoe.Font, command.Glyph, command.Command, command.AccessKey);
                shown.KeyboardAcceleratorTextOverride = command.Shortcut;
                AutomationProperties.SetName(shown, command.Name ?? string.Empty);
                AutomationProperties.SetItemStatus(shown, command.Status ?? string.Empty);
                ToolTipService.SetToolTip(shown, command.ToolTip);
                break;
            case (MenuChoice choice, MenuFlyoutItem shown):
                var checks = shown is ToggleMenuFlyoutItem or RadioMenuFlyoutItem;
                if (checks && choice.FlagRegion is { } region)
                    Show(shown, choice.Text, Flags.Font, Flags.Glyph(region), choice.Command, choice.AccessKey);
                else
                    Show(shown, choice.Text, Segoe.Font,
                        checks ? choice.Glyph : (choice.Group, choice.State) switch
                        {
                            (not null, true) => Segoe.RadioButtonOn,
                            (not null, _) => Segoe.RadioButton,
                            (_, true) => Segoe.SquareCheck,
                            (_, false) => Segoe.Square,
                            (_, null) => Segoe.SquareMinus,
                        },
                        choice.Command, choice.AccessKey);
                if (shown is ToggleMenuFlyoutItem toggle)
                    toggle.IsChecked = choice.State == true;
                if (shown is RadioMenuFlyoutItem radio)
                {
                    radio.GroupName = choice.Group;
                    radio.IsChecked = choice.State == true;
                }
                AutomationProperties.SetItemStatus(shown, Status(choice, text));
                break;
            case (MenuSubmenu submenu, MenuFlyoutSubItem shown):
                shown.Text = submenu.Text;
                var icon = Icon(shown.Icon, Segoe.Font, submenu.Glyph);
                if (icon != shown.Icon)
                    shown.Icon = icon;
                shown.AccessKey = submenu.AccessKey;
                shown.IsEnabled = submenu.IsEnabled;
                Fill(
                    shown.Items,
                    submenu.Entries,
                    text,
                    retain
                );
                break;
        }
    }

    private static string Status(MenuChoice choice, Strings text) =>
        text.Get(
            "menus",
            choice.State switch
            {
                true => "on",
                false => "off",
                null => "mixed",
            }
        );

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

    // Removes the items from start up to end one at a time: a MenuBarItem mirrors each
    // removal into its flyout but ignores Clear, which left old entries behind after a
    // language change. Dropping each command ends the item's subscription to it.
    private static void Remove(IList<MenuFlyoutItemBase> items, int start, int end)
    {
        for (var index = end - 1; index >= start; index--)
        {
            switch (items[index])
            {
                case MenuFlyoutItem item:
                    item.Command = null;
                    break;
                case MenuFlyoutSubItem submenu:
                    Remove(submenu.Items, 0, submenu.Items.Count);
                    break;
            }
            items.RemoveAt(index);
        }
    }
}
