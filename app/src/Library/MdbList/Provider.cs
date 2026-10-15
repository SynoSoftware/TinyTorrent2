using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library.MdbList;

/// <summary>
/// MDBList, which describes movies in English with ratings it gathers from
/// IMDb, TMDB and other sites. It names no cast.
/// </summary>
/// <param name="key">The person's own API key, or the one built into the release.</param>
internal sealed class Provider(string key) : IProvider
{
    private const string CatalogId = "mdblist";

    internal static ProviderOption Option { get; } =
        new(
            CatalogId,
            "MDBList",
            new("https://mdblist.com/static/legal.html"),
            new("https://mdblist.com/static/legal.html"),
            value => new Provider(value)
        )
        {
            Access = Access.Key,
            BuiltInKey = typeof(Provider)
                .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(value => value.Key == "MdbListKey")
                ?.Value,
        };

    // A movie is named by its TMDB identity, because some results lack an IMDb one.
    public async Task<VideoSearch> Search(VideoQuery query, CancellationToken cancellation)
    {
        var data = await Get(
                query.Get,
                "search/movie?query="
                    + Uri.EscapeDataString(query.Text)
                    + (
                        query.Year is { } year
                            ? "&year=" + year.ToString(CultureInfo.InvariantCulture)
                            : string.Empty
                    ),
                cancellation
            )
            .ConfigureAwait(false);
        try
        {
            var results = data.GetProperty("search");
            var choices = results
                .EnumerateArray()
                .Select(item =>
                    TmdbId(item) is { } id
                    && Text(item, "title") is { Length: > 0 and <= 300 } title
                        ? new VideoChoice(CatalogId, id, VideoKind.Movie, title)
                        {
                            Year = ReadYear(item),
                        }
                        : null
                )
                .OfType<VideoChoice>()
                .ToArray();
            return new(choices, results.GetArrayLength() == 0);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
        {
            throw new VideoException(VideoFailure.InvalidResult, error);
        }
    }

    public async Task<VideoInformation> Describe(
        VideoChoice choice,
        VideoRequest request,
        CancellationToken cancellation
    )
    {
        // The identity is written into the request, so it must be a TMDB number.
        if (
            choice.Kind != VideoKind.Movie
            || !int.TryParse(
                choice.VideoId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number
            )
            || number <= 0
        )
            throw new VideoException(VideoFailure.InvalidResult);
        var path = "tmdb/movie/" + choice.VideoId;
        var record = await request
            .Read(
                path,
                async token =>
                {
                    var content = await Get(request.Get, path, token).ConfigureAwait(false);
                    if (Text(content, "title").Length is 0 or > 300)
                        throw new VideoException(VideoFailure.InvalidResult);
                    return new VideoRecord(
                        path,
                        content,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    );
                },
                cancellation
            )
            .ConfigureAwait(false);
        var movie = record.Content;
        var title = Text(movie, "title");
        if (title.Length is 0 or > 300)
            throw new VideoException(VideoFailure.InvalidResult);
        var ratings = Child(movie, "ratings");
        return new VideoInformation(CatalogId, choice.VideoId, title, VideoKind.Movie)
        {
            Year = ReadYear(movie),
            Genres = Child(movie, "genres") is { ValueKind: JsonValueKind.Array } genres
                ? genres
                    .EnumerateArray()
                    .Select(genre => Text(genre, "title"))
                    .Where(name => name.Length > 0)
                    .Distinct()
                    .ToArray()
                : [],
            Synopsis = Text(movie, "description"),
            Records = [record],
            Ratings = new()
            {
                Imdb = ReadRating(ratings, "imdb"),
                Tmdb = ReadRating(ratings, "tmdb"),
                RottenTomatoes = ReadRating(ratings, "tomatoes"),
                Popcornmeter = ReadRating(ratings, "popcorn"),
                Metacritic = ReadRating(ratings, "metacritic"),
            },
            Certification = Text(movie, "certification"),
            // MDBList writes 0 for takings it does not know.
            BoxOffice =
                Child(movie, "revenue") is { ValueKind: JsonValueKind.Number } revenue
                && revenue.TryGetInt64(out var takings)
                && takings > 0
                    ? takings
                    : null,
            ImdbId = VideoInformation.ParseImdbId(Text(Child(movie, "ids"), "imdb")),
        };
    }

    // Each rating's score is its source's rating on a 0 to 100 scale, and its
    // votes the count behind it; a source without votes has no score.
    private static Rating? ReadRating(JsonElement ratings, string source) =>
        ratings.ValueKind != JsonValueKind.Array
            ? null
            : ratings
                .EnumerateArray()
                .Where(rating => Text(rating, "source") == source)
                .Select(rating =>
                    Child(rating, "score") is { ValueKind: JsonValueKind.Number } score
                    && score.TryGetDouble(out var value)
                    && value is > 0 and <= 100
                        ? new Rating(value / 10, VideoKind.Movie)
                        {
                            Votes =
                                Child(rating, "votes") is { ValueKind: JsonValueKind.Number } count
                                && count.TryGetInt64(out var votes)
                                && votes > 0
                                    ? votes
                                    : null,
                        }
                        : null
                )
                .FirstOrDefault(rating => rating is not null);

    private async Task<JsonElement> Get(
        ProviderGet get,
        string path,
        CancellationToken cancellation
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.mdblist.com/"
                + path
                + (path.Contains('?') ? "&" : "?")
                + "apikey="
                + Uri.EscapeDataString(key)
        );
        return await get(request, cancellation).ConfigureAwait(false);
    }

    private static int? ReadYear(JsonElement item) =>
        Child(item, "year") is { ValueKind: JsonValueKind.Number } year
        && year.TryGetInt32(out var value)
            ? value
            : null;

    private static string? TmdbId(JsonElement item) =>
        Child(Child(item, "ids"), "tmdbid") is { ValueKind: JsonValueKind.Number } id
        && id.TryGetInt32(out var value)
        && value > 0
            ? value.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string Text(JsonElement element, string property) =>
        Child(element, property) is { ValueKind: JsonValueKind.String } text
            ? text.GetString()!.Trim()
            : string.Empty;

    // A missing property reads as an undefined element, so nested reads need no checks.
    private static JsonElement Child(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var child)
            ? child
            : default;
}
