using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Library;

/// <summary>
/// A source of video information. Library owns matching, saving, scheduling,
/// request protection and failure text; a provider only builds its requests
/// and reads their answers.
/// </summary>
/// <remarks>
/// A <see cref="ProviderException"/> means the provider cannot serve any request
/// now; any other <see cref="VideoException"/> concerns one video only.
/// </remarks>
internal interface IProvider
{
    string Prepare(string query) => query.Trim();

    Task<VideoSearch> Search(VideoQuery query, CancellationToken cancellation);

    /// <summary>
    /// Describes a choice from this provider's catalogue. A series choice
    /// describes the release's episodes; Library sends one only with a release
    /// that has a season.
    /// </summary>
    Task<VideoInformation> Describe(
        VideoChoice choice,
        VideoRequest request,
        CancellationToken cancellation
    );
}

/// <summary>
/// A provider the person can choose, and everything Library needs to know
/// about it. <see cref="ProviderId"/> also names the catalogue its videos
/// belong to and the text section that holds its consent text.
/// <see cref="Create"/> receives the API key Library chose: the person's own,
/// the one built into the release, or an empty key.
/// </summary>
internal sealed record ProviderOption(
    string ProviderId,
    string Name,
    Uri Privacy,
    Uri Terms,
    Func<string, IProvider> Create
)
{
    internal static readonly IReadOnlySet<VideoKind> Movies = new HashSet<VideoKind>
    {
        VideoKind.Movie,
    };

    /// <summary>
    /// <see cref="Access.Key"/> when the provider needs an API key, which the
    /// person can replace with their own.
    /// </summary>
    internal Access Access { get; init; }

    internal string? BuiltInKey { get; init; }
    internal bool HasBuiltInKey => !string.IsNullOrWhiteSpace(BuiltInKey);

    /// <summary>The kinds the provider's searches return: movies, series, or both.</summary>
    internal IReadOnlySet<VideoKind> Kinds { get; init; } = Movies;

    /// <summary>
    /// Whether information depends on the requested language. Library saves
    /// information that does not as valid for every language.
    /// </summary>
    internal bool IsLocalized { get; init; }

    /// <summary>
    /// The data the provider answers from, which the person downloads, or null
    /// for a provider that answers over the network.
    /// </summary>
    internal Imdb.Dataset? Dataset { get; init; }

    /// <summary>
    /// Whether the provider answers over the network. Only such a provider can
    /// fetch an individual movie, and Library protects and saves its requests.
    /// </summary>
    internal bool IsOnline => Dataset is null;
}

public sealed record VideoChoice(string CatalogId, string VideoId, VideoKind Kind, string Title)
{
    public int? Year { get; init; }

    /// <summary>The title in its original language, when the provider knows it.</summary>
    public string? Original { get; init; }

    public string Label => Title + (Year is { } year ? " (" + year + ")" : string.Empty);
    public string YearText => Year?.ToString(CultureInfo.CurrentCulture) ?? "—";
}

internal sealed record VideoQuery(string Text, string Language, ProviderGet Get)
{
    internal int? Year { get; init; }
}

internal sealed record VideoSearch(IReadOnlyList<VideoChoice> Choices, bool Empty)
{
    internal bool Broadened { get; init; }
}

internal sealed record VideoRequest(
    Release Release,
    string Language,
    ReadRecord Read,
    ProviderGet Get
);

/// <summary>
/// Sends a provider's request through Library, which applies request
/// protection and the saved network route, and returns the JSON answer.
/// <paramref name="refusal"/> reads a failure that the provider reports in
/// the body of a successful answer, such as OMDb's error text.
/// </summary>
/// <exception cref="ProviderException">The provider cannot answer any request now.</exception>
/// <exception cref="MissingVideoException">The provider has no such video.</exception>
internal delegate Task<JsonElement> ProviderGet(
    HttpRequestMessage request,
    CancellationToken cancellation,
    Func<JsonElement, VideoFailure?>? refusal = null
);

/// <summary>
/// Library reuses or downloads one resource and saves its answer before the
/// provider reads the next resource in a multi-episode description.
/// </summary>
internal delegate Task<VideoRecord> ReadRecord(
    string path,
    Func<CancellationToken, Task<VideoRecord>> download,
    CancellationToken cancellation
);

/// <summary>
/// Facts about a movie, or about a series' episodes. An episode's
/// <see cref="Title"/> is its series' title.
/// </summary>
internal sealed record VideoInformation(
    string CatalogId,
    string VideoId,
    string Title,
    VideoKind Kind
)
{
    internal VideoChoice? Series { get; init; }
    internal IReadOnlyList<Episode> Episodes { get; init; } = [];
    internal IReadOnlyList<VideoRecord> Records { get; init; } = [];
    internal int? Year { get; init; }
    internal IReadOnlyList<string> Genres { get; init; } = [];
    internal IReadOnlyList<string> Cast { get; init; } = [];
    internal string Synopsis { get; init; } = string.Empty;
    internal IReadOnlyList<string> Keywords { get; init; } = [];
    internal Ratings Ratings { get; init; } = new();
    internal IReadOnlyList<string> Directors { get; init; } = [];

    /// <summary>The age certification, such as PG-13, as the provider reports it.</summary>
    internal string Certification { get; init; } = string.Empty;

    /// <summary>The box office takings in US dollars.</summary>
    internal long? BoxOffice { get; init; }

    /// <summary>
    /// The IMDb number of the movie, or of the episodes' series, such as 133093
    /// for tt0133093. Library saves the ratings and title facts once per IMDb
    /// number, shared by every provider, and shows the rating from the
    /// downloaded IMDb data.
    /// </summary>
    internal long? ImdbId { get; init; }

    /// <summary>The other videos the provider's page listed, which Library saves as choices.</summary>
    internal IReadOnlyList<VideoChoice> Related { get; init; } = [];

    internal static int? ParseYear(string date) =>
        date.Length >= 4 && int.TryParse(date[..4], out var year) ? year : null;

    /// <summary>The number of an IMDb ID such as tt0133093, or null for another value.</summary>
    internal static long? ParseImdbId(ReadOnlySpan<char> id) =>
        id.StartsWith("tt", StringComparison.Ordinal)
        && long.TryParse(
            id[2..],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var number
        )
        && number > 0
            ? number
            : null;

    /// <summary>The IMDb ID of a number, such as tt0133093 for 133093.</summary>
    internal static string FormatImdbId(long number) =>
        "tt" + number.ToString("D7", CultureInfo.InvariantCulture);
}

