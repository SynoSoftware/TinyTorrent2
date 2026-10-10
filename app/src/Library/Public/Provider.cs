using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.Public;

internal sealed class Provider(ProviderHttp http, Settings settings) : IConfigurable
{
    internal static IReadOnlyList<Website> Websites { get; } =
    [
        new("imdb", "IMDb", new("https://www.imdb.com")),
        new("rottentomatoes", "Rotten Tomatoes", new("https://www.rottentomatoes.com")),
    ];
    private readonly Browser _browser = new(http);
    private Website Website => settings.Website;
    private Uri Origin => Website.Origin;
    public string CatalogId => "public/" + Website.WebsiteId;
    public bool IsAvailable => Browser.IsAvailable;
    public bool IsLocalized => false;
    public VideoSchedule Schedule => settings.Seconds == 0
        ? new VideoSchedule.Manual() : new VideoSchedule.Selection(TimeSpan.FromSeconds(settings.Seconds));
    public IReadOnlySet<VideoKind> Kinds { get; } = new HashSet<VideoKind> { VideoKind.Movie };

    internal static IProvider Create(ProviderHttp http, IReadOnlyDictionary<string, string> values) =>
        new Provider(http, Settings.Read(values));

    public VideoEditor Edit(Strings text, Func<IReadOnlyDictionary<string, string>, Task> save)
    {
        var editor = new WebsiteDialog(text, settings, replacement => save(replacement.Values));
        return new VideoEditor(editor, editor, editor.RefreshText);
    }

    internal static VideoException Invalid() => new("websites", "invalid_page");

    public async Task<IReadOnlyList<VideoChoice>> Search(string query, string language, CancellationToken cancellation)
    {
        var address = new Uri(Origin, Website.WebsiteId == "imdb"
            ? "/find/?s=tt&ttype=ft&q=" + Uri.EscapeDataString(query)
            : "/search?search=" + Uri.EscapeDataString(query));
        try { return Choices(await _browser.Read(address, cancellation).ConfigureAwait(false)); }
        catch (RegexMatchTimeoutException) { throw Invalid(); }
    }

    public async Task<VideoInformation> Describe(VideoChoice choice, VideoRequest request, CancellationToken cancellation)
    {
        if (choice.Kind != VideoKind.Movie || !Address(choice.VideoId, out var address))
            throw Invalid();
        try { return Describe(await _browser.Read(address, cancellation).ConfigureAwait(false), address); }
        catch (RegexMatchTimeoutException) { throw Invalid(); }
    }

