using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Syno.TinyTorrent.Shared.Network;
using static Syno.TinyTorrent.Shared.Network.ProviderJson;

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
            BuiltInKey = ApiKey.BuiltIn("MdbListKey"),
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
        var results = data.GetProperty("search");
        var choices = results
            .EnumerateArray()
            .Select(item =>
                TmdbId(item) is { } id && Text(item, "title") is { } title
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

    public async Task<VideoInformation> Describe(
        VideoChoice choice,
        VideoRequest request,
        CancellationToken cancellation
    )
    {
        // The identity is written into the request, so it must be a TMDB number.
        if (
            !int.TryParse(
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
        var ratings = Child(movie, "ratings");
        return new VideoInformation(
            CatalogId,
            choice.VideoId,
            Text(movie, "title") ?? string.Empty,
            VideoKind.Movie
        )
        {
            Year = ReadYear(movie),
            Genres = Child(movie, "genres") is { ValueKind: JsonValueKind.Array } genres
                ? genres
                    .EnumerateArray()
                    .Select(genre => Text(genre, "title"))
                    .OfType<string>()
                    .Distinct()
                    .ToArray()
                : [],
            Synopsis = Text(movie, "description") ?? string.Empty,
            Records = [record],
            Ratings = new()
            {
                Imdb = ReadRating(ratings, "imdb"),
                Tmdb = ReadRating(ratings, "tmdb"),
                RottenTomatoes = ReadRating(ratings, "tomatoes"),
                Popcornmeter = ReadRating(ratings, "popcorn"),
                Metacritic = ReadRating(ratings, "metacritic"),
            },
            Certification = Text(movie, "certification") ?? string.Empty,
            // MDBList writes 0 for takings it does not know.
            BoxOffice = Number(movie, "revenue") is > 0 and var takings ? takings : null,
            ImdbId = VideoInformation.ParseImdbId(Text(Child(movie, "ids"), "imdb")),
        };
    }

    // A score of 0 gives no rating, as the providers contract rules for MDBList.
    private static Rating? ReadRating(JsonElement ratings, string source) =>
        ratings.ValueKind != JsonValueKind.Array
            ? null
            : ratings
                .EnumerateArray()
                .Where(rating => Text(rating, "source") == source)
                .Select(rating =>
                    Child(rating, "score") is { ValueKind: JsonValueKind.Number } score
                    && score.TryGetDouble(out var value)
                    && value > 0
                        ? Rating.From(value, 100, VideoKind.Movie, Number(rating, "votes"))
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
}
