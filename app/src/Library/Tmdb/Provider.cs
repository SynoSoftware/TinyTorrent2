using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using static Syno.TinyTorrent.Shared.Network.ProviderJson;

namespace Syno.TinyTorrent.Library.Tmdb;

/// <param name="key">The person's own API key or API read access token, or the token built into the release.</param>
internal sealed class Provider(string key) : IProvider
{
    private const string CatalogId = "tmdb";

    internal static ProviderOption Option { get; } =
        new(
            CatalogId,
            "TMDB",
            new("https://www.themoviedb.org/privacy-policy"),
            new("https://www.themoviedb.org/api-terms-of-use"),
            value => new Provider(value)
        )
        {
            Access = Access.Key,
            BuiltInKey = ApiKey.BuiltIn("TmdbToken"),
            Kinds = new HashSet<VideoKind> { VideoKind.Movie, VideoKind.Series },
            IsLocalized = true,
        };

    public async Task<VideoSearch> Search(VideoQuery query, CancellationToken cancellation)
    {
        if (query.Year is { } year)
        {
            var choices = new List<VideoChoice>();
            var empty = true;
            foreach (var kind in new[] { VideoKind.Movie, VideoKind.Series })
            {
                var path = kind == VideoKind.Movie ? "search/movie" : "search/tv";
                var response = await Get(
                        query.Get,
                        path
                            + "?query="
                            + Uri.EscapeDataString(query.Text)
                            + "&year="
                            + year.ToString(CultureInfo.InvariantCulture)
                            + "&language="
                            + Uri.EscapeDataString(query.Language),
                        cancellation
                    )
                    .ConfigureAwait(false);
                var results = response.GetProperty("results");
                empty &= results.GetArrayLength() == 0;
                choices.AddRange(results.EnumerateArray().Select(item => Choice(item, kind)));
            }
            return new(choices, empty);
        }
        var data = await Get(
                query.Get,
                "search/multi?query="
                    + Uri.EscapeDataString(query.Text)
                    + "&language="
                    + Uri.EscapeDataString(query.Language),
                cancellation
            )
            .ConfigureAwait(false);
        var found = data.GetProperty("results");
        return new(
            found
                .EnumerateArray()
                .Where(item => Text(item, "media_type") is "movie" or "tv")
                .Select(item =>
                    Choice(
                        item,
                        Text(item, "media_type") == "movie" ? VideoKind.Movie : VideoKind.Series
                    )
                )
                .ToArray(),
            found.GetArrayLength() == 0
        );
    }

    public async Task<VideoInformation> Describe(
        VideoChoice choice,
        VideoRequest request,
        CancellationToken cancellation
    )
    {
        if (!int.TryParse(choice.VideoId, out var identity) || identity <= 0)
            throw new VideoException(VideoFailure.InvalidResult);
        var root = (choice.Kind == VideoKind.Movie ? "movie/" : "tv/") + choice.VideoId;
        var record = await Read(root, request, value => Information(choice, value), cancellation)
            .ConfigureAwait(false);
        var information = Information(choice, record);
        if (choice.Kind == VideoKind.Movie)
            return information;
        var release = request.Release;
        var season = release.Season!.Value;
        var records = new List<VideoRecord> { record };
        var episodes = new List<Episode>();
        foreach (var episode in release.Episodes)
        {
            var detail = await Read(
                    $"{root}/season/{season}/episode/{episode}",
                    request,
                    value => ReadEpisode(value, season, episode),
                    cancellation
                )
                .ConfigureAwait(false);
            records.Add(detail);
            episodes.Add(ReadEpisode(detail, season, episode));
        }
        return information with
        {
            VideoId = $"tv/{choice.VideoId}/{season}/{string.Join(',', release.Episodes)}",
            Kind = VideoKind.Episode,
            Series = choice,
            Episodes = episodes,
            Year = records
                .Skip(1)
                .Select(value =>
                    VideoInformation.ParseYear(Text(value.Content, "air_date") ?? string.Empty)
                )
                .FirstOrDefault(year => year is not null),
            Records = records,
        };
    }

