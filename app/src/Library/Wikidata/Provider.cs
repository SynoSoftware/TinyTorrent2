using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.Wikidata;

/// <summary>
/// Wikidata, read through its SPARQL query service. Names follow the requested
/// language and fall back to English; the synopsis is the movie's one-line
/// Wikidata description, because Wikidata holds no plot.
/// </summary>
internal sealed class Provider(ProviderHttp http) : IProvider
{
    // Wikidata's item for IMDb, which review score statements name as their source.
    private const string Imdb = "Q37312";
    public string CatalogId => "wikidata";
    public bool IsAvailable => true;
    public bool IsLocalized => true;
    public VideoSchedule Schedule => VideoSchedule.Background;
    public IReadOnlySet<VideoKind> Kinds { get; } = new HashSet<VideoKind> { VideoKind.Movie };

    public async Task<IReadOnlyList<VideoChoice>> Search(
        string query,
        string language,
        CancellationToken cancellation
    )
    {
        // The search service reads quotes, colons and capitalised AND, OR and
        // NOT as syntax, so only lowercase words reach it.
        var words = new StringBuilder();
        foreach (var letter in query.ToLowerInvariant())
            words.Append(char.IsLetterOrDigit(letter) ? letter : ' ');
        // Without words the search would list arbitrary movies.
        if (string.IsNullOrWhiteSpace(words.ToString()))
            return [];
        // The search runs inside a subquery, where it pages through every hit
        // unless wikibase:limit stops it.
        var data = await Get(
                $$"""
                SELECT ?item ?title ?original ?year WHERE {
                  {
                    SELECT ?item (MIN(YEAR(?date)) AS ?year) (SAMPLE(?name) AS ?original) WHERE {
                      SERVICE wikibase:mwapi {
                        bd:serviceParam wikibase:endpoint "www.wikidata.org"; wikibase:api "Search";
                          wikibase:limit 50; mwapi:srsearch "{{words}} haswbstatement:P345".
                        ?item wikibase:apiOutputItem mwapi:title.
                      }
                      ?item wdt:P31/wdt:P279* wd:Q11424 .
                      OPTIONAL { ?item wdt:P577 ?date }
                      OPTIONAL { ?item wdt:P1476 ?name }
                    } GROUP BY ?item
                  }
                  SERVICE wikibase:label {
                    bd:serviceParam wikibase:language "{{language}},mul,en". ?item rdfs:label ?title .
                  }
                }
                """,
                cancellation
            )
            .ConfigureAwait(false);
        try
        {
            return Rows(data)
                .Select(row =>
                    (
                        Item: Item(Value(row, "item")),
                        Title: Value(row, "title"),
                        Original: Value(row, "original"),
                        Year: Value(row, "year")
                    )
                )
                .Where(row => row.Item.Length > 0 && row.Title.Length is > 0 and <= 300)
                .Select(row => new VideoChoice(CatalogId, row.Item, VideoKind.Movie, row.Title)
                {
                    Year = int.TryParse(
                        row.Year,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var year
                    )
                        ? year
                        : null,
                    Original = row.Original.Length == 0 ? null : row.Original,
                })
                .ToArray();
        }
        catch (Exception error)
            when (error is InvalidOperationException or KeyNotFoundException)
        {
            throw new VideoException("wikidata", "invalid_result", error);
        }
    }

