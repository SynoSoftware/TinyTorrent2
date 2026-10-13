using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.MdbList;

/// <summary>
/// MDBList, which describes movies in English with ratings it gathers from
/// IMDb, TMDB and other sites. It names no cast.
/// </summary>
/// <param name="key">The person's own API key, or null for the one built into the release.</param>
internal sealed class Provider(ProviderHttp http, string? key) : IProvider
{
    private readonly string? _key =
        key
        ?? typeof(Provider)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => value.Key == "MdbListKey")
            ?.Value;
    public string CatalogId => "mdblist";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_key);
    public bool IsLocalized => false;
    public VideoSchedule Schedule => VideoSchedule.Background;
    public IReadOnlySet<VideoKind> Kinds { get; } = new HashSet<VideoKind> { VideoKind.Movie };

    // A movie is named by its TMDB identity, because some results lack an IMDb one.
    public async Task<IReadOnlyList<VideoChoice>> Search(
        string query,
        string language,
        CancellationToken cancellation
    )
    {
        var data = await Get("search/movie?query=" + Uri.EscapeDataString(query), cancellation)
            .ConfigureAwait(false);
        try
        {
            return data.GetProperty("search")
                .EnumerateArray()
                .Select(item =>
                    TmdbId(item) is { } id && Text(item, "title") is { Length: > 0 and <= 300 } title
                        ? new VideoChoice(CatalogId, id, VideoKind.Movie, title)
                        {
                            Year = ReadYear(item),
                        }
                        : null
                )
                .OfType<VideoChoice>()
                .ToArray();
        }
        catch (Exception error)
            when (error is InvalidOperationException or KeyNotFoundException)
        {
            throw new VideoException("mdblist", "invalid_result", error);
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
            || !int.TryParse(choice.VideoId, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0
        )
            throw new VideoException("mdblist", "invalid_result");
        var path = "tmdb/movie/" + choice.VideoId;
        var record =
            await request.Saved(path, cancellation).ConfigureAwait(false)
            ?? new VideoRecord(
                path,
                await Get(path, cancellation).ConfigureAwait(false),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            );
        var movie = record.Content;
        var title = Text(movie, "title");
        if (title.Length is 0 or > 300)
            throw new VideoException("mdblist", "invalid_result");
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
            ImdbRating = ReadRating(ratings, "imdb"),
            TmdbRating = ReadRating(ratings, "tmdb"),
        };
    }

    // Each rating's score is its source's rating on a 0 to 100 scale; a
    // source without votes has none.
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
                        : null
                )
                .FirstOrDefault(rating => rating is not null);

    private async Task<JsonElement> Get(string path, CancellationToken cancellation)
    {
        if (!IsAvailable)
            throw new ProviderException("mdblist", "unavailable");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.mdblist.com/"
                + path
                + (path.Contains('?') ? "&" : "?")
                + "apikey="
                + Uri.EscapeDataString(_key!)
        );
        return await ProviderJson.Get(http, request, "mdblist", cancellation).ConfigureAwait(false);
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
