using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.Omdb;

/// <summary>OMDb, which describes IMDb's movies in English.</summary>
/// <param name="key">The person's own API key, or null for the one built into the release.</param>
internal sealed class Provider(ProviderHttp http, string? key) : IProvider
{
    private readonly string? _key =
        key
        ?? typeof(Provider)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => value.Key == "OmdbKey")
            ?.Value;
    public string CatalogId => "omdb";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_key);
    public bool IsLocalized => false;
    public VideoSchedule Schedule => VideoSchedule.Background;
    public IReadOnlySet<VideoKind> Kinds { get; } = new HashSet<VideoKind> { VideoKind.Movie };

    public async Task<IReadOnlyList<VideoChoice>> Search(
        string query,
        string language,
        CancellationToken cancellation
    )
    {
        var data = await Get("type=movie&s=" + Uri.EscapeDataString(query), cancellation)
            .ConfigureAwait(false);
        try
        {
            // OMDb answers a search without results with this error instead of
            // an empty list. Any other error, such as "Too many results.", is
            // not an answer, so Library must not remember it as a miss.
            if (Text(data, "Response") == "False")
                return Text(data, "Error") == "Movie not found!"
                    ? []
                    : throw new VideoException("omdb", "invalid_result");
            return data.GetProperty("Search")
                .EnumerateArray()
                .Where(item =>
                    IsTitle(Text(item, "imdbID")) && Text(item, "Title").Length is > 0 and <= 300
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
        }
        catch (Exception error)
            when (error is InvalidOperationException or KeyNotFoundException)
        {
            throw new VideoException("omdb", "invalid_result", error);
        }
    }

    public async Task<VideoInformation> Describe(
        VideoChoice choice,
        VideoRequest request,
        CancellationToken cancellation
    )
    {
        // The identity is written into the request, so it must be an IMDb title.
        if (choice.Kind != VideoKind.Movie || !IsTitle(choice.VideoId))
            throw new VideoException("omdb", "invalid_result");
        var record =
            await request.Saved(choice.VideoId, cancellation).ConfigureAwait(false)
            ?? new VideoRecord(
                choice.VideoId,
                await Get("plot=full&i=" + choice.VideoId, cancellation).ConfigureAwait(false),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            );
        var movie = record.Content;
        var title = Text(movie, "Title");
        if (Text(movie, "Response") != "True" || title.Length is 0 or > 300)
            throw new VideoException("omdb", "invalid_result");
        return new VideoInformation(CatalogId, choice.VideoId, title, VideoKind.Movie)
        {
            Year = VideoInformation.ParseYear(Text(movie, "Year")),
            Genres = Names(movie, "Genre"),
            Cast = Names(movie, "Actors"),
            Synopsis = Text(movie, "Plot"),
            Records = [record],
            ImdbRating = double.TryParse(
                Text(movie, "imdbRating"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var rating
            )
            && rating is >= 0 and <= 10
                ? new Rating(rating, VideoKind.Movie)
                : null,
        };
    }

    private async Task<JsonElement> Get(string query, CancellationToken cancellation)
    {
        if (!IsAvailable)
            throw new ProviderException("omdb", "unavailable");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://www.omdbapi.com/?apikey=" + Uri.EscapeDataString(_key!) + "&" + query
        );
        return await ProviderJson.Get(http, request, "omdb", cancellation).ConfigureAwait(false);
    }

    private static bool IsTitle(string id) =>
        id.Length is >= 9 and <= 12
        && id.StartsWith("tt", StringComparison.Ordinal)
        && id[2..].All(char.IsAsciiDigit);

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
