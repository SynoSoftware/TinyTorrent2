
namespace Syno.TinyTorrent.Library;

public sealed record Entry(long EntryId, string Path, string Name, long Size)
{
    public FileKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;

    // "movie" or "episode" for a video, from its provider match or else its name.
    public string Type { get; init; } = string.Empty;

    // The catalogue of the provider match that supplies the file's Library
    // metadata, or null when the file has none.
    internal string? CatalogId { get; init; }
    public bool HasMetadata => CatalogId is not null;

    // The selected provider searched for the file and found no match.
    public bool IsUnmatched { get; init; }
    public int? Year { get; init; }
    public Ratings Ratings { get; init; } = new();

    // The IMDb number of the movie or the episode's series, which gives its
    // rating from the downloaded IMDb data.
    internal long? ImdbId { get; init; }
    public string Genres { get; init; } = string.Empty;
    public string Cast { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public Languages Subtitles { get; init; } = Languages.None;
    public int? Track { get; init; }
    public long? Duration { get; init; }
    public long? Bitrate { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? Created { get; init; }
    public long? Modified { get; init; }
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>
/// Language codes in order. Two lists with the same codes are equal, so an
/// <see cref="Entry"/> read again with unchanged facts equals the earlier one.
/// </summary>
public sealed class Languages(IList<string> codes)
    : System.Collections.ObjectModel.ReadOnlyCollection<string>(codes),
        IEquatable<Languages>
{
    internal static Languages None { get; } = new([]);

    public bool Equals(Languages? other) => other is not null && this.SequenceEqual(other);

    public override bool Equals(object? other) => Equals(other as Languages);

    public override int GetHashCode() =>
        string.Join(',', this).GetHashCode(StringComparison.Ordinal);
}

internal sealed record Query(string Text, LibraryConfiguration Configuration)
{
    internal bool IncludesFacets { get; init; }

    // The selected provider's catalogue, or null when no provider is selected.
    internal string? CatalogId { get; init; }
    internal IReadOnlyDictionary<string, string> Filters =>
        Configurations.GetValueOrDefault(Configuration) ?? new Dictionary<string, string>();
    internal IReadOnlyDictionary<
        LibraryConfiguration,
        IReadOnlyDictionary<string, string>
    > Configurations { get; init; } =
        new Dictionary<LibraryConfiguration, IReadOnlyDictionary<string, string>>();
}

internal sealed record Matches(
    IReadOnlyList<Entry> Entries,
    IReadOnlyList<Facet> Facets,
    IReadOnlyDictionary<LibraryConfiguration, int> Counts
);

public sealed record Facet(string Section, string Value, int Count)
{
    public string? Label { get; init; }
}

internal sealed record Detail(
    Entry Entry,
    string Cast,
    string Synopsis,
    IReadOnlyList<Origin> Origins
)
{
    internal VideoChoice? Series { get; init; }
    internal IReadOnlyList<Episode> Episodes { get; init; } = [];
    internal string Directors { get; init; } = string.Empty;
    internal string Certification { get; init; } = string.Empty;
    internal long? BoxOffice { get; init; }
}

public sealed record Origin(string OriginId, string Name, int FileIndex);