    private VideoInformation Information(VideoChoice choice, VideoRecord record)
    {
        var data = record.Content;
        var title = Text(data, choice.Kind == VideoKind.Movie ? "title" : "name") ?? string.Empty;
        if (data.GetProperty("id").GetInt32() <= 0)
            throw new VideoException(VideoFailure.InvalidResult);
        var keywords = data.TryGetProperty("keywords", out var terms)
            ? terms.TryGetProperty("keywords", out var movieWords)
                ? Names(movieWords)
                : terms.TryGetProperty("results", out var seriesWords)
                    ? Names(seriesWords)
                    : []
            : [];
        var cast =
            data.TryGetProperty("credits", out var credits)
            && credits.TryGetProperty("cast", out var actors)
                ? Names(actors)
                : [];
        var genres = Names(data.GetProperty("genres"));
        // A movie's recommendations are movies and a series' are series,
        // so each takes the kind of the described choice.
        VideoChoice[] related =
            data.TryGetProperty("recommendations", out var recommendations)
            && recommendations.TryGetProperty("results", out var results)
                ? results.EnumerateArray().Select(item => Choice(item, choice.Kind)).ToArray()
                : [];
        return new VideoInformation(CatalogId, choice.VideoId, title, choice.Kind)
        {
            Year = VideoInformation.ParseYear(Text(data, "release_date") ?? string.Empty),
            Genres = genres,
            Cast = cast,
            Synopsis =
                choice.Kind == VideoKind.Movie
                    ? Text(data, "overview") ?? string.Empty
                    : string.Empty,
            Keywords = keywords,
            Records = [record],
            Ratings = new() { Tmdb = ReadRating(data, choice.Kind) },
            Directors =
                data.TryGetProperty("credits", out var team)
                && team.TryGetProperty("crew", out var crew)
                && crew.ValueKind == JsonValueKind.Array
                    ? crew.EnumerateArray()
                        .Where(member => Text(member, "job") == "Director")
                        .Select(member => Text(member, "name"))
                        .OfType<string>()
                        .Distinct()
                        .ToArray()
                    : [],
            // TMDB writes 0 for takings it does not know.
            BoxOffice = Number(data, "revenue") is > 0 and var takings ? takings : null,
            ImdbId =
                choice.Kind == VideoKind.Movie
                    ? VideoInformation.ParseImdbId(Text(data, "imdb_id"))
                    : null,
            Related = related,
        };
    }

    private static Episode ReadEpisode(VideoRecord record, int season, int number)
    {
        var data = record.Content;
        if (Text(data, "name") is not { } name || data.GetProperty("id").GetInt32() <= 0)
            throw new VideoException(VideoFailure.InvalidResult);
        return new(season, number, name, Text(data, "overview") ?? string.Empty);
    }

    private static Rating? ReadRating(JsonElement data, VideoKind kind) =>
        Number(data, "vote_count") is > 0 and var votes
        && Child(data, "vote_average") is { ValueKind: JsonValueKind.Number } average
        && average.TryGetDouble(out var value)
            ? Rating.From(value, 10, kind, votes)
            : null;

    private async Task<JsonElement> Get(
        ProviderGet get,
        string path,
        CancellationToken cancellation
    )
    {
        // A TMDB account shows both an API read access token, a JWT that
        // contains dots, and a shorter API key, which TMDB accepts only as a
        // query parameter; a person may paste either.
        var bearer = key.Contains('.');
        var credential = bearer
            ? string.Empty
            : (path.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.themoviedb.org/3/" + path + credential
        );
        if (bearer)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await get(request, cancellation).ConfigureAwait(false);
    }

    private Task<VideoRecord> Read(
        string path,
        VideoRequest request,
        Action<VideoRecord> decode,
        CancellationToken cancellation
    ) =>
        request.Read(
            path,
            async token =>
            {
                var content = await Get(
                        request.Get,
                        path
                            + (
                                path.Contains("/season/", StringComparison.Ordinal)
                                    ? "?language="
                                    : "?append_to_response=credits,keywords,recommendations&language="
                            )
                            + Uri.EscapeDataString(request.Language),
                        token
                    )
                    .ConfigureAwait(false);
                var record = new VideoRecord(
                    path,
                    content,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                );
                // The shared reader saves a successful callback before reading another resource.
                decode(record);
                return record;
            },
            cancellation
        );

    private VideoChoice Choice(JsonElement item, VideoKind kind) =>
        kind == VideoKind.Movie
            ? new VideoChoice(
                CatalogId,
                Id(item),
                VideoKind.Movie,
                Text(item, "title") ?? string.Empty
            )
            {
                Year = VideoInformation.ParseYear(Text(item, "release_date") ?? string.Empty),
                Original = Text(item, "original_title"),
            }
            : new VideoChoice(
                CatalogId,
                Id(item),
                VideoKind.Series,
                Text(item, "name") ?? string.Empty
            )
            {
                Year = VideoInformation.ParseYear(Text(item, "first_air_date") ?? string.Empty),
                Original = Text(item, "original_name"),
            };

    private static string Id(JsonElement item) =>
        item.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> Names(JsonElement values) =>
        values
            .EnumerateArray()
            .Select(value => Text(value, "name"))
            .OfType<string>()
            .Distinct()
            .ToArray();
}
