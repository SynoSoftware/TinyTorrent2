using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.Public;

/// <summary>
/// A movie website. The base reads pages and describes a movie from its
/// JSON-LD; each website supplies its addresses and the lists its pages show.
/// </summary>
internal abstract class Provider(string catalogId, Uri origin, IPageReader reader) : IProvider
{
    internal const int PageLimit = 8 * 1024 * 1024;
    public string CatalogId => catalogId;
    public bool IsAvailable => reader.IsAvailable;
    public bool IsLocalized => false;
#if CAPTURE
    // A capture review shows presentation only, so it never asks the website.
    public VideoSchedule Schedule => VideoSchedule.Manual;
#else
    public VideoSchedule Schedule => VideoSchedule.Background;
#endif
    public IReadOnlySet<VideoKind> Kinds { get; } = new HashSet<VideoKind> { VideoKind.Movie };

    // Both Rotten Tomatoes options share one catalogue, because both read the
    // same pages. IMDb has no HTTP option: it answers a request without a
    // browser with a bot challenge instead of the page.
    internal static IReadOnlyList<ProviderOption> Options(ProviderHttp http)
    {
        var browser = new Browser(http);
        var pages = new Http(http);
        Uri privacy = new("https://www.versantprivacy.com/privacy");
        Uri terms = new("https://www.rottentomatoes.com/policies/terms-and-policies");
        return
        [
            new(
                "imdb",
                "IMDb",
                "websites",
                new("https://www.imdb.com/privacy"),
                new("https://www.imdb.com/conditions"),
                _ => new Imdb(browser)
            ),
            new(
                "rottentomatoes",
                "Rotten Tomatoes",
                "websites",
                privacy,
                terms,
                _ => new RottenTomatoes(browser)
            ),
            new(
                "rottentomatoes_http",
                "Rotten Tomatoes",
                "websites_http",
                privacy,
                terms,
                _ => new RottenTomatoes(pages)
            ),
        ];
    }

    internal static VideoException Invalid() => new("websites", "invalid_page");

    // A missing page concerns one movie, such as a saved choice the website
    // has since removed; any other failed reply stops the website's lookups.
    internal static VideoException Failure(int status) =>
        status switch
        {
            403 => new ProviderException("websites", "refused"),
            404 or 410 => Invalid(),
            _ => new ProviderException("websites", "server_error", detail: status.ToString(CultureInfo.InvariantCulture)),
        };

    internal static ProviderException Unreachable(string reason, Exception? inner = null) =>
        new("websites", "unreachable", inner: inner, detail: reason);

    internal static ProviderException BrowserFailed(Exception? inner = null) =>
        new("websites", "browser_failed", inner: inner);

    /// <summary>The search page's path and query for <paramref name="query"/>.</summary>
    protected abstract string SearchPath(string query);

    /// <summary>The canonical form of a movie page's path, or null when <paramref name="path"/> is not one.</summary>
    protected abstract string? Canonical(string path);

    internal abstract IReadOnlyList<VideoChoice> Choices(string html);

    /// <summary>The other movies a movie page lists, which Library saves as choices.</summary>
    protected abstract IEnumerable<VideoChoice?> Related(string html);

    /// <summary>A movie's record built from its search listing, or null when the listing cannot describe it.</summary>
    protected virtual VideoRecord? Listing(VideoChoice choice, Uri address) => null;

    /// <summary>The movie facts to save from a movie page; a website adds what its JSON-LD lacks.</summary>
    protected virtual JsonElement Content(JsonElement movie, string html) => movie.Clone();

    protected virtual Rating? ReadImdbRating(JsonElement movie) => null;

    public async Task<IReadOnlyList<VideoChoice>> Search(
        string query,
        string language,
        CancellationToken cancellation
    )
    {
        var html = await reader
            .Read(new Uri(origin, SearchPath(query)), cancellation)
            .ConfigureAwait(false);
        try
        {
            var choices = Choices(html);
            return choices.Count == 0 && Refused(html)
                ? throw new ProviderException("websites", "refused")
                : choices;
        }
        catch (VideoException error) when (error is not ProviderException && Refused(html))
        {
            throw new ProviderException("websites", "refused");
        }
        catch (RegexMatchTimeoutException)
        {
            throw Invalid();
        }
    }

    public async Task<VideoInformation> Describe(
        VideoChoice choice,
        VideoRequest request,
        CancellationToken cancellation
    )
    {
        if (choice.Kind != VideoKind.Movie || !Address(choice.VideoId, out var address))
            throw Invalid();
        if (
            await request.Saved(address.AbsoluteUri, cancellation).ConfigureAwait(false) is
            { } saved
        )
            return Information(saved);
        if (Listing(choice, address) is { } listed)
            return Information(listed);
        var page = await reader.Read(address, cancellation).ConfigureAwait(false);
        try
        {
            return Describe(page, address);
        }
        catch (VideoException error) when (error is not ProviderException && Refused(page))
        {
            throw new ProviderException("websites", "refused");
        }
        catch (RegexMatchTimeoutException)
        {
            throw Invalid();
        }
    }

