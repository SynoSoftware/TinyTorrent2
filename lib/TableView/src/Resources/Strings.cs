using System.Text.Json;

namespace Syno.TableView;

/// <summary>
/// The control's own text, from the en.json embedded in this library rather than from the host
/// application's resources.
/// </summary>
internal static class Strings
{
    private static readonly Dictionary<string, Dictionary<string, string>> Text = Load();

    internal static string HeaderStripAccessibleName => Get("header", "accessible_name");

    // Section 17: what the control shows in place of rows when the host configured no content of
    // its own. The loading one is not drawn — it names the ring for UI Automation.
    internal static string Loading => Get("placeholder", "loading");

    internal static string Empty => Get("placeholder", "empty");

    internal static string NoResults => Get("placeholder", "no_results");

    // Section 9: the active header's sort state, read by UI Automation.
    internal static string SortedAscending => Get("header", "sorted_ascending");

    internal static string SortedDescending => Get("header", "sorted_descending");

    // Section 12: the generated menu's action labels. They are the control's own strings and are
    // not host-overridable; only the column names in them come from the host.
    //
    // The three that act on one column name it. "Hide this column" was ambiguous the moment the
    // column list moved into the same menu: "this" meant the column the menu was opened on, while
    // the names directly below it meant themselves, and nothing on screen said which was which.
    internal static string HideColumn(string name) => Format("column_menu", "hide", name);

    internal static string ShowColumn(string name) => Format("column_menu", "show", name);

    internal static string FitColumn(string name) => Format("column_menu", "fit", name);

    internal static string FitVisibleColumns => Get("column_menu", "fit_visible");

    internal static string MoveLeft => Get("column_menu", "move_left");

    internal static string MoveRight => Get("column_menu", "move_right");

    // Section 16: a live row drag's destination, read by UI Automation. In the before-row text,
    // {0} is the destination row's position and {1} the number of rows.
    internal static string DropBeforeRow => Get("row_drag", "before_row");

    internal static string DropAtEnd => Get("row_drag", "at_end");

    // Section 19: the column list draws its state as an icon rather than as a toggle's own check,
    // so the state has to be said rather than left to the toggle pattern to report.
    internal static string ColumnShown => Get("column_menu", "shown");

    internal static string ColumnHidden => Get("column_menu", "hidden");

    private static string Get(string group, string key) => Text[group][key];

    /// <summary>
    /// A label that carries a column's name. The current culture, not the invariant one: this is
    /// text a person reads, and the name is placed into a sentence the translation owns.
    /// </summary>
    private static string Format(string group, string key, params object[] values) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(group, key), values);

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using Stream stream = typeof(Strings).Assembly.GetManifestResourceStream("en.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)!;
    }
}
