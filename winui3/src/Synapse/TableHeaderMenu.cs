using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Synapse;

/// <summary>
/// Section 12's generated header menu. It is table mechanics only and has no extension surface.
/// Every item invokes the operation its pointer gesture invokes, so a command and a gesture cannot
/// disagree about what a legal result is, and every item is enabled by the operation's own
/// predicate rather than by a second copy of the rule. Generic labels come from the control's own
/// resources; column names come from the host's <see cref="TableColumn.DisplayName"/>.
/// </summary>
internal static class TableHeaderMenu
{
    /// <summary>
    /// The menu for <paramref name="active"/>, or the one unused header space gets when there is no
    /// active column: the same menu without the actions that need one.
    /// </summary>
    internal static MenuFlyout Create(TableView owner, ResolvedColumn? active)
    {
        MenuFlyout menu = new();

        // Nothing in this menu closes it. Every item is repeated by nature — showing and hiding
        // columns, nudging one left until it sits where you want it, fitting one and then another —
        // or is immediately worth undoing, which amounts to the same thing. WinUI has no stay-open
        // switch on MenuFlyout or on any of its items, but the close itself can be refused, so the
        // flag below refuses every attempt an invocation produces and is dropped on the next turn
        // of the dispatcher once those attempts are done. A click outside still dismisses it.
        bool holdOpen = false;
        menu.Closing += (_, closing) => closing.Cancel = holdOpen;

        // And because it stays open, every item has to be right again afterwards rather than merely
        // right when it was built. Hiding a column can leave another as the last visible one and
        // disable its entry; moving a column to an edge disables the command that moved it there;
        // hiding the column the menu was opened on turns that item into the one that shows it back.
        // So an item declares its label, its icon and whether it is enabled as questions about the
        // current state, and this list re-asks all of them after every invocation. No item can be
        // left saying something that has stopped being true.
        List<Action> refresh = new();

        MenuFlyoutItem Item(
            Func<string> text,
            Func<IconElement?> icon,
            Func<bool> enabled,
            Action invoke,
            Func<string>? status = null)
        {
            MenuFlyoutItem item = new();

            void Update()
            {
                item.Text = text();
                item.Icon = icon();
                item.IsEnabled = enabled();

                if (status is not null)
                {
                    AutomationProperties.SetItemStatus(item, status());
                }
            }

            item.Click += (_, _) =>
            {
                holdOpen = true;
                invoke();

                foreach (Action update in refresh)
                {
                    update();
                }

                menu.DispatcherQueue.TryEnqueue(() => holdOpen = false);
            };

            refresh.Add(Update);
            Update();
            return item;
        }

        if (active is not null)
        {
            // One item for both directions. The column it names is the one the menu was opened on,
            // so hiding it leaves the menu standing over a column that is gone; the item that hid
            // it is then the obvious place to get it back, and a second entry saying so would be a
            // second control for one state.
            menu.Items.Add(Item(
                () => active.IsVisible
                    ? TableResources.HideColumn(active.Column.DisplayName)
                    : TableResources.ShowColumn(active.Column.DisplayName),
                () => active.IsVisible ? TableIcons.HideColumn() : TableIcons.ShowColumn(),
                () => !active.IsVisible || owner.CanHideColumn(active),
                () => owner.SetColumnVisibility(active, !active.IsVisible)));

            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item(
                () => TableResources.FitColumn(active.Column.DisplayName),
                TableIcons.FitColumn,
                () => owner.CanFitColumn(active),
                () => owner.AutoFitColumn(active)));
        }

        // A right-click on unused header space has no column to act on, so the whole menu is about
        // the column set: this command and the list below it, with no door between them.
        menu.Items.Add(Item(
            () => TableResources.FitVisibleColumns,
            TableIcons.FitVisibleColumns,
            () => owner.CanFitVisibleColumns,
            owner.AutoFitVisibleColumns));

        // No Narrow and no Widen. They stepped 8 DIPs, so widening the host's 150 DIP name column
        // to something readable was thirteen invocations. That count came from the torrent host's
        // own fitted width, and the harness that produced it has been deleted, so specification 10
        // records the figure and nothing here reproduces it; what survives is the arithmetic, one
        // invocation per 8 DIPs however far a column has to go. That is not a keyboard route to
        // resizing, it is the appearance of one, and fitting reaches the outcome anyone actually
        // wants in a single invocation. If continuous keyboard resizing is wanted it belongs on the
        // focused header as a held key, where repeat does the work.

        if (active is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item(
                () => TableResources.MoveLeft,
                TableIcons.MoveLeft,
                () => owner.CanMoveColumnBy(active, -1),
                () => owner.MoveColumnBy(active, -1)));
            menu.Items.Add(Item(
                () => TableResources.MoveRight,
                TableIcons.MoveRight,
                () => owner.CanMoveColumnBy(active, 1),
                () => owner.MoveColumnBy(active, 1)));
        }

        // The column list, in this menu rather than in a submenu of it. A submenu is a second popup
        // with its own dismissal, and MenuFlyoutSubItem exposes no way to refuse it: the root menu
        // could be held open across a change and the list still collapsed underneath, so turning
        // three columns on cost three trips back through Columns. Listed here there is one popup to
        // hold open. It is also what the reference does — its column picker is a flat section of the
        // same menu, not a nested one.
        menu.Items.Add(new MenuFlyoutSeparator());

        foreach (ResolvedColumn column in owner.Geometry.Order)
        {
            // A plain item carrying its state as an icon, not a ToggleMenuFlyoutItem. A toggle keeps
            // its check in a column of its own that holds its width even while the check is
            // invisible, so one standing beside items that carry icons gives the menu two glyph
            // columns and pushes every label past both.
            ResolvedColumn target = column;
            menu.Items.Add(Item(
                () => target.Column.DisplayName,
                () => target.IsVisible ? TableIcons.Shown() : null,
                () => !target.IsVisible || owner.CanHideColumn(target),
                () => owner.SetColumnVisibility(target, !target.IsVisible),
                () => target.IsVisible ? TableResources.ColumnShown : TableResources.ColumnHidden));
        }

        return menu;
    }

}
