using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library.Public;

/// <summary>IMDb, whose pages embed their data as JSON in the <c>__NEXT_DATA__</c> script.</summary>
internal sealed class Imdb(IPageReader reader)
    : Provider("public/imdb", new("https://www.imdb.com"), reader)
{
    protected override string SearchPath(string query) =>
        "/find/?s=tt&ttype=ft&q=" + Uri.EscapeDataString(query);

    protected override string? Canonical(string path) =>
        Regex.IsMatch(path, "^/title/tt[0-9]+/?$", RegexOptions.None, TimeSpan.FromSeconds(1))
            ? path.TrimEnd('/') + "/"
            : null;

    // Each result's listing holds its year, plot, genres and rating.
    internal override IReadOnlyList<VideoChoice> Choices(string html)
    {
        if (
            Find(NextData(html), "titleResults") is not { } results
            || Child(results, "results") is not { ValueKind: JsonValueKind.Array } list
        )
            throw Invalid();
        return Unique(
            list.EnumerateArray()
                .Select(result => Child(result, "listItem"))
                .Select(item =>
                    Choice(
                        "/title/" + Text(item, "titleId") + "/",
                        Text(item, "titleText"),
                        Number(item, "releaseYear"),
                        Text(item, "originalTitleText"),
                        item
                    )
                )
        );
    }

    protected override IEnumerable<VideoChoice?> Related(string html) =>
        Find(NextData(html), "moreLikeThisTitles") is { } titles
        && Child(titles, "edges") is { ValueKind: JsonValueKind.Array } edges
            // "More like this" also lists series, which this movie catalogue excludes.
            ? edges
                .EnumerateArray()
                .Select(edge => Child(edge, "node"))
                .Where(node => Text(Child(node, "titleType"), "id") == "movie")
                .Select(node =>
                    Choice(
                        "/title/" + Text(node, "id") + "/",
                        Text(Child(node, "titleText"), "text"),
                        Number(Child(node, "releaseYear"), "year"),
                        Text(Child(node, "originalTitleText"), "text"),
                        node
                    )
                )
            : [];

    /// <summary>
    /// Returns a search result as the movie's JSON-LD record, or null when the
    /// choice has no year or no listing with a plot. The listing has no cast or
    /// keywords; Refresh metadata reads the movie page, which has them.
    /// </summary>
    protected override VideoRecord? Listing(VideoChoice choice, Uri address)
    {
        // The title and year come from the choice, because a saved choice's
        // content also holds its "More like this" facts, which shape those
        // two fields differently.
        if (
            choice.Year is not { } year
            || choice.Content is not { } item
            || Text(item, "plot") is not { Length: > 0 } plot
        )
            return null;
        var movie = new Dictionary<string, object>
        {
            ["@type"] = "Movie",
            ["url"] = address.AbsoluteUri,
            ["name"] = choice.Title,
            ["description"] = plot,
            ["datePublished"] = year.ToString(CultureInfo.InvariantCulture),
            ["genre"] = Names(item, "genres"),
        };
        var rating = Child(item, "ratingSummary");
        if (
            Child(rating, "voteCount") is { ValueKind: JsonValueKind.Number } votes
            && votes.GetInt64() > 0
        )
            movie["aggregateRating"] = new
            {
                ratingValue = Child(rating, "aggregateRating"),
                ratingCount = votes,
            };
        return new VideoRecord(
            address.AbsoluteUri,
            JsonSerializer.SerializeToElement(movie),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        );
    }

    protected override Rating? ReadImdbRating(JsonElement movie)
    {
        if (
            !movie.TryGetProperty("aggregateRating", out var aggregate)
            || aggregate.ValueKind != JsonValueKind.Object
        )
            return null;
        double? Number(string name)
        {
            if (!aggregate.TryGetProperty(name, out var field))
                return null;
            return field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number)
                    ? number
                : field.ValueKind == JsonValueKind.String
                && double.TryParse(
                    field.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number
                )
                    ? number
                : null;
        }
        var best = aggregate.TryGetProperty("bestRating", out _) ? Number("bestRating") : 10;
        var worst = aggregate.TryGetProperty("worstRating", out _) ? Number("worstRating") : 1;
        if (
            Number("ratingValue") is not { } value
            || !double.IsFinite(value)
            || Number("ratingCount") is not { } count
            || !double.IsFinite(count)
            || count <= 0
            || count != Math.Truncate(count)
            || best is not { } maximum
            || !double.IsFinite(maximum)
            || maximum <= 0
            || worst is not { } minimum
            || !double.IsFinite(minimum)
            || minimum < 0
            || minimum >= maximum
            || value < minimum
            || value > maximum
        )
            return null;
        return new Rating(value / maximum * 10, VideoKind.Movie);
    }

    private static JsonElement? NextData(string html)
    {
        foreach (var script in Scripts(html, "id", "__NEXT_DATA__"))
        {
            try
            {
                using var document = JsonDocument.Parse(script);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    // The first property named name at any depth.
    private static JsonElement? Find(JsonElement? value, string name)
    {
        if (value is not { } element)
            return null;
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name))
                    return property.Value;
                if (Find(property.Value, name) is { } found)
                    return found;
            }
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (Find(item, name) is { } found)
                    return found;
        return null;
    }
}
