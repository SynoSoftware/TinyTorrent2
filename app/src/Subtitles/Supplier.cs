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

internal abstract class Supplier(RequestPolicy policy)
{
    protected readonly object _sync = new();

    internal static IReadOnlyList<Supplier> CreateAll(RequestPolicy policy) =>
        [new OpenSubtitles(policy), new SubDL(policy), new SubSource(policy)];

    internal static IEnumerable<string> AllLanguages =>
        OpenSubtitles
            .LanguageCodes.Keys.Concat(SubDL.LanguageCodes.Keys)
            .Concat(SubSource.LanguageCodes.Keys);

    internal abstract SubtitleSupplier SupplierId { get; }
    internal virtual bool Available => true;
    internal virtual Access Access => Access.Key;

    /// <summary>
    /// The API key built into the release, which an API-key supplier sends
    /// when the account holds no key of the person's own.
    /// </summary>
    protected virtual string? BuiltInKey => null;
    internal bool HasBuiltInKey => !string.IsNullOrWhiteSpace(BuiltInKey);
    internal virtual bool SupportsHash => false;
    internal abstract Uri Privacy { get; }
    internal abstract Uri Terms { get; }
    protected abstract IReadOnlyDictionary<string, string> Codes { get; }
    internal IReadOnlyList<string> Languages => Codes.Keys.ToArray();

    internal bool Supports(string language) => Codes.ContainsKey(language);

    internal virtual string Question(SubtitleLookup lookup) => lookup.FileName;

    internal bool Configured(Account account) =>
        Available
        && (
            Access == Access.Password
                ? (account.Username.Length == 0) == (account.Secret.Length == 0)
                : account.Secret.Length > 0 || HasBuiltInKey
        );

    internal Account Interpret(Draft draft, string secret)
    {
        var password = Access == Access.Password;
        var username = password ? draft.Username.Trim() : string.Empty;
        if (password && username.Length == 0 && draft.Secret.Length == 0)
            secret = string.Empty;
        if (
            password
                ? (username.Length > 0) != (secret.Length > 0)
                : secret.Length == 0 && !HasBuiltInKey
        )
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        return new(SupplierId, username, secret);
    }

    internal async Task<Allowance> Allowance(Account account)
    {
        var search = await policy
            .Read([QuotaScope(account), Scope(account, "search")], CancellationToken.None)
            .ConfigureAwait(false);
        var download = await policy
            .Read(Scope(account, "download"), CancellationToken.None)
            .ConfigureAwait(false);
        return new(Reset(search), Reset(download));

        static DateTimeOffset? Reset(RequestBlock? block) =>
            block is { Kind: RequestBlockKind.RemoteWait or RequestBlockKind.Allowance }
                ? block.RetryAt
                : null;
    }

    internal ProviderAccess Scope(Account account, string operation) =>
        new(
            SupplierId.ToString().ToLowerInvariant(),
            RequestKey.Digest(Access == Access.Password ? account.Username : ApiKey(account)),
            operation
        );

    internal virtual ProviderAccess QuotaScope(Account account) => Scope(account, string.Empty);

    internal async Task Check(ProviderHttp http, Account account, CancellationToken cancellation)
    {
        var command = new RetryCommand(DateTimeOffset.UtcNow.UtcTicks) { Recheck = true };
        await Check(
                new(http, account, _ => Task.CompletedTask)
                {
                    Purpose = "check",
                    Fresh = new(
                        command,
                        RequestKey.Create(Scope(account, "check").ProviderId, "check", [])
                    ),
                },
                cancellation
            )
            .ConfigureAwait(false);
        await policy.Succeed(Scope(account, "check")).ConfigureAwait(false);
        await policy.Succeed(Authentication(account)).ConfigureAwait(false);
    }

    internal ProviderAccess Authentication(Account account) => Scope(account, string.Empty);

    protected Task Authenticated(Account account) =>
        policy.Succeed(Scope(account, "authentication"));

    protected abstract Task Check(Operation operation, CancellationToken cancellation);

    // Lists the full SRT subtitles in the supplier's language code for the
    // title the lookup names, or, with a hash, for that exact file.
    protected abstract Task<SubtitleSearch> Search(
        Operation operation,
        SubtitleLookup lookup,
        Release release,
        string code,
        CancellationToken cancellation
    );

