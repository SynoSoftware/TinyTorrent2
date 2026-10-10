using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

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

    Task<IReadOnlyList<VideoChoice>> Search(string query, string language, CancellationToken cancellation);

    /// <summary>
    /// Describes a choice from this provider's catalogue. A series choice
    /// describes the release's episodes; Library sends one only with a release
    /// that has a season.
    /// </summary>
    Task<VideoInformation> Describe(VideoChoice choice, VideoRequest request, CancellationToken cancellation);
}

internal interface IConfigurable : IProvider
{
    /// <summary>
    /// Creates the editor for this provider's settings. The editor submits new
    /// settings through <paramref name="save"/>, which saves them and replaces
    /// this provider.
    /// </summary>
    VideoEditor Edit(Strings text, Func<IReadOnlyDictionary<string, string>, Task> save);
}

internal sealed record ProviderOption(string ProviderId, string TextSection,
    Func<IReadOnlyDictionary<string, string>, IProvider> Create);

/// <summary>When Library asks a provider for information.</summary>
internal abstract record VideoSchedule
{
    /// <summary>Library looks up every video in the background.</summary>
    internal sealed record Background : VideoSchedule;

    /// <summary>Library looks up the selected video once the person stays on it for the delay.</summary>
    internal sealed record Selection(TimeSpan Delay) : VideoSchedule;

    /// <summary>Library looks up a video only when the person asks.</summary>
    internal sealed record Manual : VideoSchedule;
}

public sealed record VideoChoice(string CatalogId, string VideoId, VideoKind Kind, string Title)
{
    public int? Year { get; init; }

    /// <summary>The title in its original language, when the provider knows it.</summary>
    public string? Original { get; init; }

    public string Label => Title + (Year is { } year ? " (" + year + ")" : string.Empty);
}

internal sealed record VideoRequest(Release Release, string Language, SavedRecord Saved);

/// <summary>
/// Returns the saved record at a provider path, or null when the provider must
/// read it again.
/// </summary>
internal delegate Task<VideoRecord?> SavedRecord(string path, CancellationToken cancellation);

internal sealed record VideoEditor(UserControl Content, IDraft Draft, Action RefreshText);

/// <summary>
/// Facts about a movie, or about a series' episodes. An episode's
/// <see cref="Title"/> is its series' title.
/// </summary>
internal sealed record VideoInformation(string CatalogId, string VideoId, string Title, VideoKind Kind)
{
    internal VideoChoice? Series { get; init; }
    internal IReadOnlyList<Episode> Episodes { get; init; } = [];
    internal IReadOnlyList<VideoRecord> Records { get; init; } = [];
    internal int? Year { get; init; }
    internal IReadOnlyList<string> Genres { get; init; } = [];
    internal IReadOnlyList<string> Cast { get; init; } = [];
    internal string Synopsis { get; init; } = string.Empty;
    internal IReadOnlyList<string> Keywords { get; init; } = [];

    internal static int? ParseYear(string date) => date.Length >= 4 && int.TryParse(date[..4], out var year) ? year : null;
}

internal sealed record Episode(int Season, int Number, string Name, string Synopsis);

internal sealed record VideoRecord(string Path, JsonElement Content, long Retrieved);

internal class VideoException(string section, string reason, Exception? inner = null)
    : Exception(reason, inner)
{
    internal string Section { get; } = section;
    internal string Reason { get; } = reason;
}

internal class ProviderException(string section, string reason, DateTimeOffset? retryAt = null, Exception? inner = null)
    : VideoException(section, reason, inner)
{
    internal DateTimeOffset? RetryAt { get; } = retryAt;
}
