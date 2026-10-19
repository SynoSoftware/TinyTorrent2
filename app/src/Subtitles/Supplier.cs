using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Subtitles.Suppliers;

namespace Syno.TinyTorrent.Subtitles;

/// <summary>
/// The host of one <see cref="ISupplier"/>. It sends the supplier's requests,
/// signs them, accounts for them in the request policy, follows their
/// redirects, decodes subtitle files and decides which subtitles fit a file.
/// </summary>
internal sealed class Supplier(ISupplier supplier, RequestPolicy policy)
{
    internal static IReadOnlyList<Supplier> CreateAll(RequestPolicy policy) =>
        [new(new OpenSubtitles(), policy), new(new SubDL(), policy), new(new SubSource(), policy)];

    internal static IEnumerable<string> AllLanguages =>
        OpenSubtitles
            .LanguageCodes.Keys.Concat(SubDL.LanguageCodes.Keys)
            .Concat(SubSource.LanguageCodes.Keys);

    internal SubtitleSupplier SupplierId => supplier.SupplierId;
    internal bool Available => supplier.Available;
    internal Access Access => supplier.Access;
    internal bool HasBuiltInKey => !string.IsNullOrWhiteSpace(supplier.BuiltInKey);
    internal bool SupportsHash => supplier.SupportsHash;
    internal Uri Privacy => supplier.Privacy;
    internal Uri Terms => supplier.Terms;
    internal IReadOnlyList<string> Languages => supplier.Codes.Keys.ToArray();

    internal bool Supports(string language) => supplier.Codes.ContainsKey(language);

    internal string Question(SubtitleLookup lookup) => supplier.Question(lookup);

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

    /// <summary>The supplier's identity in the shared request policy.</summary>
    internal string ProviderId => SupplierId.ToString().ToLowerInvariant();

    internal ProviderAccess Scope(Account account, string operation) =>
        new(
            ProviderId,
            RequestKey.Digest(Access == Access.Password ? account.Username : ApiKey(account)),
            operation
        );

    /// <summary>The allowance every API request spends: the API key's.</summary>
    internal ProviderAccess QuotaScope(Account account) =>
        new(ProviderId, RequestKey.Digest(ApiKey(account)), string.Empty);

    internal ProviderAccess Authentication(Account account) => Scope(account, string.Empty);

    // The key a request sends: the person's own, or else the one built into
    // the release. A password supplier's secret is a password, so it always
    // sends the built-in key.
    private string ApiKey(Account account) =>
        Access == Access.Key && account.Secret.Length > 0
            ? account.Secret
            : supplier.BuiltInKey ?? string.Empty;

    internal async Task Check(ProviderHttp http, Account account, CancellationToken cancellation)
    {
        var command = new RetryCommand(DateTimeOffset.UtcNow.UtcTicks) { Recheck = true };
        var operation = new Operation(http, account, _ => Task.CompletedTask)
        {
            Purpose = "check",
            Fresh = new(command, RequestKey.Create(Scope(account, "check").ProviderId, "check", [])),
        };
        await supplier.Check(Client(operation), cancellation).ConfigureAwait(false);
        await policy.Succeed(Scope(account, "check")).ConfigureAwait(false);
        await policy.Succeed(Authentication(account)).ConfigureAwait(false);
    }

    internal async Task<byte[]> Download(
        ProviderHttp http,
        Account account,
        SubtitleLookup lookup,
        SubtitleCandidate candidate,
        CancellationToken cancellation
    )
    {
        var operation = new Operation(http, account, lookup.Validate)
        {
            Purpose = "download",
            Fresh = lookup.Fresh,
        };
        var reply = await supplier
            .Download(candidate, Client(operation), cancellation)
            .ConfigureAwait(false);
        var bytes = await Decode(reply, cancellation).ConfigureAwait(false);
        await policy.Succeed(Scope(account, "download")).ConfigureAwait(false);
        return bytes;
    }

    // Lists the subtitles that fit the file, for the person to choose one
    // when no release matches.
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
        if (!supplier.Codes.TryGetValue(lookup.Language, out var code))
            throw new SubtitleException(SubtitleFailure.Unavailable);
        var operation = new Operation(http, account, lookup.Validate) { Fresh = lookup.Fresh };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        SubtitleSearch found;
        try
        {
            found = await supplier
                .Search(
                    new(release, lookup.FileName, lookup.Language, code)
                    {
                        Hash = lookup.Hash,
                        Size = lookup.Size,
                    },
                    Client(operation),
                    deadline.Token
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            var hold = await policy
                .Fail(Scope(account, "search"), new(RequestBlockKind.Backoff))
                .ConfigureAwait(false);
            throw new SubtitleException(SubtitleFailure.Network, hold.RetryAt);
        }
        return found with
        {
            Candidates = found.Candidates.Where(candidate => Fits(release, candidate)).ToArray(),
        };
    }

