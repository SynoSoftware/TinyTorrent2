using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Services;

public sealed class Strings
{
    private Catalogue _current;
    private readonly string[] _languageTags;

    public string Language => _current.Language;
    internal Syno.TableView.Strings Table => _current.Table;
    internal bool IsRightToLeft => _current.IsRightToLeft;

    public Strings()
        : this([]) { }

    internal Strings(IEnumerable<string> languageTags)
    {
        _languageTags = languageTags
            .Concat(
                CultureInfo
                    .GetCultures(CultureTypes.AllCultures)
                    .Where(culture => culture.Name.Length > 0)
                    .Select(culture => culture.Name)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _current = Prepare("en");
    }

    internal Catalogue Prepare(string language)
    {
        var english = Read("en") ?? throw new InvalidDataException("Missing English catalogue.");
        var text = english.ToDictionary(
            group => group.Key,
            group => new Dictionary<string, string>(group.Value)
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
                    !english.TryGetValue(group.Key, out var messages)
                    || !messages.TryGetValue(message.Key, out var fallback)
                    || !Arguments(message.Value).SetEquals(Arguments(fallback))
                )
                    throw new InvalidDataException($"Invalid message '{group.Key}.{message.Key}'.");
                text[group.Key][message.Key] = message.Value;
            }
        }
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = culture;
            foreach (
                var tag in _languageTags.Append(language).Distinct(StringComparer.OrdinalIgnoreCase)
            )
            {
                try
                {
                    names.Add(tag, CultureInfo.GetCultureInfo(tag).DisplayName);
                }
                catch (CultureNotFoundException)
                {
                    names.Add(tag, tag);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
        return new Catalogue(
            language,
            text,
            Syno.TableView.Strings.Load(language),
            culture.TextInfo.IsRightToLeft,
            names
        );
    }

    internal void Publish(Catalogue catalogue) => Volatile.Write(ref _current, catalogue);

    internal string LanguageName(string tag) => _current.LanguageNames.GetValueOrDefault(tag, tag);

    private static Dictionary<string, Dictionary<string, string>>? Read(string language)
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream(language + ".json");
        if (stream is null)
            return null;
        using var document = JsonDocument.Parse(stream);
        var text = new Dictionary<string, Dictionary<string, string>>();
        foreach (var group in document.RootElement.EnumerateObject())
        {
            var messages = new Dictionary<string, string>();
            foreach (var message in group.Value.EnumerateObject())
            {
                var value =
                    message.Value.GetString()
                    ?? throw new InvalidDataException("A message is null.");
                CompositeFormat.Parse(value);
                messages.Add(message.Name, value);
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

    public string? Find(string group, string key) =>
        _current.Text.TryGetValue(group, out var messages)
        && messages.TryGetValue(key, out var text)
            ? text
            : null;

    public string Get(string group, string key) => Find(group, key) ?? group + "." + key;

    public string Format(string group, string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(group, key), values);

    internal string FormatCount(string group, string key, long count, params object[] values) =>
        Format(group, key + (count == 1 ? "_one" : "_other"), [count, .. values]);

    public string Status(string code) =>
        _current.Text.TryGetValue("status", out var messages)
        && messages.TryGetValue(code, out var text)
            ? text
            : Get("status", "unknown");

    public string Error(string code, string? detail)
    {
        var message =
            _current.Text.TryGetValue("errors", out var messages)
            && messages.TryGetValue(code, out var text)
                ? text
                : Get("errors", "unknown");
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
            Library.ProviderException { Block: { } block } =>
                Get("request_protection", block.Kind.ToString().ToLowerInvariant()),
            Library.VideoException video => Failure(video),
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
        return Format("library", "failure_" + reason, error.Provider?.Name ?? string.Empty);
    }

    internal string Protection(RequestBlock block)
    {
        var reason = Get("request_protection", block.Kind.ToString().ToLowerInvariant());
        return block.RetryAt is { } retry
            ? Format("request_protection", "retry_at", reason, Time(retry)) : reason;
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
        Dictionary<string, Dictionary<string, string>> Text,
        Syno.TableView.Strings Table,
        bool IsRightToLeft,
        IReadOnlyDictionary<string, string> LanguageNames
    );
}
