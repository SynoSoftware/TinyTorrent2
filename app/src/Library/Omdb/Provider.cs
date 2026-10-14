using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Syno.TinyTorrent.Models;

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
            BuiltInKey = typeof(Provider)
                .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(value => value.Key == "OmdbKey")
                ?.Value,
        };

    // OMDb misses Rogue Nation when the search query includes its dash.
    public string Prepare(string query) =>
        Regex.Replace(query, @"[^\p{L}\p{N}]+", " ").Trim();

    public async Task<VideoSearch> Search(
        VideoQuery query,
        CancellationToken cancellation
    )
    {
        var data = await Get(query.Get, "type=movie&s=" + Uri.EscapeDataString(query.Text)
                + (query.Year is { } year ? "&y=" + year.ToString(CultureInfo.InvariantCulture) : string.Empty), cancellation)
            .ConfigureAwait(false);
        try
        {
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
                .Where(item =>
                    VideoInformation.ParseImdbId(Text(item, "imdbID")) is not null && Text(item, "Title").Length is > 0 and <= 300
                )
                .Select(item =>
                    new VideoChoice(
                        CatalogId,
                        Text(item, "imdbID"),
                        VideoKind.Movie,
                        Text(item, "Title")
                    )
                    {
                        Year = VideoInformation.ParseYear(Text(item, "Year")),
                    }
                )
                .ToArray();
            return new(choices, results.GetArrayLength() == 0);
        }
        catch (Exception error)
            when (error is InvalidOperationException or KeyNotFoundException)
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
        // The identity is written into the request, so it must be an IMDb title.
        if (choice.Kind != VideoKind.Movie || VideoInformation.ParseImdbId(choice.VideoId) is null)
            throw new VideoException(VideoFailure.InvalidResult);
        var record = await request.Read(choice.VideoId, async token =>
        {
            var content = await Get(request.Get, "plot=full&i=" + choice.VideoId, token).ConfigureAwait(false);
            if (Text(content, "Response") == "False" && Text(content, "Error") == "Movie not found!")
                throw new MissingVideoException();
            if (Text(content, "Response") != "True" || Text(content, "Title").Length is 0 or > 300)
                throw new VideoException(VideoFailure.InvalidResult);
            return new VideoRecord(choice.VideoId, content, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }, cancellation).ConfigureAwait(false);
        var movie = record.Content;
        var title = Text(movie, "Title");
        if (Text(movie, "Response") != "True" || title.Length is 0 or > 300)
            throw new VideoException(VideoFailure.InvalidResult);
        return new VideoInformation(CatalogId, choice.VideoId, title, VideoKind.Movie)
        {
            Year = VideoInformation.ParseYear(Text(movie, "Year")),
            Genres = Names(movie, "Genre"),
            Cast = Names(movie, "Actors"),
            Synopsis = Text(movie, "Plot"),
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
            Certification = Text(movie, "Rated") is "Not Rated" or "Unrated" ? string.Empty : Text(movie, "Rated"),
            // OMDb writes the takings as "$75,982,834".
            BoxOffice = long.TryParse(
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
    private static Rating? ReadScore(JsonElement movie, string source, string suffix, double maximum) =>
        movie.TryGetProperty("Ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array
            ? ratings
                .EnumerateArray()
                .Where(rating => Text(rating, "Source") == source && Text(rating, "Value").EndsWith(suffix, StringComparison.Ordinal))
                .Select(rating =>
                    double.TryParse(
                        Text(rating, "Value")[..^suffix.Length],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value
                    )
                    && value >= 0
                    && value <= maximum
                        ? new Rating(value / maximum * 10, VideoKind.Movie)
                        : null
                )
                .FirstOrDefault(rating => rating is not null)
            : null;

    // OMDb writes the vote count with thousands separators, such as "2,345,678".
    private static Rating? ReadRating(JsonElement movie) =>
        double.TryParse(Text(movie, "imdbRating"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && value is >= 0 and <= 10
            ? new Rating(value, VideoKind.Movie)
            {
                Votes = long.TryParse(
                    Text(movie, "imdbVotes"),
                    NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out var votes
                )
                && votes > 0
                    ? votes
                    : null,
            }
            : null;

    private async Task<JsonElement> Get(ProviderGet get, string query, CancellationToken cancellation)
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
                "Request limit reached!" or "Daily request limit reached!" => VideoFailure.Exhausted,
                "Invalid API key!" => VideoFailure.Refused,
                _ => VideoFailure.Unavailable,
            };

    // OMDb writes "N/A" for a fact it lacks.
    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && text != "N/A"
            ? text.Trim()
            : string.Empty;

    // OMDb lists names in one comma-separated text.
    private static IReadOnlyList<string> Names(JsonElement element, string property) =>
        Text(element, property)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToArray();
}
