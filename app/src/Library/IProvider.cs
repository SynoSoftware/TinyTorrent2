using System.Text.Json;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library;

/// <summary>
/// An online service that supplies video information. Library owns matching,
/// saving, scheduling and failure policy; a provider only searches and describes.
/// </summary>
/// <remarks>
/// A <see cref="ProviderException"/> means the provider cannot serve any request
/// now; any other <see cref="VideoException"/> concerns one video only.
/// </remarks>
internal interface IProvider
{
    string CatalogId { get; }
    bool IsAvailable { get; }
    VideoSchedule Schedule { get; }

    /// <summary>
    /// Whether information depends on the requested language. Library saves
    /// information that does not as valid for every language.
    /// </summary>
    bool IsLocalized { get; }

    /// <summary>The kinds <see cref="Search"/> returns: movies, series, or both.</summary>
    IReadOnlySet<VideoKind> Kinds { get; }

    Task<IReadOnlyList<VideoChoice>> Search(
        string query,
        string language,
        CancellationToken cancellation
    );

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
/// A provider the person can choose. <see cref="TextSection"/> holds its
/// label, disclosure and error text; <see cref="Privacy"/> and
/// <see cref="Terms"/> are the provider's own policy pages.
/// </summary>
internal sealed record ProviderOption(
    string ProviderId,
    string Name,
    string TextSection,
    Uri Privacy,
    Uri Terms,
    Func<IProvider> Create
)
{
    /// <summary>The name shown in choices, which tells options for one website apart.</summary>
    internal string Label(Strings text) => text.Format(TextSection, "label", Name);
}

/// <summary>When Library asks a provider for information.</summary>
internal enum VideoSchedule
{
    Background,
    Manual,
}

public sealed record VideoChoice(string CatalogId, string VideoId, VideoKind Kind, string Title)
{
    public int? Year { get; init; }

    /// <summary>The title in its original language, when the provider knows it.</summary>
    public string? Original { get; init; }

    /// <summary>
    /// The facts the provider's listing showed, in a form the provider reads.
    /// Library saves them with the choice.
    /// </summary>
    internal JsonElement? Content { get; init; }

    public string Label => Title + (Year is { } year ? " (" + year + ")" : string.Empty);
}

internal sealed record VideoRequest(Release Release, string Language, SavedRecord Saved);

/// <summary>
/// Returns the saved record at a provider path, or null when the provider must
/// read it again.
/// </summary>
internal delegate Task<VideoRecord?> SavedRecord(string path, CancellationToken cancellation);

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
    internal Rating? ImdbRating { get; init; }
    internal Rating? TmdbRating { get; init; }

    /// <summary>The other videos the provider's page listed, which Library saves as choices.</summary>
    internal IReadOnlyList<VideoChoice> Related { get; init; } = [];

    internal static int? ParseYear(string date) =>
        date.Length >= 4 && int.TryParse(date[..4], out var year) ? year : null;
}

/// <summary>A rating on a ten-point scale for the specified movie or series.</summary>
public sealed record Rating(double Value, VideoKind Kind);

internal sealed record Episode(int Season, int Number, string Name, string Synopsis);

internal sealed record VideoRecord(string Path, JsonElement Content, long Retrieved);

// Detail fills the reason text's placeholder, such as the browser's network
// error or an HTTP status.
internal class VideoException(string section, string reason, Exception? inner = null, string? detail = null)
    : Exception(reason, inner)
{
    internal string Section { get; } = section;
    internal string Reason { get; } = reason;
    internal string? Detail { get; } = detail;
}

internal class ProviderException(
    string section,
    string reason,
    DateTimeOffset? retryAt = null,
    Exception? inner = null,
    string? detail = null
) : VideoException(section, reason, inner, detail)
{
    internal DateTimeOffset? RetryAt { get; } = retryAt;
}

/// <summary>
/// The provider cannot use the saved network route. The person fixes it in
/// Network settings.
/// </summary>
internal sealed class RouteException(string section, Exception inner)
    : ProviderException(section, "route_unavailable", inner: inner);
