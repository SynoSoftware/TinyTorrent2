using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Subtitles.Suppliers;

internal sealed partial class SubSource(RequestPolicy policy) : Supplier(policy)
{
    private static readonly Uri Origin = new("https://api.subsource.net");
    protected override IReadOnlyList<RateWindow> RateWindows { get; } =
    [
        new(TimeSpan.FromMinutes(1), 60),
        new(TimeSpan.FromHours(1), 1800),
        new(TimeSpan.FromDays(1), 7200),
    ];

    internal override SubtitleSupplier SupplierId => SubtitleSupplier.SubSource;
    internal override Uri Privacy { get; } = new("https://subsource.net/policy");
    internal override Uri Terms { get; } = new("https://subsource.net/terms");
    protected override IReadOnlyDictionary<string, string> Codes => LanguageCodes;

    internal override string Question(SubtitleLookup lookup)
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

    protected override string? BuiltInKey { get; } =
        typeof(SubSource)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => value.Key == "SubSourceKey")
            ?.Value;

    protected override void Authenticate(HttpRequestMessage request, Account account) =>
        request.Headers.Add("X-API-Key", ApiKey(account));

    protected override async Task Check(Operation operation, CancellationToken cancellation)
    {
        var response = await Get(
                operation,
                new Uri(
                    Origin,
                    "/api/v1/movies/search?searchType=text&q=TinyTorrentConnectionCheck"
                ),
                cancellation
            )
            .ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        Data(document.RootElement);
    }

    protected override async Task<SubtitleSearch> Search(
        Operation operation,
        SubtitleLookup lookup,
        Release release,
        string language,
        CancellationToken cancellation
    )
    {
        var candidates = new List<SubtitleCandidate>();
        var total = 0;
        var query =
            "/api/v1/movies/search?searchType=text&q=" + Uri.EscapeDataString(release.Title);
        if (release.Season is { } season)
            query += "&season=" + season.ToString(CultureInfo.InvariantCulture);
        var response = await Get(operation, new Uri(Origin, query), cancellation)
            .ConfigureAwait(false);
        using var movies = Parse(response.Bytes);
        long? movieId = null;
        foreach (var movie in Data(movies.RootElement).EnumerateArray())
        {
            if (
                Text(movie, "type") != (release.Season is null ? "movie" : "tvseries")
                || release.Season is not null && Number(movie, "season") is null
                || !Matches(
                    release,
                    Text(movie, "title") ?? string.Empty,
                    Number(movie, "releaseYear"),
                    release.Season is not null ? Number(movie, "season") : null
                )
                || Number(movie, "movieId") is not { } id
                || id <= 0
            )
                continue;
            if (movieId is not null && movieId != id)
                return new(candidates);
            movieId = id;
        }
        if (movieId is null)
            return new(candidates, Data(movies.RootElement).GetArrayLength() == 0);

        for (var page = 1; ; page++)
        {
            query =
                "/api/v1/subtitles?movieId="
                + movieId.Value.ToString(CultureInfo.InvariantCulture)
                + "&language="
                + Uri.EscapeDataString(language)
                + "&sort=rating&limit=100&page="
                + page.ToString(CultureInfo.InvariantCulture);
            response = await Get(operation, new Uri(Origin, query), cancellation)
                .ConfigureAwait(false);
            using var subtitles = Parse(response.Bytes);
            foreach (var subtitle in Data(subtitles.RootElement).EnumerateArray())
            {
                total++;
                if (
                    Number(subtitle, "movieId") != movieId
                    || Text(subtitle, "language") != language
                    || !subtitle.TryGetProperty("foreignParts", out var foreign)
                    || foreign.ValueKind != JsonValueKind.False
                    || Text(subtitle, "productionType") == "forced"
                    || Number(subtitle, "files") != 1
                    || Number(subtitle, "subtitleId") is not { } id
                    || id <= 0
                    || !subtitle.TryGetProperty("releaseInfo", out var releases)
                    || releases.ValueKind != JsonValueKind.Array
                )
                    continue;
                var names = releases
                    .EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!)
                    .ToArray();
                if (names.Length == 0)
                    continue;
                foreach (var name in names)
                    candidates.Add(
                        new(lookup.Language, name, name, id.ToString(CultureInfo.InvariantCulture))
                    );
            }
            if (
                !subtitles.RootElement.TryGetProperty("pagination", out var pagination)
                || Number(pagination, "pages") is not { } pages
                || pages < 0
            )
                throw new SubtitleException(SubtitleFailure.Format);
            if (page >= pages)
                return new(candidates, total == 0);
        }
    }

    protected override async Task<byte[]> Download(
        Operation operation,
        SubtitleCandidate candidate,
        CancellationToken cancellation
    )
    {
        var download = new Uri(Origin, "/api/v1/subtitles/" + candidate.Reference + "/download");
        var response = await Get(operation, download, cancellation, ProviderHttp.FileLimit)
            .ConfigureAwait(false);
        if (!response.Bytes.AsSpan().StartsWith("PK"u8))
            throw new SubtitleException(SubtitleFailure.Format);
        return await Decode(response, cancellation).ConfigureAwait(false);
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
