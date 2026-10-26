using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Shared.Storage;

namespace Syno.TinyTorrent.Shared;

public sealed class Strings
{
    internal static IReadOnlyList<CultureInfo> Languages { get; } =
        Array.AsReadOnly<CultureInfo>([
            new("en"),
            new("es"),
            new("nl"),
            new("zh"),
            new("pt"),
            new("fr"),
        ]);

    // The flag beside the name shows the region, so the name leaves it out:
    // Portuguese reads Português, not Português (Brasil). Invariant casing keeps
    // .NET from opening an ICU collator for each language, which it would then
    // hold for the process.
    internal static string NativeName(CultureInfo language)
    {
        var name = (language.IsNeutralCulture ? language : language.Parent).NativeName;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    // A language as the language lists name it: in the window's language, with
    // its own name after it, so a person can find their language from any
    // other. The window's language needs only its own name.
    internal string LanguageChoice(CultureInfo language) =>
        language.Name == Language
            ? NativeName(language)
            : Format("settings", "language_choice", Get("languages", language.Name), NativeName(language));

    // The region whose flag stands for the language. English text uses British
    // spelling, so it shows the UK flag rather than its default region's.
    internal static string FlagRegion(CultureInfo language) =>
        language.Name == "en"
            ? "GB"
            : new RegionInfo(CultureInfo.CreateSpecificCulture(language.Name).Name).TwoLetterISORegionName;

    private static readonly string[] _units = ["bytes", "kib", "mib", "gib", "tib"];
    private Catalogue _current;
    private readonly HashSet<string> _languageTags;

    public string Language => _current.Language;
    internal Syno.TableView.Strings Table => _current.Table;
    internal bool IsRightToLeft => _current.IsRightToLeft;

    public Strings()
        : this([]) { }

    internal Strings(IEnumerable<string> languageTags)
    {
        _languageTags = languageTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _current = Prepare("en");
    }

    internal Catalogue Prepare(string language)
    {
        var english = Read("en") ?? throw new InvalidDataException("Missing English catalogue.");
        var text = english.ToDictionary(
            group => group.Key,
            group => new Dictionary<string, CompositeFormat>(group.Value)
        );
        var culture = CultureInfo.GetCultureInfo(language);
        var parents = new Stack<string>();
        for (var current = culture; current.Name.Length > 0; current = current.Parent)
            parents.Push(current.Name);
        foreach (var tag in parents)
        {
            if (tag == "en" || Read(tag) is not { } translated)
                continue;
            foreach (var group in translated)
            foreach (var message in group.Value)
            {
                if (
                    english.TryGetValue(group.Key, out var messages)
                    && messages.TryGetValue(message.Key, out var fallback)
                    && Arguments(message.Value.Format).SetEquals(Arguments(fallback.Format))
                )
                    text[group.Key][message.Key] = message.Value;
            }
        }
        return new Catalogue(
            language,
            text,
            Syno.TableView.Strings.Load(language),
            culture.TextInfo.IsRightToLeft,
            _languageTags
        );
    }

    internal void Publish(Catalogue catalogue) => Volatile.Write(ref _current, catalogue);

    internal string LanguageName(string tag) => _current.LanguageName(tag);

    private static Dictionary<string, Dictionary<string, CompositeFormat>>? Read(string language)
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream(language + ".json");
        if (stream is null)
            return null;
        using var document = JsonDocument.Parse(stream);
        var text = new Dictionary<string, Dictionary<string, CompositeFormat>>();
        foreach (var group in document.RootElement.EnumerateObject())
        {
            var messages = new Dictionary<string, CompositeFormat>();
            foreach (var message in group.Value.EnumerateObject())
            {
                var value =
                    message.Value.GetString()
                    ?? throw new InvalidDataException("A message is null.");
                messages.Add(message.Name, CompositeFormat.Parse(value));
            }
            text.Add(group.Name, messages);
        }
        return text;
    }

    private static HashSet<string> Arguments(string text) =>
        Regex
            .Matches(text, @"\{\{|\{(\d+)(?:[^}]*)\}")
            .Where(match => match.Groups[1].Success)
            .Select(match => match.Groups[1].Value)
            .ToHashSet();