/// <summary>A rating on a ten-point scale for the specified movie or series.</summary>
public sealed record Rating(double Value, VideoKind Kind)
{
    /// <summary>
    /// The number of votes behind the rating, which shows whether it means
    /// much; null when the source does not report it.
    /// </summary>
    public long? Votes { get; init; }
}

/// <summary>A video's ratings, at most one from each <see cref="RatingSource"/>.</summary>
public sealed record Ratings
{
    public Rating? Imdb { get; init; }
    public Rating? Tmdb { get; init; }
    public Rating? RottenTomatoes { get; init; }
    public Rating? Popcornmeter { get; init; }
    public Rating? Metacritic { get; init; }

    /// <summary>
    /// These ratings, with each site's rating from <paramref name="found"/>
    /// unless it has fewer votes. A site's vote count only grows, so the
    /// rating with more votes is the newer one, and a provider that lacks a
    /// rating or its count never replaces a saved rating that has them.
    /// </summary>
    internal Ratings Merge(Ratings found) =>
        new()
        {
            Imdb = Newer(Imdb, found.Imdb),
            Tmdb = Newer(Tmdb, found.Tmdb),
            RottenTomatoes = Newer(RottenTomatoes, found.RottenTomatoes),
            Popcornmeter = Newer(Popcornmeter, found.Popcornmeter),
            Metacritic = Newer(Metacritic, found.Metacritic),
        };

    private static Rating? Newer(Rating? saved, Rating? found) =>
        found is null || saved?.Votes > (found.Votes ?? 0) ? saved : found;
}

/// <summary>
/// A site whose rating Library shows. A rating keeps the ten-point value that
/// sorting compares; <see cref="Maximum"/> and <see cref="IsPercent"/> give the
/// scale the site itself shows, so a Rotten Tomatoes 5.9 reads as 59%.
/// <see cref="Id"/> names its column and its text.
/// </summary>
public sealed record RatingSource(
    string Id,
    double Maximum,
    bool IsPercent,
    Func<Ratings, Rating?> Read
)
{
    internal RatingSample Sample(Ratings ratings)
    {
        if (Read(ratings)?.Votes is not { } votes)
            return RatingSample.Unknown;
        var (moderate, large) =
            this == RottenTomatoes ? (20, 100)
            : this == Metacritic ? (10, 40)
            : (200, 2_000);
        return votes < moderate ? RatingSample.Small
            : votes < large ? RatingSample.Moderate
            : RatingSample.Large;
    }

    public static RatingSource Imdb { get; } = new("imdb", 10, false, ratings => ratings.Imdb);
    public static RatingSource Tmdb { get; } = new("tmdb", 10, false, ratings => ratings.Tmdb);
    public static RatingSource RottenTomatoes { get; } =
        new("rottentomatoes", 100, true, ratings => ratings.RottenTomatoes);

    /// <summary>Rotten Tomatoes' audience score.</summary>
    public static RatingSource Popcornmeter { get; } =
        new("popcornmeter", 100, true, ratings => ratings.Popcornmeter);
    public static RatingSource Metacritic { get; } =
        new("metacritic", 100, false, ratings => ratings.Metacritic);

    /// <summary>Every source, in the order Library shows them.</summary>
    public static IReadOnlyList<RatingSource> All { get; } =
    [Imdb, Tmdb, RottenTomatoes, Popcornmeter, Metacritic];
}

internal sealed record Episode(int Season, int Number, string Name, string Synopsis);

internal sealed record VideoRecord(string Path, JsonElement Content, long Retrieved);

internal class VideoException(VideoFailure failure, Exception? inner = null)
    : Exception(failure.ToString(), inner)
{
    internal VideoFailure Failure { get; } = failure;

    /// <summary>The provider that failed, which Library sets so the text can name it.</summary>
    internal ProviderOption? Provider { get; set; }

    /// <summary>What the text names instead when no provider is involved, such as the city data's publisher.</summary>
    internal string? Subject { get; set; }
}

internal class ProviderException(
    VideoFailure failure,
    DateTimeOffset? retryAt = null,
    Exception? inner = null
) : VideoException(failure, inner)
{
    internal ProviderException(RequestBlock block)
        : this(VideoFailure.Blocked, block.RetryAt) => Block = block;

    internal DateTimeOffset? RetryAt { get; } = retryAt;

    /// <summary>The request protection that holds lookups back, when the failure is <see cref="VideoFailure.Blocked"/>.</summary>
    internal RequestBlock? Block { get; }
}

/// <summary>
/// The provider cannot use the saved network route. The person fixes it in
/// Network settings.
/// </summary>
internal sealed class RouteException(Exception inner)
    : ProviderException(VideoFailure.Route, inner: inner);

internal sealed class MissingVideoException() : VideoException(VideoFailure.Missing);
