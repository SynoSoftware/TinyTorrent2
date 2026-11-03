using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
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

internal static class MenuItems
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
        // WinUI has no stay-open switch, but the close that a click raises can be refused.
        // The hold ends on the next dispatcher turn, after that close was refused.
        var holding = false;
        // Until a click keeps the menu open, it stays as it opened, so no entry moves
        // under the pointer.
        var kept = false;
        void Hold(MenuEntry entry, IReadOnlyList<MenuFlyoutSubItem> submenus)
        {
            var keepsOpen = entry switch
            {
                MenuChoice => choicesKeepOpen,
                MenuCommand command => command.KeepsMenuOpen,
                _ => false,
            };
            if (!keepsOpen)
                return;
            holding = true;
            kept = true;
            menu.DispatcherQueue.TryEnqueue(() =>
            {
                holding = false;
                Refill();
                // WinUI closes the submenus before the close that can be refused, so the
                // submenus that held the click open again.
                foreach (var submenu in submenus)
                    if (menu.IsOpen && submenu.IsLoaded && submenu.IsEnabled)
                        Expand(submenu);
            });
        }
        void Refill()
        {
            if (kept && menu.IsOpen)
                Fill(menu.Items, describe(), text(), Hold);
        }
        menu.Opening += (_, _) =>
        {
            kept = false;
            Fill(menu.Items, describe(), text(), Hold);
        };
        menu.Closing += (_, closing) => closing.Cancel = holding;
        menu.Closed += (_, _) => Truncate(menu.Items, 0);
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
    ) => Fill(items, entries, text, clicked: null);

    /// <summary>
    /// Told about a click on an entry, with the submenus that hold it, outermost first.
    /// </summary>
    private delegate void Clicked(MenuEntry entry, IReadOnlyList<MenuFlyoutSubItem> submenus);

    private static void Fill(
        IList<MenuFlyoutItemBase> items,
        IReadOnlyList<MenuEntry> entries,
        Strings text,
        Clicked? clicked
    )
    {
        var lines = entries
            .SelectMany(entry => entry is MenuHeading ? new[] { entry, Separator } : [entry])
            .ToArray();
        // A native check reserves a second column for every row. Keep it only
        // when choices have content icons to show beside their state.
        var checks = lines.OfType<MenuChoice>().All(choice =>
            choice.Glyph is not null || choice.FlagRegion is not null);
        // The items before the first change stay, so the item that a click kept
        // open stays under the pointer and keeps keyboard focus.
        var unchanged = 0;
        while (
            unchanged < items.Count
            && unchanged < lines.Length
            && Matches(items[unchanged], lines[unchanged], checks)
        )
            unchanged++;
        Truncate(items, unchanged);
        for (var index = unchanged; index < lines.Length; index++)
            items.Add(Create(lines[index], items, checks, clicked));
        for (var index = 0; index < lines.Length; index++)
            Update(items[index], lines[index], text, clicked);
    }

    private static bool Matches(MenuFlyoutItemBase item, MenuEntry line, bool checks) =>
        Equals(item.Tag, line.GetType().Name)
        && (line switch
        {
            MenuChoice choice when !checks || choice.State is null => item is MenuFlyoutItem and not ToggleMenuFlyoutItem and not RadioMenuFlyoutItem,
            MenuChoice { Group: not null } => item is RadioMenuFlyoutItem,
            MenuChoice => item is ToggleMenuFlyoutItem,
            _ => true,
        });

    private static MenuFlyoutItemBase Create(
        MenuEntry line,
        IList<MenuFlyoutItemBase> siblings,
        bool checks,
        Clicked? clicked
    )
    {
        MenuFlyoutItemBase item = line switch
        {
            MenuSeparator => new MenuFlyoutSeparator(),
            MenuSubmenu => new MenuFlyoutSubItem(),
            MenuChoice choice when !checks || choice.State is null => new MenuFlyoutItem(),
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
        if (clicked is not null && item is MenuFlyoutItem invoked)
            invoked.Click += (_, _) => clicked((MenuEntry)invoked.DataContext, []);
        // A Type in Tag crosses WinRT as a type name, so the kind is kept as a string.
        item.Tag = line.GetType().Name;
        return item;
    }

    private static void Update(MenuFlyoutItemBase item, MenuEntry line, Strings text, Clicked? clicked)
    {
        if ((line is MenuCommand or MenuHeading or MenuSeparator) && Equals(item.DataContext, line))
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
                        checks ? choice.Glyph : choice.State switch
                        {
                            true => Segoe.SquareCheck,
                            false => Segoe.Square,
                            null => Segoe.SquareMinus,
                        },
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
                var icon = Icon(shown.Icon, Segoe.Font, submenu.Glyph);
                if (icon != shown.Icon)
                    shown.Icon = icon;
                shown.AccessKey = submenu.AccessKey;
                shown.IsEnabled = submenu.IsEnabled;
                Fill(
                    shown.Items,
                    submenu.Entries,
                    text,
                    clicked is null ? null : (entry, submenus) => clicked(entry, [shown, .. submenus])
                );
                break;
        }
    }

    private static void Expand(MenuFlyoutSubItem submenu) =>
        ((IExpandCollapseProvider)FrameworkElementAutomationPeer
            .CreatePeerForElement(submenu)
            .GetPattern(PatternInterface.ExpandCollapse)).Expand();

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

    // Removes the items after the first count one at a time: a MenuBarItem mirrors each
    // removal into its flyout but ignores Clear, which left old entries behind after a
    // language change. Dropping each command ends the item's subscription to it.
    private static void Truncate(IList<MenuFlyoutItemBase> items, int count)
    {
        while (items.Count > count)
        {
            switch (items[^1])
            {
                case MenuFlyoutItem item:
                    item.Command = null;
                    break;
                case MenuFlyoutSubItem submenu:
                    Truncate(submenu.Items, 0);
                    break;
            }
            items.RemoveAt(items.Count - 1);
        }
    }
}