    internal VideoInformation Describe(string html, Uri address)
    {
        foreach (var script in Scripts(html, "type", "application/ld+json"))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(script);
            }
            catch (JsonException)
            {
                continue;
            }
            using (document)
            {
                foreach (var movie in Objects(document.RootElement))
                {
                    if (
                        !movie.TryGetProperty("@type", out var type)
                        || !Values(type).Contains("Movie", StringComparer.Ordinal)
                        || !Address(Text(movie, "url"), out var canonical)
                        || canonical != address
                    )
                        continue;
                    var record = new VideoRecord(
                        canonical.AbsoluteUri,
                        Content(movie, html),
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    );
                    return Information(record) with
                    {
                        Related = Unique(
                            Related(html).Where(choice => choice?.VideoId != canonical.AbsoluteUri)
                        ),
                    };
                }
            }
        }
        throw Invalid();
    }

    // A website can answer a blocked request with an ordinary page whose title
    // names the refusal. Callers ask only when the page held no movie data,
    // because a movie title such as "The Forbidden Kingdom" contains the same words.
    private static bool Refused(string html)
    {
        var title = Regex
            .Match(
                html,
                "<title[^>]*>(.*?)</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromSeconds(1)
            )
            .Groups[1]
            .Value;
        return title.Contains("403", StringComparison.OrdinalIgnoreCase)
            || title.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || title.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || title.Contains("captcha", StringComparison.OrdinalIgnoreCase);
    }

    private VideoInformation Information(VideoRecord record)
    {
        var movie = record.Content;
        var title = Text(movie, "name");
        var synopsis = Text(movie, "synopsis");
        if (synopsis.Length == 0)
            synopsis = Text(movie, "description");
        var date = Text(movie, "datePublished");
        if (date.Length == 0)
            date = Text(movie, "dateCreated");
        var year = VideoInformation.ParseYear(date);
        if (
            title.Length == 0
            || title.Length > 300
            || year is < 1888 or > 2200
            || synopsis.Length == 0
        )
            throw Invalid();
        return new VideoInformation(CatalogId, record.Path, title, VideoKind.Movie)
        {
            Year = year,
            Genres = Names(movie, "genre"),
            Cast = Names(movie, "actor"),
            Synopsis = synopsis,
            Keywords = Names(movie, "keywords"),
            Records = [record],
            ImdbRating = ReadImdbRating(movie),
        };
    }

    private bool Address(string value, out Uri address)
    {
        address = origin;
        if (
            !Uri.TryCreate(origin, value, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !candidate.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase)
            || !candidate.IsDefaultPort
            || candidate.UserInfo.Length != 0
            || Canonical(candidate.AbsolutePath) is not { } path
        )
            return false;
        address = new Uri(origin, path);
        return true;
    }

    protected VideoChoice? Choice(
        string href,
        string title,
        int? year,
        string original,
        JsonElement content
    ) =>
        !Address(href, out var address) || title.Length is 0 or > 300
            ? null
            : new VideoChoice(CatalogId, address.AbsoluteUri, VideoKind.Movie, title)
            {
                Year = year,
                Original = original.Length == 0 || original == title ? null : original,
                Content = content.Clone(),
            };

    protected static VideoChoice[] Unique(IEnumerable<VideoChoice?> choices) =>
        choices.OfType<VideoChoice>().DistinctBy(choice => choice.VideoId).ToArray();

    /// <summary>The content of each script whose <paramref name="attribute"/> is <paramref name="value"/>.</summary>
    protected static IEnumerable<string> Scripts(string html, string attribute, string value) =>
        Regex
            .Matches(
                html,
                "<script\\b(?<attributes>[^>]*)>(?<content>.*?)</script\\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromSeconds(1)
            )
            .Where(script =>
                Attribute(script.Groups["attributes"].Value, attribute)
                    .Equals(value, StringComparison.OrdinalIgnoreCase)
            )
            .Select(script => script.Groups["content"].Value);

    protected static string Attribute(string attributes, string name) =>
        WebUtility.HtmlDecode(
            Regex
                .Match(
                    attributes,
                    "(?:^|\\s)" + name + "\\s*=\\s*([\"'])(.*?)\\1",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline,
                    TimeSpan.FromSeconds(1)
                )
                .Groups[2]
                .Value
        );

    protected static string Text(JsonElement value, string property) =>
        Child(value, property) is { ValueKind: JsonValueKind.String } text
            ? WebUtility.HtmlDecode(text.GetString() ?? string.Empty).Trim()
            : string.Empty;

    protected static int? Number(JsonElement value, string property) =>
        Child(value, property) is { ValueKind: JsonValueKind.Number } number
        && number.TryGetInt32(out var result)
            ? result
            : null;

    protected static IReadOnlyList<string> Names(JsonElement value, string property) =>
        Values(Child(value, property)).Where(item => item.Length > 0).Distinct().ToArray();

    // A missing property reads as an undefined element, so nested reads need no checks.
    protected static JsonElement Child(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child)
            ? child
            : default;

    protected static string Plain(string html) =>
        Regex
            .Replace(
                WebUtility.HtmlDecode(
                    Regex.Replace(html, "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(1))
                ),
                "\\s+",
                " ",
                RegexOptions.None,
                TimeSpan.FromSeconds(1)
            )
            .Trim();

    private static IEnumerable<JsonElement> Objects(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            foreach (var child in Objects(item))
                yield return child;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            yield return value;
            if (value.TryGetProperty("@graph", out var graph))
                foreach (var child in Objects(graph))
                    yield return child;
        }
    }

    private static IEnumerable<string> Values(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().SelectMany(Values),
            JsonValueKind.String => [WebUtility.HtmlDecode(value.GetString() ?? string.Empty)],
            JsonValueKind.Object => [Text(value, "name")],
            _ => [],
        };
}

/// <summary>Returns the HTML of a website page, at most <see cref="Provider.PageLimit"/> characters.</summary>
internal interface IPageReader
{
    bool IsAvailable { get; }

    Task<string> Read(Uri address, CancellationToken cancellation);
}
