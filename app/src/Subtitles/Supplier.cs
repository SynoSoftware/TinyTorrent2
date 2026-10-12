using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal abstract class Supplier
{
    private readonly Dictionary<string, DateTimeOffset> _waits = [];
    protected readonly Dictionary<string, DateTimeOffset> _downloads = [];
    protected readonly object _sync = new();

    internal static IReadOnlyList<Supplier> CreateAll() =>
        [new OpenSubtitles(), new SubDL(), new SubSource()];

    internal abstract SubtitleSupplier SupplierId { get; }
    internal virtual bool Available => true;
    internal virtual bool UsesPassword => false;
    internal virtual bool SupportsHash => false;
    internal abstract Uri Privacy { get; }
    internal abstract Uri Terms { get; }
    protected abstract IReadOnlyDictionary<string, string> Codes { get; }
    internal IReadOnlyList<string> Languages => Codes.Keys.ToArray();

    internal bool Supports(string language) => Codes.ContainsKey(language);

    internal bool Configured(Account account) =>
        Available
        && (
            UsesPassword
                ? (account.Username.Length == 0) == (account.Secret.Length == 0)
                : account.Secret.Length > 0
        );

    internal Account Interpret(Draft draft, string secret)
    {
        var username = UsesPassword ? draft.Username.Trim() : string.Empty;
        if (UsesPassword && username.Length == 0 && draft.Secret.Length == 0)
            secret = string.Empty;
        if (UsesPassword ? (username.Length > 0) != (secret.Length > 0) : secret.Length == 0)
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        return new(SupplierId, username, secret);
    }

    internal Allowance Allowance(Account account)
    {
        var key = AccountKey(account);
        lock (_sync)
        {
            return new Allowance(
                _waits.TryGetValue(key, out var rate) ? rate : null,
                _downloads.TryGetValue(key, out var download) ? download : null
            );
        }
    }

    internal void Restore(Account account, Allowance allowance)
    {
        var key = AccountKey(account);
        if (allowance.RequestReset is { } rate)
            Wait(key, rate);
        if (allowance.DownloadReset is { } download)
            lock (_sync)
                if (!_downloads.TryGetValue(key, out var current) || download > current)
                    _downloads[key] = download;
    }

    internal abstract Task Check(
        ProviderHttp http,
        Account account,
        CancellationToken cancellation
    );

    // Lists the full SRT subtitles in the supplier's language code for the
    // title the lookup names, or, with a hash, for that exact file.
    protected abstract Task<IReadOnlyList<SubtitleCandidate>> Search(
        ProviderHttp http,
        Account account,
        SubtitleLookup lookup,
        Release release,
        string code,
        CancellationToken cancellation
    );
    internal abstract Task<byte[]> Download(
        ProviderHttp http,
        Account account,
        SubtitleLookup lookup,
        SubtitleCandidate candidate,
        CancellationToken cancellation
    );
    protected abstract void Authenticate(HttpRequestMessage request, Account account);

    protected virtual void Reserve(string key) { }

    protected virtual DateTimeOffset? DownloadReset(
        HttpRequestMessage request,
        ProviderReply response
    ) => null;

    protected virtual bool CanRedirect(HttpRequestMessage request) => false;

    internal Task<IReadOnlyList<SubtitleCandidate>> Match(
        ProviderHttp http,
        Account account,
        SubtitleLookup lookup,
        CancellationToken cancellation
    ) =>
        Timed(
            token => Match(http, account, lookup, FileName.Interpret(lookup.FileName), token),
            cancellation
        );

    private async Task<IReadOnlyList<SubtitleCandidate>> Match(
        ProviderHttp http,
        Account account,
        SubtitleLookup lookup,
        Release release,
        CancellationToken cancellation
    )
    {
        var candidates = await Find(http, account, lookup, release, cancellation)
            .ConfigureAwait(false);
        var exact = candidates.FirstOrDefault(candidate =>
            candidate.HashMatch || SameRelease(release, candidate.Release)
        );
        if (exact is null && lookup.Hash is not null)
        {
            // A hash search lists that file's subtitles; a guess needs the title's.
            candidates = await Find(
                    http,
                    account,
                    lookup with
                    {
                        Hash = null,
                    },
                    release,
                    cancellation
                )
                .ConfigureAwait(false);
            exact = candidates.FirstOrDefault(candidate => SameRelease(release, candidate.Release));
        }
        if (exact is not null)
            return [exact with { Exact = true }];
        if (SupportsHash && lookup.Hash is null)
            // The hash search after the download completes can still find the
            // exact file, so guessing waits for it.
            return [];
        return candidates
            .OrderByDescending(candidate => Likeness(release, candidate.Release))
            .DistinctBy(candidate => FileName.Normalize(candidate.Release))
            .Take(lookup.Guesses)
            .ToArray();
    }

    // Lists every subtitle for the file's title, for the person to choose
    // one when no release matches.
    internal Task<IReadOnlyList<SubtitleCandidate>> Candidates(
        ProviderHttp http,
        Account account,
        SubtitleLookup lookup,
        CancellationToken cancellation
    ) =>
        Timed(
            token => Find(http, account, lookup, FileName.Interpret(lookup.FileName), token),
            cancellation
        );

    // A search that takes longer than a minute fails as a network failure,
    // so callers report it like any other supplier they could not reach.
    private static async Task<IReadOnlyList<SubtitleCandidate>> Timed(
        Func<CancellationToken, Task<IReadOnlyList<SubtitleCandidate>>> search,
        CancellationToken cancellation
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            return await search(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            throw new SubtitleException(SubtitleFailure.Network);
        }
    }

    private async Task<IReadOnlyList<SubtitleCandidate>> Find(
        ProviderHttp http,
        Account account,
        SubtitleLookup lookup,
        Release release,
        CancellationToken cancellation
    )
    {
        if (!Available)
            throw new SubtitleException(SubtitleFailure.Unavailable);
        await lookup.Validate(cancellation).ConfigureAwait(false);
        if (release.Title.Length == 0 || release.IsExtra)
            return [];
        if (!Codes.TryGetValue(lookup.Language, out var code))
            throw new SubtitleException(SubtitleFailure.Unavailable);
        return await Search(http, account, lookup, release, code, cancellation)
            .ConfigureAwait(false);
    }

    protected static Task<byte[]> Decode(ProviderReply response, CancellationToken cancellation)
    {
        var content = response.Header("Content-Type");
        var charset =
            content is not null && MediaTypeHeaderValue.TryParse(content, out var media)
                ? media.CharSet?.Trim('"')
                : null;
        return SubtitleFile.Decode(response.Bytes, charset, cancellation);
    }

    protected async Task<ProviderReply> Get(
        ProviderHttp http,
        Account account,
        Uri uri,
        CancellationToken cancellation,
        int maximum = ProviderHttp.JsonLimit
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await Send(http, account, request, cancellation, maximum).ConfigureAwait(false);
    }

    protected static string AccountKey(Account account)
    {
        var bytes = Encoding.UTF8.GetBytes(
            account.SupplierId + "\n" + account.Username + "\n" + account.Secret
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

    protected async Task<ProviderReply> Send(
        ProviderHttp http,
        Account account,
        HttpRequestMessage request,
        CancellationToken cancellation,
        int maximum = ProviderHttp.JsonLimit,
        bool authenticate = true
    )
    {
        if (authenticate && !Configured(account))
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        var key = AccountKey(account);
        if (authenticate)
        {
            lock (_sync)
            {
                if (_waits.TryGetValue(key, out var wait) && wait > DateTimeOffset.UtcNow)
                    throw new SubtitleException(SubtitleFailure.Quota, wait);
                Reserve(key);
            }
            Authenticate(request, account);
        }
        try
        {
            var response = await http.Send(request, maximum, cancellation).ConfigureAwait(false);
            var retry = Retry(response);
            var downloadReset = authenticate ? DownloadReset(request, response) : null;
            if (downloadReset is { } reset)
            {
                lock (_sync)
                    _downloads[key] = reset;
            }
            if (
                response.Header("X-RateLimit-Remaining") == "0"
                || response.Status == HttpStatusCode.TooManyRequests
                || response.Status == HttpStatusCode.PaymentRequired && downloadReset is null
            )
            {
                Wait(key, retry ?? DateTimeOffset.UtcNow.AddHours(1));
            }
            if (downloadReset is { } allowance && (int)response.Status >= 400)
                throw new SubtitleException(SubtitleFailure.Quota, allowance);
            if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SubtitleException(
                    authenticate ? SubtitleFailure.Authentication : SubtitleFailure.Network
                );
            if (response.Status is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired)
                throw new SubtitleException(
                    SubtitleFailure.Quota,
                    retry ?? DateTimeOffset.UtcNow.AddHours(1)
                );
            if ((int)response.Status >= 500)
                throw new SubtitleException(SubtitleFailure.Network, retry);
            if ((int)response.Status is >= 300 and < 400 && (!authenticate || CanRedirect(request)))
                return response;
            if ((int)response.Status is < 200 or >= 300)
                throw new SubtitleException(SubtitleFailure.Format);
            return response;
        }
        catch (HttpRouteException)
        {
            throw new SubtitleException(SubtitleFailure.Route);
        }
        catch (HttpRequestException error)
        {
            throw new SubtitleException(
                error.GetBaseException() is InvalidOperationException
                    ? SubtitleFailure.Route
                    : SubtitleFailure.Network
            );
        }
        catch (InvalidDataException)
        {
            throw new SubtitleException(SubtitleFailure.Format);
        }
        catch (IOException)
        {
            throw new SubtitleException(SubtitleFailure.Network);
        }
        catch (InvalidOperationException)
        {
            throw new SubtitleException(SubtitleFailure.Route);
        }
    }

    protected void Wait(string key, DateTimeOffset reset)
    {
        lock (_sync)
            if (!_waits.TryGetValue(key, out var current) || reset > current)
                _waits[key] = reset;
    }

    protected static bool Matches(
        Release release,
        string? title = null,
        long? year = null,
        long? season = null,
        long? episode = null
    ) =>
        (
            title is null
            || string.Equals(TitleWords(title), TitleWords(release.Title), StringComparison.Ordinal)
        )
        && (release.Year is null || year is null || release.Year == year)
        && (season is null || season == release.Season)
        && (episode is null || release.Episodes.Any(value => value == episode));

    // A file name spells "U.N.C.L.E." as "U N C L E", so titles compare by
    // their words alone.
    private static string TitleWords(string title) => string.Join(' ', Words(title));

    protected static bool Within(Release release, string title) =>
        (" " + TitleWords(title) + " ").Contains(
            " " + TitleWords(release.Title) + " ",
            StringComparison.Ordinal
        );

    private static string[] Words(string text) =>
        new string(
            FileName
                .Normalize(text)
                .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
                .ToArray()
        ).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static readonly string[] Editions =
    [
        "extended",
        "director",
        "directors",
        "unrated",
        "uncut",
        "imax",
        "theatrical",
        "remastered",
    ];
    internal static readonly string[] HearingImpaired = ["sdh", "hi"];

    // A subtitle made for another release of the title fits best when it
    // shares the edition, which decides the cut, then the source and release
    // tags, which decide the timing.
    private static int Likeness(Release release, string name)
    {
        var file = Words(release.Name).ToHashSet();
        var other = Words(name).ToHashSet();
        var score = file.Count(other.Contains);
        if (Source(file) is { } source && Source(other) is { } candidate)
            score += source == candidate ? 4 : -4;
        if (!file.Where(Editions.Contains).ToHashSet().SetEquals(other.Where(Editions.Contains)))
            score -= 8;
        // Hearing-impaired subtitles add sound descriptions most people
        // don't want, so they lose ties.
        if (other.Overlaps(HearingImpaired) && !file.Overlaps(HearingImpaired))
            score -= 1;
        return score;
    }

    private static string? Source(HashSet<string> words) =>
        words.Overlaps(["bluray", "blu", "bdrip", "brrip", "bdremux", "remux"]) ? "bluray"
        : words.Overlaps(["web", "webdl", "webrip"]) ? "web"
        : words.Contains("hdtv") ? "hdtv"
        : words.Overlaps(["dvd", "dvdrip"]) ? "dvd"
        : null;

    protected static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    protected static long? Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.Number
        && field.TryGetInt64(out var number)
            ? number
            : null;

    protected static bool SameRelease(Release release, string name)
    {
        var candidate = FileName.Interpret(
            FileName.Kind(name) == FileKind.Video ? name : name + ".mkv"
        );
        return release.Season == candidate.Season
            && release.Episodes.SequenceEqual(candidate.Episodes)
            && release.Year == candidate.Year
            && string.Equals(
                FileName.Normalize(release.Name),
                FileName.Normalize(candidate.Name),
                StringComparison.Ordinal
            );
    }

    protected static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;
            document.Dispose();
            throw new SubtitleException(SubtitleFailure.Format);
        }
        catch (JsonException)
        {
            throw new SubtitleException(SubtitleFailure.Format);
        }
    }

    protected static DateTimeOffset? Retry(ProviderReply response)
    {
        var now = DateTimeOffset.UtcNow;
        if (response.RetryAt is { } retry)
            return retry;
        var value = response.Header("X-RateLimit-Reset");
        if (
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
            && reset is > 0 and < 253402300800
        )
        {
            var deadline = DateTimeOffset.FromUnixTimeSeconds(reset);
            return deadline > now ? deadline : null;
        }
        if (
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var resetDate
            )
        )
            return resetDate > now ? resetDate : null;
        return null;
    }
}

internal sealed record SubtitleLookup(
    string FileName,
    string Language,
    Func<CancellationToken, Task> Validate
)
{
    internal string? Hash { get; init; }
    internal long Size { get; init; }
    internal int Guesses { get; init; } = 2;
}

// One subtitle a supplier offers. Reference is the supplier's own handle for
// downloading it; Label is what the person sees when choosing.
internal sealed record SubtitleCandidate(
    string Language,
    string Release,
    string Label,
    string Reference
)
{
    internal bool HashMatch { get; init; }
    internal bool Exact { get; init; }
}

internal sealed record Allowance(
    DateTimeOffset? RequestReset = null,
    DateTimeOffset? DownloadReset = null
)
{
    internal DateTimeOffset? RetryAt =>
        RequestReset is { } request && (DownloadReset is null || request > DownloadReset)
            ? request
            : DownloadReset;
}
