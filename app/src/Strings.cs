using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace Syno.TinyTorrent;

public sealed class Strings
{
    private Catalogue _current;

    public string Language => _current.Language;
    internal Syno.TableView.Strings Table => _current.Table;
    internal bool IsRightToLeft => _current.IsRightToLeft;

    public Strings()
    {
        _current = Prepare("en");
    }

    internal static Catalogue Prepare(string language)
    {
        var english = Read("en") ?? throw new InvalidDataException("Missing English catalogue.");
        var text = english.ToDictionary(group => group.Key, group => new Dictionary<string, string>(group.Value));
        var culture = CultureInfo.GetCultureInfo(language);
        var parents = new Stack<string>();
        for (var current = culture; current.Name.Length > 0; current = current.Parent) parents.Push(current.Name);
        foreach (var tag in parents)
        {
            if (tag == "en" || Read(tag) is not { } translated) continue;
            foreach (var group in translated)
                foreach (var message in group.Value)
                {
                    if (!english.TryGetValue(group.Key, out var messages) ||
                        !messages.TryGetValue(message.Key, out var fallback) ||
                        !Arguments(message.Value).SetEquals(Arguments(fallback)))
                        throw new InvalidDataException($"Invalid message '{group.Key}.{message.Key}'.");
                    text[group.Key][message.Key] = message.Value;
                }
        }
        return new Catalogue(language, text, Syno.TableView.Strings.Load(language), culture.TextInfo.IsRightToLeft);
    }

    internal void Publish(Catalogue catalogue) => Volatile.Write(ref _current, catalogue);

    private static Dictionary<string, Dictionary<string, string>>? Read(string language)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json");
        if (stream is null) return null;
        using var document = JsonDocument.Parse(stream);
        var text = new Dictionary<string, Dictionary<string, string>>();
        foreach (var group in document.RootElement.EnumerateObject())
        {
            var messages = new Dictionary<string, string>();
            foreach (var message in group.Value.EnumerateObject())
            {
                var value = message.Value.GetString() ?? throw new InvalidDataException("A message is null.");
                CompositeFormat.Parse(value);
                messages.Add(message.Name, value);
            }
            text.Add(group.Name, messages);
        }
        return text;
    }

    private static HashSet<string> Arguments(string text) => Regex.Matches(text, @"\{(\d+)(?:[^}]*)\}")
        .Select(match => match.Groups[1].Value).ToHashSet();

    public string Get(string group, string key) =>
        _current.Text.TryGetValue(group, out var messages) && messages.TryGetValue(key, out var text)
            ? text : group + "." + key;

    public string Format(string group, string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(group, key), values);

    internal string FormatCount(string group, string key, int count, params object[] values) =>
        Format(group, key + (count == 1 ? "_one" : "_other"), [count, .. values]);

    public string Status(string code) =>
        _current.Text.TryGetValue("status", out var messages) && messages.TryGetValue(code, out var text)
            ? text : Get("status", "unknown");

    public string Error(string code, string? detail)
    {
        var message = _current.Text.TryGetValue("errors", out var messages) && messages.TryGetValue(code, out var text)
            ? text : Get("errors", "unknown");
        return string.IsNullOrWhiteSpace(detail) ? message : Format("errors", "detail", message, detail);
    }

    public string Bytes(double value)
    {
        var units = new[] { "bytes", "kib", "mib", "gib", "tib" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return Format("units", units[unit], value);
    }

    internal sealed record Catalogue(string Language, Dictionary<string, Dictionary<string, string>> Text,
        Syno.TableView.Strings Table, bool IsRightToLeft);
}