    private CompositeFormat? FindFormat(string group, string key) =>
        _current.Text.TryGetValue(group, out var messages)
        && messages.TryGetValue(key, out var text)
            ? text
            : null;

    public string? Find(string group, string key) => FindFormat(group, key)?.Format;

    public string Get(string group, string key) => Find(group, key) ?? group + "." + key;

    internal string Get(WorkDescription description, bool library = false) =>
        Get(
            "work",
            description switch
            {
                WorkDescription.Imdb imdb => "imdb_" + imdb.Phase.ToString().ToLowerInvariant(),
                WorkDescription.City city => "city_" + city.Phase.ToString().ToLowerInvariant(),
                WorkDescription.Metadata metadata => metadata.Scope switch
                {
                    MetadataScope.Batch => library ? "metadata" : "library_metadata",
                    MetadataScope.Automatic => "automatic_metadata",
                    _ => "inspector_metadata",
                },
                WorkDescription.Subtitles subtitles => "subtitles_"
                    + subtitles.Work.ToString().ToLowerInvariant(),
                WorkDescription.FileProperties => "file_properties",
                WorkDescription.Update update => "update_"
                    + update.Phase.ToString().ToLowerInvariant(),
                WorkDescription.Sources => "sources",
                WorkDescription.Maintenance maintenance => "maintenance_"
                    + maintenance.Work.ToString().ToLowerInvariant(),
                _ => throw new InvalidOperationException(),
            }
        );

    public string Format(string group, string key, params object[] values) =>
        FindFormat(group, key) is { } format
            ? string.Format(CultureInfo.CurrentCulture, format, values)
            : Get(group, key);

    internal string FormatCount(string group, string key, long count, params object[] values) =>
        Format(group, key + (count == 1 ? "_one" : "_other"), [count, .. values]);

    public string Status(string code) => Find("status", code) ?? Get("status", "unknown");

    public string Error(string code, string? detail)
    {
        if (code == "torrent_error" && !string.IsNullOrWhiteSpace(detail))
            return detail;
        var message = Find("errors", code) ?? Get("errors", "unknown");
        return string.IsNullOrWhiteSpace(detail)
            ? message
            : Format("errors", "detail", message, detail);
    }

    public string Error(Exception error) =>
        error switch
        {
            CommandException => error.Message,
            DatabaseVersionException => Get("errors", "database_incompatible"),
            RequestBlockedException blocked => Protection(blocked.Block),
            Library.ProviderException { Block: { } block } => Get(
                "request_protection",
                block.Kind.ToString().ToLowerInvariant()
            ),
            Library.VideoException video => Failure(video),
            Shell.UpdateException update => Get(
                "about",
                "update_" + update.Reason.ToString().ToLowerInvariant()
            ),
            Subtitles.SubtitleException subtitle => Get(
                "subtitles",
                "failure_" + subtitle.Reason.ToString().ToLowerInvariant()
            ),
            // A title or the surrounding control already says that the
            // operation failed, so only the reason follows it.
            _ => string.IsNullOrWhiteSpace(error.Message)
                ? Get("errors", "unknown")
                : error.Message,
        };

    // A provider without an API key refused the request, not a key.
    private string Failure(Library.VideoException error)
    {
        var reason = error is { Failure: VideoFailure.Refused, Provider.Access: not Access.Key }
            ? "refused_request"
            : error.Failure.ToString().ToLowerInvariant();
        return Format(
            "library",
            "failure_" + reason,
            error.Provider?.Name ?? error.Subject ?? string.Empty
        );
    }

    internal string Protection(RequestBlock block)
    {
        var reason = Get("request_protection", block.Kind.ToString().ToLowerInvariant());
        return block.RetryAt is { } retry
            ? Format("request_protection", "retry_at", reason, Time(retry))
            : reason;
    }

    public string RateUnit(RateScale scale) =>
        Get("units", scale == RateScale.KibPerSecond ? "kib_per_second" : "mb_per_second");