    internal IReadOnlyList<VideoChoice> Choices(string html)
    {
        Refusal(html);
        var choices = new Dictionary<string, VideoChoice>(StringComparer.Ordinal);
        IEnumerable<(string Html, int? Year)> rows = Website.WebsiteId == "rottentomatoes"
            ? Regex.Matches(html, "<search-page-media-row\\b(?<attributes>[^>]*)>(?<content>.*?)</search-page-media-row\\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1))
                .Select(row => (Html: row.Groups["content"].Value,
                    Year: int.TryParse(Attribute(row.Groups["attributes"].Value, "release-year"), out var year) ? (int?)year : null))
            : [(Html: html, Year: (int?)null)];
        foreach (var row in rows)
        foreach (Match match in Regex.Matches(row.Html, "<a\\b(?<attributes>[^>]*)>(?<content>.*?)</a\\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1)))
        {
            var href = Attribute(match.Groups["attributes"].Value, "href");
            if (!Address(href, out var address))
                continue;
            var title = WebUtility.HtmlDecode(Regex.Replace(match.Groups["content"].Value,
                "<[^>]+>", " ", RegexOptions.None, TimeSpan.FromSeconds(1))).Trim();
            title = Regex.Replace(title, "\\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (title.Length == 0 || title.Length > 300)
                continue;
            choices.TryAdd(address.AbsoluteUri, new VideoChoice(CatalogId, address.AbsoluteUri, VideoKind.Movie, title) { Year = row.Year });
        }
        return choices.Values.ToArray();
    }

    internal VideoInformation Describe(string html, Uri address)
    {
        Refusal(html);
        foreach (Match script in Regex.Matches(html, "<script\\b(?<attributes>[^>]*)>(?<content>.*?)</script\\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1)))
        {
            if (!Attribute(script.Groups["attributes"].Value, "type").Equals("application/ld+json", StringComparison.OrdinalIgnoreCase))
                continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(script.Groups["content"].Value); }
            catch (JsonException) { continue; }
            using (document)
            {
                foreach (var movie in Objects(document.RootElement))
                {
                    if (!movie.TryGetProperty("@type", out var type) || !Values(type).Contains("Movie", StringComparer.Ordinal))
                        continue;
                    if (!Address(Text(movie, "url"), out var canonical) || canonical != address)
                        continue;
                    var content = movie.Clone();
                    if (Website.WebsiteId == "rottentomatoes")
                    {
                        var synopsis = Regex.Match(html,
                            "<rt-text\\b[^>]*data-qa=[\"']synopsis-value[\"'][^>]*>(?<content>.*?)</rt-text\\s*>",
                            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Groups["content"].Value;
                        synopsis = WebUtility.HtmlDecode(Regex.Replace(synopsis, "<[^>]+>", " ",
                            RegexOptions.None, TimeSpan.FromSeconds(1))).Trim();
                        if (synopsis.Length == 0)
                            throw Invalid();
                        var facts = new Dictionary<string, JsonElement>();
                        foreach (var property in movie.EnumerateObject())
                            facts[property.Name] = property.Value.Clone();
                        facts["synopsis"] = JsonSerializer.SerializeToElement(synopsis);
                        content = JsonSerializer.SerializeToElement(facts);
                    }
                    return Information(new VideoRecord(canonical.AbsoluteUri, content, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                }
            }
        }
        throw Invalid();
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
        if (title.Length == 0 || title.Length > 300 || year is < 1888 or > 2200 || synopsis.Length == 0)
            throw Invalid();
        return new VideoInformation(CatalogId, record.Path, title, VideoKind.Movie)
        {
            Year = year, Genres = Names(movie, "genre"), Cast = Names(movie, "actor"),
            Synopsis = synopsis, Keywords = Names(movie, "keywords"), Records = [record],
        };
    }

    private bool Address(string value, out Uri address)
    {
        address = Origin;
        if (!Uri.TryCreate(Origin, value, out var candidate) || candidate.Scheme != Uri.UriSchemeHttps ||
            !candidate.Host.Equals(Origin.Host, StringComparison.OrdinalIgnoreCase) || !candidate.IsDefaultPort ||
            candidate.UserInfo.Length != 0)
            return false;
        var pattern = Website.WebsiteId == "imdb" ? "^/title/tt[0-9]+/?$" : "^/m/[a-z0-9_]+/?$";
        if (!Regex.IsMatch(candidate.AbsolutePath, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
            return false;
        address = new Uri(Origin, candidate.AbsolutePath.TrimEnd('/') + (Website.WebsiteId == "imdb" ? "/" : string.Empty));
        return true;
    }

    private static void Refusal(string html)
    {
        var title = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(1)).Groups[1].Value;
        if (title.Contains("403", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("captcha", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("websites", "refused");
    }

    private static string Attribute(string attributes, string name) => WebUtility.HtmlDecode(Regex.Match(attributes,
        "(?:^|\\s)" + name + "\\s*=\\s*([\"'])(.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1)).Groups[2].Value);

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

    private static IEnumerable<string> Values(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Values),
        JsonValueKind.String => [WebUtility.HtmlDecode(value.GetString() ?? string.Empty)],
        JsonValueKind.Object => [Text(value, "name")],
        _ => [],
    };

    private static string Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String
            ? WebUtility.HtmlDecode(text.GetString() ?? string.Empty).Trim() : string.Empty;

    private static IReadOnlyList<string> Names(JsonElement value, string property) => value.TryGetProperty(property, out var items)
        ? Values(items).Where(item => item.Length > 0).Distinct().ToArray() : [];
}

internal sealed record Website(string WebsiteId, string Name, Uri Origin);
