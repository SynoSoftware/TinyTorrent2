using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Library.Omdb;

/// <summary>OMDb, which describes IMDb's movies in English.</summary>
/// <param name="key">The person's own API key, or the one built into the release.</param>
internal sealed class Provider(string key) : IProvider
{
    private const string CatalogId = "omdb";

    internal static ProviderOption Option { get; } =
        new(
            CatalogId,
            "OMDb",
            new("https://www.omdbapi.com/legal.htm"),
            new("https://www.omdbapi.com/legal.htm"),
            value => new Provider(value)
        )
        {
            Access = Access.Key,
            BuiltInKey = ApiKey.BuiltIn("OmdbKey"),
        };

    // OMDb misses Rogue Nation when the search query includes its dash.
    public string Prepare(string query) => string.Join(' ', FileName.TitleWords(query));

    public async Task<VideoSearch> Search(VideoQuery query, CancellationToken cancellation)
    {
        var data = await Get(
                query.Get,
                "type=movie&s="
                    + Uri.EscapeDataString(query.Text)
                    + (
                        query.Year is { } year
                            ? "&y=" + year.ToString(CultureInfo.InvariantCulture)
                            : string.Empty
                    ),
                cancellation
            )
            .ConfigureAwait(false);
        // OMDb reports a missing movie as an error rather than an empty list.
        if (Text(data, "Response") == "False")
        {
            if (Text(data, "Error") == "Movie not found!")
                return new([], true);
            throw new VideoException(VideoFailure.InvalidResult);
        }
        var results = data.GetProperty("Search");
        var choices = results
            .EnumerateArray()
            .Select(item =>
                Text(item, "imdbID") is { } id && VideoInformation.ParseImdbId(id) is not null
                    ? new VideoChoice(
                        CatalogId,
                        id,
                        VideoKind.Movie,
                        Text(item, "Title") ?? string.Empty
                    )
                    {
                        Year = VideoInformation.ParseYear(Text(item, "Year") ?? string.Empty),
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
        // The identity is written into the request, so it must be an IMDb title.
        if (VideoInformation.ParseImdbId(choice.VideoId) is null)
            throw new VideoException(VideoFailure.InvalidResult);
        var record = await request
            .Read(
                choice.VideoId,
                async token =>
                {
                    var content = await Get(request.Get, "plot=full&i=" + choice.VideoId, token)
                        .ConfigureAwait(false);
                    if (
                        Text(content, "Response") == "False"
                        && Text(content, "Error") == "Movie not found!"
                    )
                        throw new MissingVideoException();
                    if (Text(content, "Response") != "True")
                        throw new VideoException(VideoFailure.InvalidResult);
                    return new VideoRecord(
                        choice.VideoId,
                        content,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    );
                },
                cancellation
            )
            .ConfigureAwait(false);
        var movie = record.Content;
        if (Text(movie, "Response") != "True")
            throw new VideoException(VideoFailure.InvalidResult);
        return new VideoInformation(
            CatalogId,
            choice.VideoId,
            Text(movie, "Title") ?? string.Empty,
            VideoKind.Movie
        )
        {
            Year = VideoInformation.ParseYear(Text(movie, "Year") ?? string.Empty),
            Genres = Names(movie, "Genre"),
            Cast = Names(movie, "Actors"),
            Synopsis = Text(movie, "Plot") ?? string.Empty,
            Records = [record],
            ImdbId = VideoInformation.ParseImdbId(choice.VideoId),
            Ratings = new()
            {
                Imdb = ReadRating(movie),
                RottenTomatoes = ReadScore(movie, "Rotten Tomatoes", "%", 100),
                Metacritic = ReadScore(movie, "Metacritic", "/100", 100),
            },
            Directors = Names(movie, "Director"),
            // OMDb writes "Not Rated" or "Unrated" when it knows no age rating.
            Certification = Text(movie, "Rated") is { } rated and not ("Not Rated" or "Unrated")
                ? rated
                : string.Empty,
            // OMDb writes the takings as "$75,982,834".
            BoxOffice =
                long.TryParse(
                    Text(movie, "BoxOffice"),
                    NumberStyles.AllowCurrencySymbol | NumberStyles.AllowThousands,
                    CultureInfo.GetCultureInfo("en-US"),
                    out var takings
                )
                && takings > 0
                    ? takings
                    : null,
        };
    }

    // OMDb lists the other sites' scores as text, such as "59%" for Rotten
    // Tomatoes and "50/100" for Metacritic, without their review counts.
    private static Rating? ReadScore(
        JsonElement movie,
        string source,
        string suffix,
        double maximum
    ) =>
        movie.TryGetProperty("Ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array
            ? ratings
                .EnumerateArray()
                .Where(rating => Text(rating, "Source") == source)
                .Select(rating =>
                    Text(rating, "Value") is { } text
                    && text.EndsWith(suffix, StringComparison.Ordinal)
                    && double.TryParse(
                        text[..^suffix.Length],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value
                    )
                        ? Rating.From(value, maximum, VideoKind.Movie)
                        : null
                )
                .FirstOrDefault(rating => rating is not null)
            : null;

    // OMDb writes the vote count with thousands separators, such as "2,345,678".
    private static Rating? ReadRating(JsonElement movie) =>
        double.TryParse(
            Text(movie, "imdbRating"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value
        )
            ? Rating.From(
                value,
                10,
                VideoKind.Movie,
                long.TryParse(
                    Text(movie, "imdbVotes"),
                    NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out var votes
                )
                    ? votes
                    : null
            )
            : null;

    private async Task<JsonElement> Get(
        ProviderGet get,
        string query,
        CancellationToken cancellation
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://www.omdbapi.com/?apikey=" + Uri.EscapeDataString(key) + "&" + query
        );
        return await get(request, cancellation, Refusal).ConfigureAwait(false);
    }

    // OMDb answers a refused request with success and names the failure in
    // the body; a query without usable candidates is an answer, which the caller reads.
    private static VideoFailure? Refusal(JsonElement data) =>
        Text(data, "Response") != "False"
            ? null
            : Text(data, "Error") switch
            {
                "Movie not found!" or "Too many results." => null,
                "Request limit reached!" or "Daily request limit reached!" =>
                    VideoFailure.Exhausted,
                "Invalid API key!" => VideoFailure.Refused,
                _ => VideoFailure.Unavailable,
            };

    // OMDb writes "N/A" for a fact it lacks.
    private static string? Text(JsonElement element, string property) =>
        ProviderJson.Text(element, property) is { } text && text != "N/A" ? text : null;

    // OMDb lists names in one comma-separated text.
    private static IReadOnlyList<string> Names(JsonElement element, string property) =>
        Text(element, property) is { } names
            ? names
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct()
                .ToArray()
            : [];
}
