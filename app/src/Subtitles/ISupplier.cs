using System.Net.Http;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Subtitles;

/// <summary>
/// A source of subtitles. The host <see cref="Supplier"/> owns sending,
/// request protection, sign-in accounting, redirects, decoding and matching;
/// a supplier only builds its requests and reads their answers into facts.
/// </summary>
/// <remarks>
/// A supplier reports a failure as a <see cref="SubtitleException"/>, and an
/// answer it cannot read as <see cref="SubtitleFailure.Format"/>.
/// </remarks>
internal interface ISupplier
{
    SubtitleSupplier SupplierId { get; }

    /// <summary>Whether this release can use the supplier at all.</summary>
    bool Available => true;

    Access Access => Access.Key;

    /// <summary>
    /// The API key built into the release. A key supplier sends it when the
    /// account holds no key of the person's own; a password supplier always
    /// sends it.
    /// </summary>
    string? BuiltInKey => null;

    bool SupportsHash => false;
    Uri Privacy { get; }
    Uri Terms { get; }

    /// <summary>The supplier's code for each language tag it serves.</summary>
    IReadOnlyDictionary<string, string> Codes { get; }

    /// <summary>The supplier's request limits, which the host applies to every API request.</summary>
    IReadOnlyList<RateWindow> RateWindows => [];

    /// <summary>
    /// What a search for the lookup asks the supplier. Lookups that ask the
    /// same share one saved answer.
    /// </summary>
    string Question(SubtitleLookup lookup) => lookup.FileName;

    /// <summary>Adds the API key to a request to the supplier's API.</summary>
    void Authenticate(HttpRequestMessage request, string key);

    /// <summary>
    /// Whether the host follows a redirect of a GET request from
    /// <paramref name="address"/> to <paramref name="target"/>, which is
    /// already HTTPS without credentials.
    /// </summary>
    bool CanRedirect(Uri address, Uri target) => false;

    /// <summary>
    /// The account's used-up downloads, if a reply to the request says in the
    /// supplier's own way that they are used up; otherwise null.
    /// </summary>
    Exhaustion? UsedUp(HttpRequestMessage request, ProviderReply reply) => null;

    /// <summary>Proves that the supplier accepts the client's account.</summary>
    Task Check(SupplierClient client, CancellationToken cancellation);

    /// <summary>
    /// Lists, by the supplier's own key, the titles in its catalogue that the
    /// query's title may name, when the supplier lists subtitles by title.
    /// The host chooses the file's title among them for
    /// <see cref="SubtitleQuery.Title"/>. Null when the supplier lists the
    /// subtitles for the file itself.
    /// </summary>
    Task<IReadOnlyDictionary<long, Listing>?> Titles(
        SubtitleQuery query,
        SupplierClient client,
        CancellationToken cancellation
    ) => Task.FromResult<IReadOnlyDictionary<long, Listing>?>(null);

    /// <summary>
    /// Lists the full SRT subtitles in the query's language for the title the
    /// host chose, or else for the file, by its name or hash, with what the
    /// supplier reports about each, one page per reply. The host decides
    /// which fit the file, and reads no further page after one whose
    /// subtitles all report a title that is not the file's.
    /// </summary>
    IAsyncEnumerable<SubtitleSearch> Search(
        SubtitleQuery query,
        SupplierClient client,
        CancellationToken cancellation
    );

    /// <summary>Fetches the candidate's subtitle file, which the host decodes.</summary>
    Task<ProviderReply> Download(
        SubtitleCandidate candidate,
        SupplierClient client,
        CancellationToken cancellation
    );
}

/// <summary>
/// Sends one of a supplier's requests through the host, which checks that
/// the work is still wanted, signs the request as <paramref name="kind"/>
/// says, applies request protection and the saved network route, and follows
/// the redirects <see cref="ISupplier.CanRedirect"/> allows, up to the host's
/// limit.
/// The reply is a success.
/// </summary>
/// <exception cref="SubtitleException">The request failed; its retry time is the hold's.</exception>
internal delegate Task<ProviderReply> SupplierSend(
    HttpRequestMessage request,
    SupplierRequest kind,
    CancellationToken cancellation
);

/// <summary>The account a supplier operation serves, and the host's way to send its requests.</summary>
internal sealed record SupplierClient(Account Account, SupplierSend Send)
{
    internal async Task<ProviderReply> Get(Uri address, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        return await Send(request, SupplierRequest.Api, cancellation).ConfigureAwait(false);
    }
}

/// <summary>
/// What a search asks a supplier: the file's release and name, and the
/// language as a tag and as the supplier's code.
/// </summary>
internal sealed record SubtitleQuery(Release Release, string FileName, string Language, string Code)
{
    internal string? Hash { get; init; }
    internal long Size { get; init; }

    /// <summary>The title the host chose from <see cref="ISupplier.Titles"/>.</summary>
    internal CatalogueTitle? Title { get; init; }
}

/// <summary>A title in a supplier's catalogue, with the supplier's key for it.</summary>
internal sealed record CatalogueTitle(long TitleId, Listing Listing);

/// <summary>A used-up allowance, and when the supplier renews it, if it says.</summary>
internal sealed record Exhaustion(DateTimeOffset? Until);

/// <summary>
/// A movie, or a show, season or episode, as a supplier's catalogue lists it.
/// The host decides whether a file names it.
/// </summary>
internal sealed record Listing(VideoKind Kind, IReadOnlyList<string> Titles)
{
    /// <summary>
    /// The title's own year: the movie's, or the show's first. A supplier
    /// whose year means something else for a show reports none.
    /// </summary>
    internal int? Year { get; init; }
    internal int? Season { get; init; }
    internal int? Episode { get; init; }
}
