using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class SubSource : Supplier
{
    private static readonly Uri Origin = new("https://api.subsource.net");
    private readonly Dictionary<string, Queue<DateTimeOffset>> _usage = [];

    internal override SubtitleSupplier SupplierId => SubtitleSupplier.SubSource;
    internal override Uri Privacy { get; } = new("https://subsource.net/policy");
    internal override Uri Terms { get; } = new("https://subsource.net/terms");
    protected override IReadOnlyDictionary<string, string> Codes => LanguageCodes;
    protected override void Authenticate(HttpRequestMessage request, Account account) =>
        request.Headers.Add("X-API-Key", account.Secret);

    internal override async Task Check(ProviderHttp http, Account account, CancellationToken cancellation)
    {
        var response = await Get(http, account,
            new Uri(Origin, "/api/v1/movies/search?searchType=text&q=TinyTorrentConnectionCheck"), cancellation).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        Data(document.RootElement);
    }

    protected override async Task<byte[]?> Find(ProviderHttp http, Account account,
        SubtitleLookup lookup, Release release, string language, CancellationToken cancellation)
    {
        var query = "/api/v1/movies/search?searchType=text&q=" + Uri.EscapeDataString(release.Title);
        if (release.Season is { } season)
            query += "&season=" + season.ToString(CultureInfo.InvariantCulture);
        var response = await Get(http, account, new Uri(Origin, query), cancellation).ConfigureAwait(false);
        using var movies = Parse(response.Bytes);
        long? movieId = null;
        foreach (var movie in Data(movies.RootElement).EnumerateArray())
        {
            if (Text(movie, "type") != (release.Season is null ? "movie" : "tvseries") ||
                release.Season is not null && Number(movie, "season") is null ||
                !Matches(release, Text(movie, "title") ?? string.Empty, Number(movie, "releaseYear"),
                    release.Season is not null ? Number(movie, "season") : null) ||
                Number(movie, "movieId") is not { } id || id <= 0)
                continue;
            if (movieId is not null && movieId != id)
                return null;
            movieId = id;
        }
        if (movieId is null)
            return null;

        for (var page = 1; ; page++)
        {
            await lookup.Validate(cancellation).ConfigureAwait(false);
            query = "/api/v1/subtitles?movieId=" + movieId.Value.ToString(CultureInfo.InvariantCulture) +
                "&language=" + Uri.EscapeDataString(language) + "&sort=rating&limit=100&page=" +
                page.ToString(CultureInfo.InvariantCulture);
            response = await Get(http, account, new Uri(Origin, query), cancellation).ConfigureAwait(false);
            using var subtitles = Parse(response.Bytes);
            foreach (var subtitle in Data(subtitles.RootElement).EnumerateArray())
            {
                if (Number(subtitle, "movieId") != movieId || Text(subtitle, "language") != language ||
                    !subtitle.TryGetProperty("foreignParts", out var foreign) || foreign.ValueKind != JsonValueKind.False ||
                    Text(subtitle, "productionType") == "forced" || Number(subtitle, "files") != 1 ||
                    Number(subtitle, "subtitleId") is not { } id || id <= 0 ||
                    !subtitle.TryGetProperty("releaseInfo", out var names) || names.ValueKind != JsonValueKind.Array ||
                    !names.EnumerateArray().Any(name => name.ValueKind == JsonValueKind.String &&
                        SameRelease(release, name.GetString()!)))
                    continue;
                await lookup.Validate(cancellation).ConfigureAwait(false);
                var download = new Uri(Origin, "/api/v1/subtitles/" +
                    id.ToString(CultureInfo.InvariantCulture) + "/download");
                response = await Get(http, account, download, cancellation, ProviderHttp.FileLimit).ConfigureAwait(false);
                if (!response.Bytes.AsSpan().StartsWith("PK"u8))
                    throw new SubtitleException(SubtitleFailure.Format);
                return await Decode(response, cancellation).ConfigureAwait(false);
            }
            if (!subtitles.RootElement.TryGetProperty("pagination", out var pagination) ||
                Number(pagination, "pages") is not { } pages || pages < 0)
                throw new SubtitleException(SubtitleFailure.Format);
            if (page >= pages)
                break;
        }
        return null;
    }

    private static JsonElement Data(JsonElement root)
    {
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new SubtitleException(SubtitleFailure.Format);
        return data;
    }

    protected override void Reserve(string key)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_usage.TryGetValue(key, out var requests))
            _usage[key] = requests = new();
        while (requests.TryPeek(out var oldest) && oldest <= now.AddDays(-1))
            requests.Dequeue();
        DateTimeOffset? retry = null;
        foreach (var (duration, maximum) in new[]
        {
            (TimeSpan.FromMinutes(1), 60), (TimeSpan.FromHours(1), 1800), (TimeSpan.FromDays(1), 7200),
        })
        {
            var recent = requests.Where(time => time > now - duration).ToArray();
            if (recent.Length < maximum)
                continue;
            var reset = recent[recent.Length - maximum] + duration;
            if (retry is null || reset > retry)
                retry = reset;
        }
        if (retry is { } deadline)
        {
            Wait(key, deadline);
            throw new SubtitleException(SubtitleFailure.Quota, deadline);
        }
        requests.Enqueue(now);
    }

}
