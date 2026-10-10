using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library.Tmdb;

internal sealed class Provider(ProviderHttp http) : IProvider
{
    private static readonly string? Token = typeof(Provider).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(value => value.Key == "TmdbToken")?.Value;
    public string CatalogId => "tmdb";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Token);
    public bool IsLocalized => true;
    public VideoSchedule Schedule { get; } = new VideoSchedule.Background();
    public IReadOnlySet<VideoKind> Kinds { get; } = new HashSet<VideoKind> { VideoKind.Movie, VideoKind.Series };

    public async Task<IReadOnlyList<VideoChoice>> Search(string query, string language, CancellationToken cancellation)
    {
        var data = await Get("search/multi?query=" + Uri.EscapeDataString(query) +
            "&language=" + Uri.EscapeDataString(language), cancellation).ConfigureAwait(false);
        try
        {
            return data.GetProperty("results").EnumerateArray()
                .Where(item => Text(item, "media_type") is "movie" or "tv")
                .Select(item => Text(item, "media_type") == "movie"
                    ? new VideoChoice(CatalogId, Id(item), VideoKind.Movie, Text(item, "title"))
                        { Year = VideoInformation.ParseYear(Text(item, "release_date")), Original = Text(item, "original_title") }
                    : new VideoChoice(CatalogId, Id(item), VideoKind.Series, Text(item, "name"))
                        { Year = VideoInformation.ParseYear(Text(item, "first_air_date")), Original = Text(item, "original_name") })
                .ToArray();
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new VideoException("tmdb", "invalid_result", error);
        }
    }

    public async Task<VideoInformation> Describe(VideoChoice choice, VideoRequest request, CancellationToken cancellation)
    {
        if (choice.Kind is not (VideoKind.Movie or VideoKind.Series) ||
            !int.TryParse(choice.VideoId, out var identity) || identity <= 0)
            throw new VideoException("tmdb", "invalid_result");
        var release = request.Release;
        if (choice.Kind == VideoKind.Movie)
        {
            var movie = await Read("movie/" + choice.VideoId, request, cancellation).ConfigureAwait(false);
            return Information(choice, [movie], []);
        }
        var season = release.Season!.Value;
        var root = "tv/" + choice.VideoId;
        var records = new List<VideoRecord> { await Read(root, request, cancellation).ConfigureAwait(false) };
        foreach (var episode in release.Episodes)
            records.Add(await Read($"{root}/season/{season}/episode/{episode}", request, cancellation).ConfigureAwait(false));
        return Information(choice, records, release.Episodes.Select(episode => (season, episode)).ToArray());
    }

    private VideoInformation Information(VideoChoice choice, IReadOnlyList<VideoRecord> records,
        IReadOnlyList<(int Season, int Number)> numbers)
    {
        try
        {
            var data = records[0].Content;
            var keywords = data.TryGetProperty("keywords", out var terms)
                ? terms.TryGetProperty("keywords", out var movieWords) ? Names(movieWords) :
                    terms.TryGetProperty("results", out var seriesWords) ? Names(seriesWords) : []
                : [];
            var cast = data.TryGetProperty("credits", out var credits) && credits.TryGetProperty("cast", out var actors)
                ? Names(actors) : [];
            var genres = Names(data.GetProperty("genres"));
            if (choice.Kind == VideoKind.Movie)
                return new VideoInformation(CatalogId, choice.VideoId, Text(data, "title"), VideoKind.Movie)
                {
                    Year = VideoInformation.ParseYear(Text(data, "release_date")), Genres = genres, Cast = cast,
                    Synopsis = Text(data, "overview"), Keywords = keywords, Records = records,
                };
            var episodes = numbers.Select((number, index) => new Episode(number.Season, number.Number,
                Text(records[index + 1].Content, "name"), Text(records[index + 1].Content, "overview"))).ToArray();
            return new VideoInformation(CatalogId,
                $"tv/{choice.VideoId}/{numbers[0].Season}/{string.Join(',', numbers.Select(number => number.Number))}",
                Text(data, "name"), VideoKind.Episode)
            {
                Series = choice, Episodes = episodes,
                Year = records.Skip(1).Select(record => VideoInformation.ParseYear(Text(record.Content, "air_date"))).FirstOrDefault(year => year is not null),
                Genres = genres, Cast = cast, Keywords = keywords, Records = records,
            };
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new VideoException("tmdb", "invalid_result", error);
        }
    }

    private async Task<JsonElement> Get(string path, CancellationToken cancellation)
    {
        if (!IsAvailable)
            throw new ProviderException("tmdb", "unavailable");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        ProviderReply reply;
        try { reply = await http.Send(request, ProviderHttp.JsonLimit, cancellation).ConfigureAwait(false); }
        catch (HttpRouteException error)
        {
            throw new ProviderException("tmdb", "route_unavailable", inner: error);
        }
        catch (InvalidDataException error)
        {
            throw new ProviderException("tmdb", "invalid_result", inner: error);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            throw new ProviderException("tmdb", "unavailable", inner: error);
        }
        DateTimeOffset? retryAt = null;
        if (reply.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            retryAt = reply.RetryAt ?? DateTimeOffset.UtcNow.AddMinutes(1);
        if (reply.Status != HttpStatusCode.OK)
            throw new ProviderException("tmdb", "unavailable", retryAt);
        try
        {
            using var document = JsonDocument.Parse(reply.Bytes);
            return document.RootElement.Clone();
        }
        catch (JsonException error) { throw new ProviderException("tmdb", "invalid_result", inner: error); }
    }

    private async Task<VideoRecord> Read(string path, VideoRequest request, CancellationToken cancellation)
    {
        if (await request.Saved(path, cancellation).ConfigureAwait(false) is { } saved)
            return saved;
        var content = await Get(path + (path.Contains("/season/", StringComparison.Ordinal)
            ? "?language=" : "?append_to_response=credits,keywords&language=") +
            Uri.EscapeDataString(request.Language), cancellation).ConfigureAwait(false);
        return new VideoRecord(path, content, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static string Id(JsonElement item) => item.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
    private static IReadOnlyList<string> Names(JsonElement values) =>
        values.EnumerateArray().Select(value => Text(value, "name")).Where(value => value.Length > 0).Distinct().ToArray();
}