    public async Task<VideoInformation> Describe(
        VideoChoice choice,
        VideoRequest request,
        CancellationToken cancellation
    )
    {
        // The item identity is written into the query, so it must be one.
        if (choice.Kind != VideoKind.Movie || Item(choice.VideoId) != choice.VideoId)
            throw new VideoException("wikidata", "invalid_result");
        var item = choice.VideoId;
        var record = await request.Saved(item, cancellation).ConfigureAwait(false);
        if (record is null)
        {
            // Cast members are ordered by how many Wikimedia sites link them, a
            // measure of prominence, because the query service does not keep
            // Wikidata's billing order.
            var data = await Get(
                    $$"""
                    SELECT ?field ?value ?valueLabel ?valueDescription ?by ?date ?links WHERE {
                      VALUES ?item { wd:{{item}} }
                      { VALUES ?value { wd:{{item}} } BIND("title" AS ?field) }
                      UNION { ?item wdt:P1476 ?value . BIND("original" AS ?field) }
                      UNION { ?item wdt:P577 ?value . BIND("date" AS ?field) }
                      UNION { ?item wdt:P345 ?value . BIND("imdb" AS ?field) }
                      UNION { ?item wdt:P136 ?value . BIND("genre" AS ?field) }
                      UNION { ?item wdt:P161 ?value . ?value wikibase:sitelinks ?links . BIND("cast" AS ?field) }
                      UNION {
                        ?item p:P444 ?statement . ?statement ps:P444 ?value ; pq:P447 ?by .
                        OPTIONAL { ?statement pq:P585 ?date }
                        BIND("score" AS ?field)
                      }
                      SERVICE wikibase:label {
                        bd:serviceParam wikibase:language "{{request.Language}},mul,en".
                      }
                    }
                    """,
                    cancellation
                )
                .ConfigureAwait(false);
            record = new VideoRecord(item, data, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        try
        {
            return Information(item, record);
        }
        catch (Exception error)
            when (error is InvalidOperationException or KeyNotFoundException)
        {
            throw new VideoException("wikidata", "invalid_result", error);
        }
    }

    private VideoInformation Information(string item, VideoRecord record)
    {
        var rows = Rows(record.Content);
        IEnumerable<JsonElement> Field(string field) =>
            rows.Where(row => Value(row, "field") == field);
        IReadOnlyList<string> Names(IEnumerable<JsonElement> values) =>
            values
                .Select(row => Value(row, "valueLabel"))
                .Where(name => name.Length > 0)
                .Distinct()
                .ToArray();
        var movie = Field("title").FirstOrDefault();
        // The label service leaves the label empty when the item has none in
        // the requested language, in any language or in English.
        var title = Value(movie, "valueLabel");
        if (title.Length is 0 or > 300)
            throw new VideoException("wikidata", "invalid_result");
        return new VideoInformation(CatalogId, item, title, VideoKind.Movie)
        {
            Year = Field("date")
                .Select(row => VideoInformation.ParseYear(Value(row, "value")))
                .Min(),
            Genres = Names(Field("genre")),
            Cast = Names(
                Field("cast")
                    .OrderByDescending(row =>
                        int.TryParse(Value(row, "links"), CultureInfo.InvariantCulture, out var links)
                            ? links
                            : 0
                    )
            ),
            Synopsis = Value(movie, "valueDescription"),
            Records = [record],
            // Editors record a score again when it changes, so the newest one applies.
            ImdbRating = Field("score")
                .Where(row => Item(Value(row, "by")) == Imdb)
                .OrderByDescending(row => Value(row, "date"), StringComparer.Ordinal)
                .Select(row => ReadRating(Value(row, "value")))
                .FirstOrDefault(rating => rating is not null),
        };
    }

    // Wikidata writes a score as its value over the scale's maximum, such as "8.8/10".
    private static Rating? ReadRating(string score) =>
        score.Split('/') is [var text, var scale]
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum)
        && double.IsFinite(maximum)
        && maximum > 0
        && value >= 0
        && value <= maximum
            ? new Rating(value / maximum * 10, VideoKind.Movie)
            : null;

    private async Task<JsonElement> Get(string query, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://query.wikidata.org/sparql?query=" + Uri.EscapeDataString(query)
        );
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/sparql-results+json")
        );
        return await ProviderJson.Get(http, request, "wikidata", cancellation).ConfigureAwait(false);
    }

    private static JsonElement[] Rows(JsonElement data) =>
        data.GetProperty("results").GetProperty("bindings").EnumerateArray().ToArray();

    // A row omits a column the query left unbound.
    private static string Value(JsonElement row, string column) =>
        row.ValueKind == JsonValueKind.Object
        && row.TryGetProperty(column, out var cell)
        && cell.ValueKind == JsonValueKind.Object
        && cell.TryGetProperty("value", out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    /// <summary>
    /// The item identity, such as "Q25188", at the end of an entity address or
    /// as it stands; empty when <paramref name="value"/> names no item.
    /// </summary>
    private static string Item(string value)
    {
        var item = value[(value.LastIndexOf('/') + 1)..];
        return item.Length is > 1 and <= 12
            && item[0] == 'Q'
            && item[1] != '0'
            && item[1..].All(char.IsAsciiDigit)
            ? item
            : string.Empty;
    }
}
