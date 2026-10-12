using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Syno.TinyTorrent.Library.Public;

/// <summary>Rotten Tomatoes, whose lists are custom elements in the page's HTML.</summary>
internal sealed class RottenTomatoes(IPageReader reader)
    : Provider("public/rottentomatoes", new("https://www.rottentomatoes.com"), reader)
{
    protected override string SearchPath(string query) =>
        "/search?search=" + Uri.EscapeDataString(query);

    // Movie paths hold letters, digits, underscores and hyphens, such as /m/10009598-surrogates.
    protected override string? Canonical(string path) =>
        Regex.IsMatch(path, "^/m/[a-z0-9_-]+/?$", RegexOptions.None, TimeSpan.FromSeconds(1))
            ? path.TrimEnd('/')
            : null;

    internal override IReadOnlyList<VideoChoice> Choices(string html) =>
        Unique(
            Regex
                .Matches(
                    html,
                    "<search-page-media-row\\b(?<attributes>[^>]*)>(?<content>.*?)</search-page-media-row\\s*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline,
                    TimeSpan.FromSeconds(1)
                )
                .Select(row =>
                {
                    // The row's attributes hold its year, cast and Tomatometer.
                    var facts = Regex
                        .Matches(
                            row.Groups["attributes"].Value,
                            "(?<name>[a-z-]+)\\s*=\\s*\"(?<value>[^\"]*)\"",
                            RegexOptions.IgnoreCase,
                            TimeSpan.FromSeconds(1)
                        )
                        .DistinctBy(attribute => attribute.Groups["name"].Value)
                        .ToDictionary(
                            attribute => attribute.Groups["name"].Value,
                            attribute => WebUtility.HtmlDecode(attribute.Groups["value"].Value)
                        );
                    var link = Regex
                        .Matches(
                            row.Groups["content"].Value,
                            "<a\\b(?<attributes>[^>]*)>(?<content>.*?)</a\\s*>",
                            RegexOptions.IgnoreCase | RegexOptions.Singleline,
                            TimeSpan.FromSeconds(1)
                        )
                        .FirstOrDefault(anchor => Plain(anchor.Groups["content"].Value).Length > 0);
                    return link is null
                        ? null
                        : Choice(
                            Attribute(link.Groups["attributes"].Value, "href"),
                            Plain(link.Groups["content"].Value),
                            int.TryParse(facts.GetValueOrDefault("release-year"), out var year)
                                ? year
                                : null,
                            string.Empty,
                            JsonSerializer.SerializeToElement(facts)
                        );
                })
        );

    protected override IEnumerable<VideoChoice?> Related(string html) =>
        Regex
            .Matches(
                html,
                "<tile-poster-card\\b[^>]*>(?<content>.*?)</tile-poster-card\\s*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromSeconds(1)
            )
            .Select(tile =>
            {
                var link = Regex.Match(
                    tile.Groups["content"].Value,
                    "<rt-link\\b(?<attributes>[^>]*\\bslot=\"title\"[^>]*)>(?<content>.*?)</rt-link\\s*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline,
                    TimeSpan.FromSeconds(1)
                );
                // The tile's labelled texts hold its critics and audience scores.
                var scores = Regex
                    .Matches(
                        tile.Groups["content"].Value,
                        "<rt-text\\b[^>]*\\bslot=\"(?<slot>[a-z-]+)\"[^>]*>(?<content>.*?)</rt-text\\s*>",
                        RegexOptions.IgnoreCase | RegexOptions.Singleline,
                        TimeSpan.FromSeconds(1)
                    )
                    .DistinctBy(text => text.Groups["slot"].Value)
                    .ToDictionary(
                        text => text.Groups["slot"].Value,
                        text => Plain(text.Groups["content"].Value)
                    );
                return link.Success
                    ? Choice(
                        Attribute(link.Groups["attributes"].Value, "href"),
                        Plain(link.Groups["content"].Value),
                        null,
                        string.Empty,
                        JsonSerializer.SerializeToElement(scores)
                    )
                    : null;
            });

    // The JSON-LD has no synopsis, so the page's synopsis element is added to it.
    protected override JsonElement Content(JsonElement movie, string html)
    {
        var synopsis = Plain(
            Regex
                .Match(
                    html,
                    "<rt-text\\b[^>]*data-qa=[\"']synopsis-value[\"'][^>]*>(?<content>.*?)</rt-text\\s*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline,
                    TimeSpan.FromSeconds(1)
                )
                .Groups["content"]
                .Value
        );
        if (synopsis.Length == 0)
            throw Invalid();
        var facts = new Dictionary<string, JsonElement>();
        foreach (var property in movie.EnumerateObject())
            facts[property.Name] = property.Value.Clone();
        facts["synopsis"] = JsonSerializer.SerializeToElement(synopsis);
        return JsonSerializer.SerializeToElement(facts);
    }
}
