using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class OpenSubtitles : Supplier
{
    private static readonly Uri Origin = new("https://api.opensubtitles.com");
    private static readonly string? Key = typeof(OpenSubtitles).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(value => value.Key == "OpenSubtitlesKey")?.Value;
    private readonly Dictionary<string, Session> _sessions = [];

    internal override SubtitleSupplier SupplierId => SubtitleSupplier.OpenSubtitles;
    internal override bool Available => !string.IsNullOrWhiteSpace(Key);
    internal override bool UsesPassword => true;
    internal override bool SupportsHash => true;
    internal override Uri Privacy { get; } = new("https://www.opensubtitles.com/en/privacy");
    internal override Uri Terms { get; } = new("https://www.opensubtitles.com/en/tos");
    protected override IReadOnlyDictionary<string, string> Codes => LanguageCodes;
    protected override void Authenticate(HttpRequestMessage request, Account account) =>
        request.Headers.Add("Api-Key", Key);
    protected override bool CanRedirect(HttpRequestMessage request) => request.Method == HttpMethod.Get;
    protected override DateTimeOffset? DownloadReset(HttpRequestMessage request, ProviderReply response) =>
        request.RequestUri?.AbsolutePath == "/api/v1/download" ? Quota(response) : null;

    internal override async Task Check(ProviderHttp http, Account account, CancellationToken cancellation)
    {
        var personal = account.Username.Length > 0;
        var response = await Request(http, account, personal ? "/api/v1/infos/user" : "/api/v1/infos/languages",
            null, cancellation).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        if (personal)
        {
            if (!document.RootElement.TryGetProperty("data", out var user) || user.ValueKind != JsonValueKind.Object)
                throw new SubtitleException(SubtitleFailure.Format);
            return;
        }
        var languages = Data(document.RootElement);
        if (!languages.EnumerateArray().Any(language => Text(language, "language_code") is { Length: > 0 }))
            throw new SubtitleException(SubtitleFailure.Format);
    }

    protected override async Task<byte[]?> Find(ProviderHttp http, Account account,
        SubtitleLookup lookup, Release release, string language, CancellationToken cancellation)
    {
        for (var page = 1; ; page++)
        {
            await lookup.Validate(cancellation).ConfigureAwait(false);
            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["languages"] = language, ["order_by"] = "ratings", ["order_direction"] = "desc",
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
            };
            if (lookup.Hash is { } hash)
            {
                parameters["moviehash"] = hash;
                parameters["moviebytesize"] = lookup.Size.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                parameters["query"] = release.Title;
                if (release.Year is { } year)
                    parameters["year"] = year.ToString(CultureInfo.InvariantCulture);
                if (release.Season is { } season)
                    parameters["season_number"] = season.ToString(CultureInfo.InvariantCulture);
                if (release.Episodes.Count == 1)
                    parameters["episode_number"] = release.Episodes[0].ToString(CultureInfo.InvariantCulture);
            }
            var query = "/api/v1/subtitles?" + string.Join('&', parameters.Select(pair =>
                pair.Key + "=" + Uri.EscapeDataString(pair.Value).Replace("%20", "+", StringComparison.Ordinal)));
            var response = await Request(http, account, query, null, cancellation).ConfigureAwait(false);
            using var document = Parse(response.Bytes);
            foreach (var subtitle in Data(document.RootElement).EnumerateArray())
            {
                if (subtitle.ValueKind != JsonValueKind.Object ||
                    !subtitle.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object ||
                    Text(attributes, "language") != language ||
                    !attributes.TryGetProperty("foreign_parts_only", out var foreign) || foreign.ValueKind != JsonValueKind.False ||
                    !attributes.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
                    files.GetArrayLength() != 1 || Number(files[0], "file_id") is not { } id || id <= 0 ||
                    Number(attributes, "nb_cd") is { } discs && discs != 1 ||
                    !Match(attributes, release, lookup.Hash is not null))
                    continue;
                await lookup.Validate(cancellation).ConfigureAwait(false);
                response = await Request(http, account, "/api/v1/download",
                    new { file_id = id, sub_format = "srt" }, cancellation).ConfigureAwait(false);
                using var download = Parse(response.Bytes);
                if (!Uri.TryCreate(Text(download.RootElement, "link"), UriKind.Absolute, out var uri) || !CanDownload(uri))
                    throw new SubtitleException(SubtitleFailure.Format);
                await lookup.Validate(cancellation).ConfigureAwait(false);
                for (var redirect = 0; ; redirect++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    response = await Send(http, account, request, cancellation, ProviderHttp.FileLimit, false).ConfigureAwait(false);
                    if ((int)response.Status is >= 300 and < 400)
                    {
                        if (redirect == 3 || !Uri.TryCreate(uri, response.Header("Location"), out uri) || !CanDownload(uri))
                            throw new SubtitleException(SubtitleFailure.Format);
                        await lookup.Validate(cancellation).ConfigureAwait(false);
                        continue;
                    }
                    return await Decode(response, cancellation).ConfigureAwait(false);
                }
            }
            if (Number(document.RootElement, "total_pages") is not { } pages || pages < 0)
                throw new SubtitleException(SubtitleFailure.Format);
            if (page >= pages)
                return null;
        }
    }

    private static bool Match(JsonElement attributes, Release release, bool hashing)
    {
        if (hashing)
        {
            if (!attributes.TryGetProperty("moviehash_match", out var match) || match.ValueKind != JsonValueKind.True)
                return false;
        }
        else if (Text(attributes, "release") is not { } name || !SameRelease(release, name))
            return false;
        if (!attributes.TryGetProperty("feature_details", out var feature) || feature.ValueKind != JsonValueKind.Object)
            return false;
        if (Text(feature, "feature_type") is { } type &&
            !string.Equals(type, release.Season is null ? "Movie" : "Episode", StringComparison.OrdinalIgnoreCase))
            return false;
        return Matches(release, year: Number(feature, "year"), season: Number(feature, "season_number"),
            episode: Number(feature, "episode_number"));
    }

    private static JsonElement Data(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data : throw new SubtitleException(SubtitleFailure.Format);

    private static bool CanDownload(Uri uri) => uri is { Scheme: "https", UserInfo: "", IsDefaultPort: true } &&
        (uri.Host is "www.opensubtitles.com" or "dl.opensubtitles.com") &&
        uri.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal);

    private static DateTimeOffset? Quota(ProviderReply response)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Bytes);
            if (Number(document.RootElement, "remaining") is not <= 0)
                return null;
            if (DateTimeOffset.TryParse(Text(document.RootElement, "reset_time_utc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var reset))
                return reset > DateTimeOffset.UtcNow ? reset : DateTimeOffset.UtcNow.AddSeconds(30);
            return Retry(response) ?? DateTimeOffset.UtcNow.AddHours(1);
        }
        catch (JsonException) { return null; }
    }

    private async Task<ProviderReply> Request(ProviderHttp http, Account account, string path,
        object? body, CancellationToken cancellation)
    {
        var key = AccountKey(account);
        if (path == "/api/v1/download")
            lock (_sync)
                if (_downloads.TryGetValue(key, out var reset) && reset > DateTimeOffset.UtcNow)
                    throw new SubtitleException(SubtitleFailure.Quota, reset);
        var session = await Login(http, account, cancellation).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var address = new Uri(session.Origin, path);
                for (var redirect = 0; ; redirect++)
                {
                    using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, address);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    if (session.Token.Length > 0)
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
                    if (body is not null)
                        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                    var response = await Send(http, account, request, cancellation).ConfigureAwait(false);
                    if ((int)response.Status is < 300 or >= 400)
                        return response;
                    if (redirect == 3 || !Uri.TryCreate(address, response.Header("Location"), out var next) ||
                        next is not { Scheme: "https", UserInfo: "", IsDefaultPort: true } ||
                        next.Host != session.Origin.Host || next.AbsolutePath != address.AbsolutePath)
                        throw new SubtitleException(SubtitleFailure.Format);
                    address = next;
                }
            }
            catch (SubtitleException error) when (error.Reason == SubtitleFailure.Authentication &&
                account.Username.Length > 0 && attempt == 0)
            {
                lock (_sync)
                    _sessions.Remove(key);
                session = await Login(http, account, cancellation).ConfigureAwait(false);
            }
        }
    }

    private async Task<Session> Login(ProviderHttp http, Account account,
        CancellationToken cancellation)
    {
        if (!Configured(account))
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        if (account.Username.Length == 0)
            return new Session(string.Empty, Origin);
        var key = AccountKey(account);
        lock (_sync)
            if (_sessions.TryGetValue(key, out var session))
                return session;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, "/api/v1/login"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { username = account.Username, password = account.Secret }),
                Encoding.UTF8, "application/json"),
        };
        var response = await Send(http, account, request, cancellation).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        if (Text(document.RootElement, "token") is not { Length: > 0 } token)
            throw new SubtitleException(SubtitleFailure.Authentication);
        var origin = Origin;
        if (Text(document.RootElement, "base_url") is { } host)
        {
            if (host is not "api.opensubtitles.com" and not "vip-api.opensubtitles.com")
                throw new SubtitleException(SubtitleFailure.Format);
            origin = new Uri("https://" + host);
        }
        var authenticated = new Session(token, origin);
        lock (_sync)
            _sessions[key] = authenticated;
        return authenticated;
    }

    private sealed record Session(string Token, Uri Origin)
    {
        public override string ToString() => nameof(Session);
    }
}