    private static Task<byte[]> Decode(ProviderReply reply, CancellationToken cancellation)
    {
        var content = reply.Header("Content-Type");
        var charset =
            content is not null && MediaTypeHeaderValue.TryParse(content, out var media)
                ? media.CharSet?.Trim('"')
                : null;
        return SubtitleFile.Decode(reply.Bytes, charset, cancellation);
    }

    private SupplierClient Client(Operation operation) =>
        new(operation.Account, (request, kind, token) => Send(operation, request, kind, token));

    // Sends one of the supplier's requests, and each redirect of it that the
    // supplier allows, after checking that the work is still wanted.
    private async Task<ProviderReply> Send(
        Operation operation,
        HttpRequestMessage request,
        SupplierRequest kind,
        CancellationToken cancellation
    )
    {
        if (kind != SupplierRequest.Link)
        {
            if (!Configured(operation.Account))
                throw new SubtitleException(SubtitleFailure.Unconfigured);
            supplier.Authenticate(request, ApiKey(operation.Account));
        }
        var message = request;
        try
        {
            for (var redirect = 0; ; redirect++)
            {
                cancellation.ThrowIfCancellationRequested();
                await operation.Validate(cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var call = Call(operation, message, kind);
                ProviderReply reply;
                try
                {
                    reply = await operation
                        .Http.Send(
                            call,
                            kind is SupplierRequest.File or SupplierRequest.Link
                                ? ProviderHttp.FileLimit
                                : ProviderHttp.JsonLimit,
                            cancellation
                        )
                        .ConfigureAwait(false);
                    if ((int)reply.Status is < 300 or >= 400)
                    {
                        // An API request's success counts when its operation
                        // completes, because a reply it cannot read fails it.
                        if (kind is SupplierRequest.SignIn or SupplierRequest.Link)
                            await policy.Succeed(call.Request.Access).ConfigureAwait(false);
                        return reply;
                    }
                    // A redirect the supplier does not allow fails like any
                    // other reply that is not a success.
                    if (
                        redirect == 3
                        || !Uri.TryCreate(message.RequestUri, reply.Header("Location"), out var target)
                        || target is not { Scheme: "https", UserInfo: "", IsDefaultPort: true }
                        || !supplier.CanRedirect(message.RequestUri!, target)
                    )
                        throw await ProviderHttp
                            .Fail(call, ReplyFailure.Unavailable, reply.RetryAt)
                            .ConfigureAwait(false);
                    // The redirect carries the request's headers, which hold
                    // the key and the session.
                    var next = new HttpRequestMessage(HttpMethod.Get, target);
                    foreach (var header in message.Headers)
                        next.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    if (message != request)
                        message.Dispose();
                    message = next;
                }
                catch (ReplyException error)
                {
                    throw Failure(error);
                }
            }
        }
        finally
        {
            if (message != request)
                message.Dispose();
        }
    }

    private ProviderCall Call(Operation operation, HttpRequestMessage message, SupplierRequest kind)
    {
        var account = operation.Account;
        var link = kind == SupplierRequest.Link;
        var download = operation.Purpose == "download";
        var scope = link
            ? new ProviderAccess(ProviderId, RequestKey.Digest(message.RequestUri!.Host), "file")
            : Scope(account, kind == SupplierRequest.SignIn ? "authentication" : operation.Purpose);
        return new ProviderCall(
            message,
            policy,
            new(scope)
            {
                Windows = link ? [] : supplier.RateWindows,
                Authentication = link ? null : Authentication(account),
                Allowance = link ? null : QuotaScope(account),
            }
        )
        {
            Fresh = operation.Fresh,
            CanRedirect = message.Method == HttpMethod.Get,
            Renewable = kind == SupplierRequest.Session,
            NamesResource = download,
            // A 402 says that an allowance is used up, as a supplier's own
            // download reset does.
            Quota = reply =>
            {
                var reset = link ? null : supplier.DownloadReset(message, reply);
                return reset is null && reply.Status != HttpStatusCode.PaymentRequired
                    ? null
                    : new Quota(
                        download ? Scope(account, "download") : scope,
                        reset ?? reply.RetryAt
                    );
            },
        };
    }

    // The failure's retry time is the hold's. A missing download reference is
    // Format, which Acquisition holds for that reference alone.
    private static SubtitleException Failure(ReplyException error) =>
        new(
            error.Failure switch
            {
                ReplyFailure.Refused or ReplyFailure.Expired => SubtitleFailure.Authentication,
                ReplyFailure.Wait => SubtitleFailure.Quota,
                ReplyFailure.Missing => SubtitleFailure.Format,
                ReplyFailure.Unavailable or ReplyFailure.Invalid => SubtitleFailure.Network,
                ReplyFailure.Route => SubtitleFailure.Route,
                _ => throw new ArgumentOutOfRangeException(nameof(error)),
            },
            error.Hold?.RetryAt
        );

    private sealed record Operation(
        ProviderHttp Http,
        Account Account,
        Func<CancellationToken, Task> Validate
    )
    {
        internal string Purpose { get; init; } = "search";
        internal FreshRequest? Fresh { get; init; }
    }

    // Whether a subtitle can serve the file. A supplier reports a season
    // exactly for an episode's subtitle, so the season also tells an
    // episode's subtitle from a movie's. An unreported episode does not count
    // against the subtitle.
    private static bool Fits(Release release, SubtitleCandidate candidate) =>
        !candidate.Forced
        && candidate.Season == release.Season
        && SameYear(release, candidate.Year)
        && (candidate.Episode is not { } episode || release.Episodes.Contains(episode));

    /// <summary>
    /// Chooses, by the supplier's own key, the listing that names the file:
    /// the one listing that <see cref="Matches"/> it, or else the one whose
    /// title holds the file's title words, as "Death on the Brandywine" holds
    /// a file's "Brandywine". Null when none qualifies or several do.
    /// </summary>
    internal static long? Choose(Release release, IReadOnlyDictionary<long, Listing> listings)
    {
        var exact = listings.Where(pair => Matches(release, pair.Value)).ToArray();
        if (exact.Length > 0)
            return exact.Length == 1 ? exact[0].Key : null;
        var near = listings
            .Where(pair =>
                Agrees(release, pair.Value)
                && pair.Value.Titles.Any(title => Within(release, title))
            )
            .ToArray();
        return near.Length == 1 ? near[0].Key : null;
    }

    /// <summary>
    /// Whether the listing is the file's title, by its kind, season, year and
    /// title words.
    /// </summary>
    internal static bool Matches(Release release, Listing listing) =>
        Agrees(release, listing)
        && listing.Titles.Any(title =>
            string.Equals(TitleWords(title), TitleWords(release.Title), StringComparison.Ordinal)
        );

    private static bool Agrees(Release release, Listing listing) =>
        listing.Kind == (release.Season is null ? VideoKind.Movie : VideoKind.Series)
        && (listing.Season is null || listing.Season == release.Season)
        && (listing.Episode is not { } episode || release.Episodes.Contains(episode))
        && SameYear(release, listing.Year);

    // A file name carries a year to tell same-named titles apart, such as the
    // two Doctor Who shows, so a reported year must be the file's. An
    // unreported year does not count against the match.
    private static bool SameYear(Release release, int? year) =>
        release.Year is null || year is null || year == release.Year;

    // A file name spells "U.N.C.L.E." as "U N C L E", so titles compare by
    // their words alone.
    private static string TitleWords(string title) =>
        string.Join(' ', FileName.Words(FileName.Normalize(title)));

    private static bool Within(Release release, string title) =>
        (" " + TitleWords(title) + " ").Contains(
            " " + TitleWords(release.Title) + " ",
            StringComparison.Ordinal
        );

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
        var file = FileName.Words(FileName.Normalize(release.Name)).ToHashSet();
        var other = FileName.Words(FileName.Normalize(name)).ToHashSet();
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

    internal static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    internal static long? Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.Number
        && field.TryGetInt64(out var number)
            ? number
            : null;

    internal static JsonDocument Parse(byte[] bytes)
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

    // What the supplier reports about the subtitle. The host matches these
    // before an answer is saved, so they are not public and a saved answer
    // keeps none of them.

    /// <summary>
    /// The year of the subtitle's title: the movie's, or the show's first. A
    /// supplier whose year means something else for a show reports none.
    /// </summary>
    internal int? Year { get; init; }
    internal int? Season { get; init; }
    internal int? Episode { get; init; }
    internal bool Forced { get; init; }
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