    internal async Task<byte[]> Download(
        ProviderHttp http,
        Account account,
        SubtitleLookup lookup,
        SubtitleCandidate candidate,
        CancellationToken cancellation
    )
    {
        var bytes = await Download(
                new(http, account, lookup.Validate) { Purpose = "download", Fresh = lookup.Fresh },
                candidate,
                cancellation
            )
            .ConfigureAwait(false);
        await policy.Succeed(Scope(account, "download")).ConfigureAwait(false);
        return bytes;
    }

    protected abstract Task<byte[]> Download(
        Operation operation,
        SubtitleCandidate candidate,
        CancellationToken cancellation
    );
    protected abstract void Authenticate(HttpRequestMessage request, Account account);

    // The person's own key replaces the built-in one.
    protected string ApiKey(Account account) =>
        account.Secret.Length > 0 ? account.Secret : BuiltInKey ?? string.Empty;

    protected virtual IReadOnlyList<RateWindow> RateWindows => [];

    protected virtual DateTimeOffset? DownloadReset(
        HttpRequestMessage request,
        ProviderReply response
    ) => null;

    protected virtual bool CanRedirect(HttpRequestMessage request) => false;

    // Lists every subtitle for the file's title, for the person to choose
    // one when no release matches.
    internal async Task<SubtitleSearch> Candidates(
        ProviderHttp http,
        Account account,
        SubtitleLookup lookup,
        CancellationToken cancellation
    )
    {
        if (!Available)
            throw new SubtitleException(SubtitleFailure.Unavailable);
        var release = FileName.Interpret(lookup.FileName);
        if (release.Title.Length == 0 || release.IsExtra)
            return new([]);
        if (!Codes.TryGetValue(lookup.Language, out var code))
            throw new SubtitleException(SubtitleFailure.Unavailable);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            return await Search(
                    new(http, account, lookup.Validate) { Fresh = lookup.Fresh },
                    lookup,
                    release,
                    code,
                    deadline.Token
                )
                .ConfigureAwait(false);
        }
        catch (Exception error)
            when (error is OperationCanceledException
                    && deadline.IsCancellationRequested
                    && !cancellation.IsCancellationRequested
                || error is SubtitleException { Reason: SubtitleFailure.Network, RetryAt: null }
            )
        {
            var hold = await policy
                .Fail(Scope(account, "search"), new(RequestBlockKind.Backoff))
                .ConfigureAwait(false);
            throw new SubtitleException(SubtitleFailure.Network, hold.RetryAt);
        }
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
        Operation operation,
        Uri uri,
        CancellationToken cancellation,
        int maximum = ProviderHttp.JsonLimit
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await Send(operation, request, cancellation, maximum).ConfigureAwait(false);
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
        Operation operation,
        HttpRequestMessage request,
        CancellationToken cancellation,
        int maximum = ProviderHttp.JsonLimit,
        bool authenticate = true,
        bool canRenew = false
    )
    {
        cancellation.ThrowIfCancellationRequested();
        await operation.Validate(cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var account = operation.Account;
        if (authenticate && !Configured(account))
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        if (authenticate)
            Authenticate(request, account);
        var scope = Scope(
            account,
            request.RequestUri?.AbsolutePath.EndsWith("/login", StringComparison.Ordinal) == true
                ? "authentication"
                : operation.Purpose
        );
        if (!authenticate)
            scope = scope with
            {
                Identity = RequestKey.Digest(request.RequestUri!.Host),
                Operation = "file",
            };
        var call = new ProviderCall(
            request,
            policy,
            new(scope)
            {
                Windows = authenticate ? RateWindows : [],
                Authentication = authenticate ? Authentication(account) : null,
                Allowance = authenticate ? QuotaScope(account) : null,
            }
        )
        {
            Fresh = operation.Fresh,
            Observe = Observe,
        };
        async Task Observe(ProviderReply response)
        {
            var retry = Retry(response);
            if (
                response.Header("X-RateLimit-Remaining") == "0"
                || response.Status == HttpStatusCode.TooManyRequests
            )
            {
                await policy
                    .Fail(
                        authenticate ? QuotaScope(account) : scope,
                        new(RequestBlockKind.RemoteWait, retry)
                    )
                    .ConfigureAwait(false);
            }
            if (response.Status == HttpStatusCode.PaymentRequired && retry is not null)
                await policy
                    .Fail(
                        operation.Purpose == "download" ? Scope(account, "download") : scope,
                        new(RequestBlockKind.RemoteWait, retry)
                    )
                    .ConfigureAwait(false);
            if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                if (!authenticate)
                {
                    var hold = await policy
                        .Fail(scope, new(RequestBlockKind.Backoff, retry))
                        .ConfigureAwait(false);
                    throw new SubtitleException(SubtitleFailure.Network, hold.RetryAt);
                }
                if (!canRenew)
                    await policy
                        .Fail(Authentication(account), new(RequestBlockKind.Attention))
                        .ConfigureAwait(false);
                throw new SubtitleException(SubtitleFailure.Authentication);
            }
            if (response.Status == HttpStatusCode.TooManyRequests)
                throw new SubtitleException(
                    SubtitleFailure.Quota,
                    retry ?? DateTimeOffset.UtcNow.AddHours(1)
                );
            if ((int)response.Status >= 500)
            {
                var hold = await policy
                    .Fail(scope, new(RequestBlockKind.Backoff, retry))
                    .ConfigureAwait(false);
                throw new SubtitleException(SubtitleFailure.Network, hold.RetryAt);
            }
        }
        try
        {
            var response = await operation
                .Http.Send(call, maximum, cancellation)
                .ConfigureAwait(false);
            var reset = authenticate ? DownloadReset(request, response) : null;
            if (reset is not null || response.Status == HttpStatusCode.PaymentRequired)
            {
                var hold = await policy
                    .Fail(
                        operation.Purpose == "download" ? Scope(account, "download") : scope,
                        new(RequestBlockKind.RemoteWait, reset ?? Retry(response))
                    )
                    .ConfigureAwait(false);
                if ((int)response.Status >= 400)
                    throw new SubtitleException(SubtitleFailure.Quota, hold.RetryAt);
            }
            if ((int)response.Status is >= 300 and < 400 && (!authenticate || CanRedirect(request)))
                return response;
            if ((int)response.Status is < 200 or >= 300)
                throw new SubtitleException(SubtitleFailure.Format);
            if (!authenticate)
                await policy.Succeed(scope).ConfigureAwait(false);
            return response;
        }
        catch (HttpRouteException)
        {
            throw new SubtitleException(SubtitleFailure.Route);
        }
        catch (HttpRequestException error)
        {
            var hold = await policy.Read(scope, CancellationToken.None).ConfigureAwait(false);
            throw new SubtitleException(
                error.GetBaseException() is InvalidOperationException
                    ? SubtitleFailure.Route
                    : SubtitleFailure.Network,
                hold?.RetryAt
            );
        }
        catch (InvalidDataException)
        {
            throw new SubtitleException(SubtitleFailure.Format);
        }
        catch (IOException)
        {
            var hold =
                await policy.Read(scope, CancellationToken.None).ConfigureAwait(false)
                ?? await policy.Fail(scope, new(RequestBlockKind.Backoff)).ConfigureAwait(false);
            throw new SubtitleException(SubtitleFailure.Network, hold.RetryAt);
        }
        catch (InvalidOperationException)
        {
            throw new SubtitleException(SubtitleFailure.Route);
        }
    }

    protected sealed record Operation(
        ProviderHttp Http,
        Account Account,
        Func<CancellationToken, Task> Validate
    )
    {
        internal string Purpose { get; init; } = "search";
        internal FreshRequest? Fresh { get; init; }
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
    internal static int Likeness(Release release, string name)
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

    internal static bool SameRelease(Release release, string name)
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
    internal FreshRequest? Fresh { get; init; }
    internal SubtitleStage Stage { get; init; }
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
    public bool HashMatch { get; init; }
    public bool Exact { get; init; }
}

internal sealed record SubtitleSearch(
    IReadOnlyList<SubtitleCandidate> Candidates,
    bool Empty = false
);

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