    public string SpeedLimit(double bytes)
    {
        var unit = RateUnit(RateScale.MbPerSecond);
        return bytes switch
        {
            0 => Get("transfer_limits", "unlimited"),
            < 10000 => Format("units", "speed_under", 0.01, unit),
            _ => Format("units", "speed", bytes / (double)RateScale.MbPerSecond, unit),
        };
    }

    /// <summary>Formats a move's copy rate in KiB/s, or MiB/s from 1 MiB/s, never GiB/s.</summary>
    public string CopyRate(double bytes) =>
        Format(
            "units",
            "rate",
            bytes < 1024 * 1024
                ? Format("units", "kib", bytes / 1024)
                : Format("units", "mib", bytes / (1024 * 1024))
        );

    public string Rate(double value) => value == 0 ? "—" : Format("units", "rate", Bytes(value));

    public string Bytes(double value)
    {
        var unit = 0;
        while (value >= 1024 && unit < _units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return Format("units", _units[unit], value);
    }

    /// <summary>Shortens a large number as IMDb does: 950, 2.7K, 12K, 2.7M.</summary>
    public string Compact(long value)
    {
        // One decimal below ten, as 2.7, and none above, as 12.
        static double Shorten(double value) => Math.Round(value, value < 10 ? 1 : 0);
        return value switch
        {
            >= 1_000_000 => Format("units", "millions", Shorten(value / 1e6)),
            >= 1_000 => Format("units", "thousands", Shorten(value / 1e3)),
            _ => value.ToString("N0"),
        };
    }

    /// <summary>Formats a remaining time in its two largest units; 100 days or more reads as never.</summary>
    public string Duration(double seconds)
    {
        var minutes = (long)Math.Ceiling(seconds / 60);
        if (minutes >= 100 * 24 * 60)
            return "∞";
        if (minutes < 60)
            return Format("units", "minutes", minutes);
        if (minutes < 24 * 60)
            return minutes % 60 == 0
                ? Format("units", "hours", minutes / 60)
                : Format("units", "hours_minutes", minutes / 60, minutes % 60);
        var hours = minutes % (24 * 60) / 60;
        return hours == 0
            ? Format("units", "days", minutes / (24 * 60))
            : Format("units", "days_hours", minutes / (24 * 60), hours);
    }

    public string Ago(DateTimeOffset moment)
    {
        var elapsed = DateTimeOffset.Now - moment;
        if (elapsed.TotalMinutes < 1)
            return Get("units", "now");
        if (elapsed.TotalHours < 1)
            return FormatCount("units", "minutes_ago", (int)elapsed.TotalMinutes);
        if (elapsed.TotalDays < 1)
            return FormatCount("units", "hours_ago", (int)elapsed.TotalHours);
        if (elapsed.TotalDays < 30)
            return FormatCount("units", "days_ago", (int)elapsed.TotalDays);
        if (elapsed.TotalDays < 365)
            return FormatCount("units", "months_ago", (int)(elapsed.TotalDays / 30));
        return FormatCount("units", "years_ago", (int)(elapsed.TotalDays / 365));
    }

    public string Time(DateTimeOffset moment) =>
        moment.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    internal sealed record Catalogue(
        string Language,
        Dictionary<string, Dictionary<string, CompositeFormat>> Text,
        Syno.TableView.Strings Table,
        bool IsRightToLeft,
        IReadOnlySet<string> LanguageTags
    )
    {
        private readonly ConcurrentDictionary<string, string> _names = new(
            StringComparer.OrdinalIgnoreCase
        );

        internal string LanguageName(string tag)
        {
            try
            {
                return _names.GetOrAdd(tag, ResolveName);
            }
            catch (CultureNotFoundException)
            {
                return tag;
            }
        }

        private string ResolveName(string tag)
        {
            var previous = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Language);
                var supplied =
                    LanguageTags.Contains(tag)
                    || string.Equals(tag, Language, StringComparison.OrdinalIgnoreCase);
                if (tag.Length == 0 && !supplied)
                    return tag;
                return CultureInfo.GetCultureInfo(tag, predefinedOnly: !supplied).DisplayName;
            }
            finally
            {
                CultureInfo.CurrentUICulture = previous;
            }
        }
    }
}
