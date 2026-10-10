using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Library;

public sealed record Entry(long EntryId, string Path, string Name, long Size)
{
    public FileKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public int? Year { get; init; }
    public string Genres { get; init; } = string.Empty;
    public string Cast { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public int? Track { get; init; }
    public long? Duration { get; init; }
    public long? Created { get; init; }
    public long? Modified { get; init; }
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

internal sealed record Query(string Text, LibraryConfiguration Configuration)
{
    internal IReadOnlyDictionary<string, string> Filters =>
        Configurations.GetValueOrDefault(Configuration) ?? new Dictionary<string, string>();
    internal IReadOnlyDictionary<LibraryConfiguration, IReadOnlyDictionary<string, string>> Configurations { get; init; } =
        new Dictionary<LibraryConfiguration, IReadOnlyDictionary<string, string>>();
}

internal sealed record Matches(IReadOnlyList<Entry> Entries,
    IReadOnlyList<Facet> Facets, IReadOnlyDictionary<LibraryConfiguration, int> Counts);

public sealed record Facet(string Section, string Value, int Count);

internal sealed record Detail(Entry Entry, string Cast, string Synopsis, IReadOnlyList<Origin> Origins);

public sealed record Origin(string OriginId, string Name);
