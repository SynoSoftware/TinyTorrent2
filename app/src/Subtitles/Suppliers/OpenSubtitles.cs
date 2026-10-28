using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using static Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent.Subtitles.Suppliers;

internal sealed partial class OpenSubtitles : ISupplier
{
    private static readonly Uri Origin = new("https://api.opensubtitles.com");
    private readonly object _sync = new();
    private readonly Dictionary<string, Session> _sessions = [];

    public SubtitleSupplier SupplierId => SubtitleSupplier.OpenSubtitles;
    public bool Available => !string.IsNullOrWhiteSpace(BuiltInKey);
    public Access Access => Access.Password;
    public string? BuiltInKey { get; } =
        typeof(OpenSubtitles)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => value.Key == "OpenSubtitlesKey")
            ?.Value;
    public bool SupportsHash => true;
    public Uri Privacy { get; } = new("https://www.opensubtitles.com/en/privacy");
    public Uri Terms { get; } = new("https://www.opensubtitles.com/en/tos");
    public IReadOnlyDictionary<string, string> Codes => LanguageCodes;

    public string Question(SubtitleLookup lookup)
    {
        var release = FileName.Interpret(lookup.FileName);
        var query = (release.Season is null ? release.Name : release.Title).ToLowerInvariant();
        return JsonSerializer.Serialize(
            new
            {
                query,
                release.Year,
                release.Season,
                release.Episodes,
            }
        );
    }

    public void Authenticate(HttpRequestMessage request, string key) =>
        request.Headers.Add("Api-Key", key);

    // The API redirects a request only to reorder its query, and a download
    // link only to another download link.
    public bool CanRedirect(Uri address, Uri target) =>
        CanDownload(address)
            ? CanDownload(target)
            : target.Host == address.Host && target.AbsolutePath == address.AbsolutePath;

    public Exhaustion? UsedUp(HttpRequestMessage request, ProviderReply reply) =>
        request.RequestUri?.AbsolutePath == "/api/v1/download" ? Quota(reply) : null;

    public async Task Check(SupplierClient client, CancellationToken cancellation)
    {
        var personal = client.Account is Account.SignedIn;
        var reply = await Request(
                client,
                personal ? "/api/v1/infos/user" : "/api/v1/infos/languages",
                null,
                cancellation
            )
            .ConfigureAwait(false);
        using var document = Parse(reply.Bytes);
        if (personal)
        {
            if (
                !document.RootElement.TryGetProperty("data", out var user)
                || user.ValueKind != JsonValueKind.Object
            )
                throw new SubtitleException(SubtitleFailure.Format);
            return;
        }
        var languages = Data(document.RootElement);
        if (
            !languages
                .EnumerateArray()
                .Any(language => Text(language, "language_code") is { Length: > 0 })
        )
            throw new SubtitleException(SubtitleFailure.Format);
    }

    public async IAsyncEnumerable<SubtitleSearch> Search(
        SubtitleQuery query,
        SupplierClient client,
        [EnumeratorCancellation] CancellationToken cancellation
    )
    {
        var release = query.Release;
        var filter = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (query.Title is { } title)
        {
            filter[release.Season is null ? "id" : "parent_feature_id"] = title.TitleId.ToString(
                CultureInfo.InvariantCulture
            );
            if (release.Season is { } season)
                filter["season_number"] = season.ToString(CultureInfo.InvariantCulture);
            if (release.Episodes.Count == 1)
                filter["episode_number"] = release
                    .Episodes[0]
                    .ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            // Titles lists none only for a hash search.
            filter["moviehash"] = query.Hash!;
            filter["moviebytesize"] = query.Size.ToString(CultureInfo.InvariantCulture);
        }
        for (var page = 1; ; page++)
        {
            var parameters = new SortedDictionary<string, string>(filter, StringComparer.Ordinal)
            {
                ["languages"] = query.Code,
                ["order_by"] = "ratings",
                ["order_direction"] = "desc",
            };
            if (page > 1)
                parameters["page"] = page.ToString(CultureInfo.InvariantCulture);
            var reply = await Request(
                    client,
                    "/api/v1/subtitles?" + Query(parameters),
                    null,
                    cancellation
                )
                .ConfigureAwait(false);
            using var document = Parse(reply.Bytes);
            var subtitles = Data(document.RootElement);
            var candidates = new List<SubtitleCandidate>();
            foreach (var subtitle in subtitles.EnumerateArray())
            {
                if (
                    subtitle.ValueKind != JsonValueKind.Object
                    || !subtitle.TryGetProperty("attributes", out var attributes)
                    || attributes.ValueKind != JsonValueKind.Object
                    || Text(attributes, "language") != query.Code
                    || !attributes.TryGetProperty("files", out var files)
                    || files.ValueKind != JsonValueKind.Array
                    || files.GetArrayLength() != 1
                    || Number(files[0], "file_id") is not { } id
                    || id <= 0
                    || Number(attributes, "nb_cd") is { } discs && discs != 1
                    || Text(attributes, "release") is not { } name
                    || !attributes.TryGetProperty("feature_details", out var feature)
                    || feature.ValueKind != JsonValueKind.Object
                )
                    continue;
                // A movie's subtitle has no season or episode.
                var movie = string.Equals(
                    Text(feature, "feature_type"),
                    "Movie",
                    StringComparison.OrdinalIgnoreCase
                );
                candidates.Add(
                    new(
                        query.Language,
                        name,
                        Text(files[0], "file_name") ?? name,
                        id.ToString(CultureInfo.InvariantCulture)
                    )
                    {
                        HashMatch =
                            attributes.TryGetProperty("moviehash_match", out var hashed)
                            && hashed.ValueKind == JsonValueKind.True,
                        Year = (int?)Number(feature, "year"),
                        Season = movie ? null : (int?)Number(feature, "season_number"),
                        Episode = movie ? null : (int?)Number(feature, "episode_number"),
                        // Only an explicit false says that the subtitle is full.
                        Forced =
                            !attributes.TryGetProperty("foreign_parts_only", out var foreign)
                            || foreign.ValueKind != JsonValueKind.False,
                    }
                );
            }
            if (Number(document.RootElement, "total_pages") is not { } pages || pages < 0)
                throw new SubtitleException(SubtitleFailure.Format);
            yield return new(candidates, subtitles.GetArrayLength() == 0);
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
        var reply = await Request(
                client,
                "/api/v1/download",
                new
                {
                    file_id = long.Parse(candidate.Reference, CultureInfo.InvariantCulture),
                    sub_format = "srt",
                },
                cancellation
            )
            .ConfigureAwait(false);
        using var download = Parse(reply.Bytes);
        if (
            !Uri.TryCreate(Text(download.RootElement, "link"), UriKind.Absolute, out var link)
            || !CanDownload(link)
        )
            throw new SubtitleException(SubtitleFailure.Format);
        using var request = new HttpRequestMessage(HttpMethod.Get, link);
        return await client.Send(request, SupplierRequest.Link, cancellation).ConfigureAwait(false);
    }

    // A title search for subtitles matches any title sharing its words, and
    // the API refuses to page past 1,000 results, so a search without a hash
    // lists one feature's subtitles.
    public async Task<IReadOnlyDictionary<long, Listing>?> Titles(
        SubtitleQuery query,
        SupplierClient client,
        CancellationToken cancellation
    )
    {
        if (query.Hash is not null)
            return null;
        var release = query.Release;
        // The API parses a movie's whole file name into the right title, but
        // an episode's file name finds unrelated shows, so it sends the title.
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = (release.Season is null ? release.Name : release.Title).ToLowerInvariant(),
            ["type"] = release.Season is null ? "movie" : "tvshow",
        };
        var reply = await Request(
                client,
                "/api/v1/features?" + Query(parameters),
                null,
                cancellation
            )
            .ConfigureAwait(false);
        using var document = Parse(reply.Bytes);
        // The request names the type, so each listing has that type.
        var kind = release.Season is null ? VideoKind.Movie : VideoKind.Series;
        var listings = new Dictionary<long, Listing>();
        foreach (var feature in Data(document.RootElement).EnumerateArray())
        {
            if (
                !feature.TryGetProperty("attributes", out var attributes)
                || attributes.ValueKind != JsonValueKind.Object
                || !long.TryParse(
                    Text(feature, "id"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var id
                )
                || id <= 0
            )
                continue;
            listings[id] = new(
                kind,
                new[] { Text(attributes, "title"), Text(attributes, "original_title") }
                    .OfType<string>()
                    .ToArray()
            )
            {
                // A show's feature year is not known to be its first year.
                Year =
                    kind == VideoKind.Movie
                    && int.TryParse(
                        Text(attributes, "year"),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var year
                    )
                        ? year
                        : null,
            };
        }
        return listings;
    }

    // The API redirects a request whose parameters are not sorted and lower
    // case, which costs a second request.
    private static string Query(SortedDictionary<string, string> parameters) =>
        string.Join(
            '&',
            parameters.Select(pair =>
                pair.Key
                + "="
                + Uri.EscapeDataString(pair.Value).Replace("%20", "+", StringComparison.Ordinal)
            )
        );

    private static JsonElement Data(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data
            : throw new SubtitleException(SubtitleFailure.Format);

    private static bool CanDownload(Uri uri) =>
        uri is { Scheme: "https", UserInfo: "", IsDefaultPort: true }
        && (uri.Host is "www.opensubtitles.com" or "dl.opensubtitles.com")
        && uri.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal);

    private static Exhaustion? Quota(ProviderReply reply)
    {
        try
        {
            using var document = JsonDocument.Parse(reply.Bytes);
            if (Number(document.RootElement, "remaining") is not <= 0)
                return null;
            if (
                !DateTimeOffset.TryParse(
                    Text(document.RootElement, "reset_time_utc"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var reset
                )
            )
                return new(null);
            return new(
                reset > DateTimeOffset.UtcNow ? reset : DateTimeOffset.UtcNow.AddSeconds(30)
            );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Sends a request to the API with the signed-in account's session,
    // signing in again once when the API refuses the session. Without sign-in
    // a request sends only the API key.
    private async Task<ProviderReply> Request(
        SupplierClient client,
        string path,
        object? body,
        CancellationToken cancellation
    )
    {
        var signedIn = client.Account as Account.SignedIn;
        var session = signedIn is null
            ? null
            : await Login(client, signedIn, cancellation).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(
                body is null ? HttpMethod.Get : HttpMethod.Post,
                new Uri(session?.Origin ?? Origin, path)
            );
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (session is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    session.Token
                );
            if (body is not null)
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json"
                );
            try
            {
                return await client
                    .Send(
                        request,
                        session is not null && attempt == 0
                            ? SupplierRequest.Session
                            : SupplierRequest.Api,
                        cancellation
                    )
                    .ConfigureAwait(false);
            }
            catch (SubtitleException error)
                when (error.Reason == SubtitleFailure.Authentication
                    && signedIn is not null
                    && attempt == 0
                )
            {
                lock (_sync)
                    _sessions.Remove(AccountKey(signedIn));
                session = await Login(client, signedIn, cancellation).ConfigureAwait(false);
            }
        }
    }

    private async Task<Session> Login(
        SupplierClient client,
        Account.SignedIn account,
        CancellationToken cancellation
    )
    {
        var key = AccountKey(account);
        lock (_sync)
            if (_sessions.TryGetValue(key, out var session))
                return session;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(Origin, "/api/v1/login")
        )
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new { username = account.Username, password = account.Password }
                ),
                Encoding.UTF8,
                "application/json"
            ),
        };
        var reply = await client
            .Send(request, SupplierRequest.SignIn, cancellation)
            .ConfigureAwait(false);
        using var document = Parse(reply.Bytes);
        if (Text(document.RootElement, "token") is not { Length: > 0 } token)
            throw new SubtitleException(SubtitleFailure.Authentication);
        var origin = Origin;
        if (Text(document.RootElement, "base_url") is { } host)
        {
            if (host is not "api.opensubtitles.com" and not "vip-api.opensubtitles.com")
                throw new SubtitleException(SubtitleFailure.Format);
            origin = new Uri("https://" + host);
        }
        var signedIn = new Session(token, origin);
        lock (_sync)
            _sessions[key] = signedIn;
        return signedIn;
    }

    private static string AccountKey(Account.SignedIn account)
    {
        var bytes = Encoding.UTF8.GetBytes(
            account.SupplierId + "\n" + account.Username + "\n" + account.Password
        );
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record Session(string Token, Uri Origin)
    {
        public override string ToString() => nameof(Session);
    }
}
