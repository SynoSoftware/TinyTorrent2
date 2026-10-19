using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using static Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent.Subtitles.Suppliers;

internal sealed partial class SubSource : ISupplier
{
    private static readonly Uri Origin = new("https://api.subsource.net");
    public IReadOnlyList<RateWindow> RateWindows { get; } =
    [
        new(TimeSpan.FromMinutes(1), 60),
        new(TimeSpan.FromHours(1), 1800),
        new(TimeSpan.FromDays(1), 7200),
    ];

    public SubtitleSupplier SupplierId => SubtitleSupplier.SubSource;
    public Uri Privacy { get; } = new("https://subsource.net/policy");
    public Uri Terms { get; } = new("https://subsource.net/terms");
    public IReadOnlyDictionary<string, string> Codes => LanguageCodes;

    public string Question(SubtitleLookup lookup)
    {
        var release = FileName.Interpret(lookup.FileName);
        return JsonSerializer.Serialize(
            new
            {
                release.Title,
                release.Year,
                release.Season,
                release.Episodes,
            }
        );
    }

    public string? BuiltInKey { get; } =
        typeof(SubSource)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => value.Key == "SubSourceKey")
            ?.Value;

    public void Authenticate(HttpRequestMessage request, string key) =>
        request.Headers.Add("X-API-Key", key);

    public async Task Check(SupplierClient client, CancellationToken cancellation)
    {
        var reply = await client
            .Get(
                new Uri(
                    Origin,
                    "/api/v1/movies/search?searchType=text&q=TinyTorrentConnectionCheck"
                ),
                cancellation
            )
            .ConfigureAwait(false);
        using var document = Parse(reply.Bytes);
        Data(document.RootElement);
    }

    public async Task<IReadOnlyDictionary<long, Listing>?> Titles(
        SubtitleQuery query,
        SupplierClient client,
        CancellationToken cancellation
    )
    {
        var release = query.Release;
        var address =
            "/api/v1/movies/search?searchType=text&q=" + Uri.EscapeDataString(release.Title);
        if (release.Season is { } season)
            address += "&season=" + season.ToString(CultureInfo.InvariantCulture);
        var reply = await client.Get(new Uri(Origin, address), cancellation).ConfigureAwait(false);
        using var movies = Parse(reply.Bytes);
        var listings = new Dictionary<long, Listing>();
        foreach (var movie in Data(movies.RootElement).EnumerateArray())
        {
            // A series listing is one season of the show.
            var type = Text(movie, "type");
            var series = type == "tvseries";
            if (
                type is not ("movie" or "tvseries")
                || Text(movie, "title") is not { } title
                || series && Number(movie, "season") is null
                || Number(movie, "movieId") is not { } id
                || id <= 0
            )
                continue;
            listings[id] = new(series ? VideoKind.Series : VideoKind.Movie, [title])
            {
                Year = (int?)Number(movie, "releaseYear"),
                Season = series ? (int?)Number(movie, "season") : null,
            };
        }
        return listings;
    }

    public async IAsyncEnumerable<SubtitleSearch> Search(
        SubtitleQuery query,
        SupplierClient client,
        [EnumeratorCancellation] CancellationToken cancellation
    )
    {
        // Titles always lists, so the host searches only a title it chose.
        var title = query.Title!;
        for (var page = 1; ; page++)
        {
            var address =
                "/api/v1/subtitles?movieId="
                + title.TitleId.ToString(CultureInfo.InvariantCulture)
                + "&language="
                + Uri.EscapeDataString(query.Code)
                + "&sort=rating&limit=100&page="
                + page.ToString(CultureInfo.InvariantCulture);
            var reply = await client
                .Get(new Uri(Origin, address), cancellation)
                .ConfigureAwait(false);
            using var subtitles = Parse(reply.Bytes);
            var data = Data(subtitles.RootElement);
            var candidates = new List<SubtitleCandidate>();
            foreach (var subtitle in data.EnumerateArray())
            {
                if (
                    Number(subtitle, "movieId") != title.TitleId
                    || Text(subtitle, "language") != query.Code
                    || Number(subtitle, "files") != 1
                    || Number(subtitle, "subtitleId") is not { } id
                    || id <= 0
                    || !subtitle.TryGetProperty("releaseInfo", out var releases)
                    || releases.ValueKind != JsonValueKind.Array
                )
                    continue;
                // Only an explicit false says that the subtitle is full.
                var forced =
                    !subtitle.TryGetProperty("foreignParts", out var foreign)
                    || foreign.ValueKind != JsonValueKind.False
                    || Text(subtitle, "productionType") == "forced";
                var names = releases
                    .EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!);
                foreach (var name in names)
                    candidates.Add(
                        new(query.Language, name, name, id.ToString(CultureInfo.InvariantCulture))
                        {
                            Season = title.Listing.Season,
                            Forced = forced,
                        }
                    );
            }
            if (
                !subtitles.RootElement.TryGetProperty("pagination", out var pagination)
                || Number(pagination, "pages") is not { } pages
                || pages < 0
            )
                throw new SubtitleException(SubtitleFailure.Format);
            yield return new(candidates, data.GetArrayLength() == 0);
            if (page >= pages)
                yield break;
        }
    }

    public async Task<ProviderReply> Download(
        SubtitleCandidate candidate,
        SupplierClient client,
        CancellationToken cancellation
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(Origin, "/api/v1/subtitles/" + candidate.Reference + "/download")
        );
        var reply = await client
            .Send(request, SupplierRequest.File, cancellation)
            .ConfigureAwait(false);
        return reply.Bytes.AsSpan().StartsWith("PK"u8)
            ? reply
            : throw new SubtitleException(SubtitleFailure.Format);
    }

    private static JsonElement Data(JsonElement root)
    {
        if (
            !root.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
        )
            throw new SubtitleException(SubtitleFailure.Format);
        return data;
    }
}
