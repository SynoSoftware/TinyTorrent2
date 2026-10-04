using Microsoft.Windows.ApplicationModel.Resources;

namespace Synapse;

/// <summary>
/// The control's own localized strings. The library reads its own resource file rather than the
/// host application's.
/// </summary>
internal static class TableResources
{
    private static readonly ResourceLoader Loader =
        new(ResourceLoader.GetDefaultResourceFilePath(), "Synapse/Resources");

    internal static string HeaderStripAccessibleName => Get("HeaderStrip_AccessibleName");

    // Section 17: what the control shows in place of rows when the host configured no content of
    // its own. The loading one is not drawn — it names the ring for UI Automation.
    internal static string Loading => Get("Placeholder_Loading");

    internal static string Empty => Get("Placeholder_Empty");

    internal static string NoResults => Get("Placeholder_NoResults");

    // Section 9: the active header's sort state, read by UI Automation.
    internal static string SortedAscending => Get("Header_SortedAscending");

    internal static string SortedDescending => Get("Header_SortedDescending");

    // Section 12: the generated menu's action labels. They are the control's own strings and are
    // not host-overridable; only the column names in them come from the host.
    //
    // The three that act on one column name it. "Hide this column" was ambiguous the moment the
    // column list moved into the same menu: "this" meant the column the menu was opened on, while
    // the names directly below it meant themselves, and nothing on screen said which was which.
    internal static string HideColumn(string name) => Format("ColumnMenu_HideColumn", name);

    internal static string ShowColumn(string name) => Format("ColumnMenu_ShowColumn", name);

    internal static string FitColumn(string name) => Format("ColumnMenu_FitColumn", name);

    internal static string FitVisibleColumns => Get("ColumnMenu_FitVisibleColumns");

    internal static string MoveLeft => Get("ColumnMenu_MoveLeft");

    internal static string MoveRight => Get("ColumnMenu_MoveRight");

    // Section 16: a live row drag's destination, read by UI Automation.
    internal static string DropBeforeRow => Get("RowDrag_DropBeforeRow");

    internal static string DropAtEnd => Get("RowDrag_DropAtEnd");

    // Section 19: the column list draws its state as an icon rather than as a toggle's own check,
    // so the state has to be said rather than left to the toggle pattern to report.
    internal static string ColumnShown => Get("ColumnMenu_ColumnShown");

    internal static string ColumnHidden => Get("ColumnMenu_ColumnHidden");

    private static string Get(string name) => Loader.GetString(name);

    /// <summary>
    /// A label that carries a column's name. The current culture, not the invariant one: this is
    /// text a person reads, and the name is placed into a sentence the translation owns.
    /// </summary>
    private static string Format(string name, params object[] values) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(name), values);
}
