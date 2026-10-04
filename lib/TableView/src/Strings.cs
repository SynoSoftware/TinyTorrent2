using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Syno.TableView;

/// <summary>
/// Prepared control text from this library's embedded catalogues, with parent-language and
/// English fallback. The host publishes it without owning the library's messages.
/// </summary>
public sealed class Strings
{
    private static readonly Lazy<Strings> Fallback = new(() => new Strings(Read("en")
        ?? throw new InvalidDataException("The embedded English catalogue is missing.")));
    internal static Strings English => Fallback.Value;
    private readonly Dictionary<string, Dictionary<string, string>> _text;

    private Strings(Dictionary<string, Dictionary<string, string>> text) => _text = text;

    /// <summary>
    /// Prepare embedded text off the UI thread. Missing messages use parent languages, then
    /// English; invalid catalogues fail without changing any control's current text.
    /// </summary>
    public static Task<Strings> LoadAsync(string language, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        CultureInfo culture = CultureInfo.GetCultureInfo(language);
        return Task.Run(() => Load(culture, cancellation), cancellation);
    }

    internal string HeaderStripAccessibleName => Get("header", "accessible_name");

    // Section 17: what the control shows in place of rows when the host configured no content of
    // its own. The loading one is not drawn — it names the ring for UI Automation.
    internal string Loading => Get("placeholder", "loading");

    internal string Empty => Get("placeholder", "empty");

    internal string NoResults => Get("placeholder", "no_results");

    // Section 9: the active header's sort state, read by UI Automation.
    internal string SortedAscending => Get("header", "sorted_ascending");

    internal string SortedDescending => Get("header", "sorted_descending");

    // Section 12: the generated menu's action labels. They are the control's own strings and are
    // not host-overridable; only the column names in them come from the host.
    //
    // The three that act on one column name it. "Hide this column" was ambiguous the moment the
    // column list moved into the same menu: "this" meant the column the menu was opened on, while
    // the names directly below it meant themselves, and nothing on screen said which was which.
    internal string HideColumn(string name) => Format("column_menu", "hide", name);

    internal string ShowColumn(string name) => Format("column_menu", "show", name);

    internal string FitColumn(string name) => Format("column_menu", "fit", name);

    internal string FitVisibleColumns => Get("column_menu", "fit_visible");

    internal string MoveLeft => Get("column_menu", "move_left");

    internal string MoveRight => Get("column_menu", "move_right");

    // Section 16: a live row drag's destination, read by UI Automation. In the before-row text,
    // {0} is the destination row's position and {1} the number of rows.
    internal string DropBeforeRow => Get("row_drag", "before_row");

    internal string DropAtEnd => Get("row_drag", "at_end");

    // Section 19: the column list draws its state as an icon rather than as a toggle's own check,
    // so the state has to be said rather than left to the toggle pattern to report.
    internal string ColumnShown => Get("column_menu", "shown");

    internal string ColumnHidden => Get("column_menu", "hidden");

    private string Get(string group, string key) => _text[group][key];

    /// <summary>
    /// A label that carries a column's name. The current culture, not the invariant one: this is
    /// text a person reads, and the name is placed into a sentence the translation owns.
    /// </summary>
    private string Format(string group, string key, params object[] values) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(group, key), values);

    private static Strings Load(CultureInfo culture, CancellationToken cancellation)
    {
        Dictionary<string, Dictionary<string, string>> text = English._text.ToDictionary(
            group => group.Key, group => new Dictionary<string, string>(group.Value));
        Stack<string> languages = new();
        for (CultureInfo current = culture; current.Name.Length > 0; current = current.Parent)
            languages.Push(current.Name);
        foreach (string language in languages)
        {
            cancellation.ThrowIfCancellationRequested();
            if (language.Equals("en", StringComparison.OrdinalIgnoreCase)) continue;
            if (Read(language) is not { } translated) continue;
            foreach (var group in translated)
            {
                if (!text.TryGetValue(group.Key, out var messages))
                    throw new InvalidDataException($"Unknown text group '{group.Key}'.");
                foreach (var message in group.Value)
                {
                    if (!English._text[group.Key].TryGetValue(message.Key, out string? fallback)
                        || !Arguments(message.Value).SetEquals(Arguments(fallback)))
                        throw new InvalidDataException($"Invalid message '{group.Key}.{message.Key}'.");
                    messages[message.Key] = message.Value;
                }
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return new Strings(text);
    }

    private static HashSet<int> Arguments(string value)
    {
        HashSet<int> indices = new();
        for (int offset = 0; offset < value.Length; offset++)
        {
            if (value[offset] != '{') continue;
            if (offset + 1 < value.Length && value[offset + 1] == '{')
            {
                offset++;
                continue;
            }
            int start = ++offset;
            while (offset < value.Length && char.IsAsciiDigit(value[offset])) offset++;
            indices.Add(int.Parse(value.AsSpan(start, offset - start), CultureInfo.InvariantCulture));
        }
        return indices;
    }

    private static Dictionary<string, Dictionary<string, string>>? Read(string language)
    {
        var assembly = typeof(Strings).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(
            name => name.Equals(language + ".json", StringComparison.OrdinalIgnoreCase));
        if (resource is null) return null;
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using JsonDocument document = JsonDocument.Parse(stream);
        Dictionary<string, Dictionary<string, string>> text = new(StringComparer.Ordinal);
        foreach (JsonProperty group in document.RootElement.EnumerateObject())
        {
            Dictionary<string, string> messages = new(StringComparer.Ordinal);
            foreach (JsonProperty message in group.Value.EnumerateObject())
            {
                string value = message.Value.GetString()
                    ?? throw new InvalidDataException($"Null message '{group.Name}.{message.Name}'.");
                _ = CompositeFormat.Parse(value);
                if (!messages.TryAdd(message.Name, value))
                    throw new InvalidDataException($"Duplicate message '{group.Name}.{message.Name}'.");
            }
            if (!text.TryAdd(group.Name, messages))
                throw new InvalidDataException($"Duplicate text group '{group.Name}'.");
        }
        return text;
    }
}
