using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;

namespace Syno.TinyTorrent.Torrents;

public sealed class Pieces
{
    public bool MetadataReady { get; }
    public int PieceSize { get; }
    public int PeerCount { get; }
    public IReadOnlyList<bool> Verified { get; }
    public IReadOnlyList<bool> Wanted { get; }
    public IReadOnlyList<int> Availability { get; }
    public IReadOnlyDictionary<int, double> Downloading { get; }
    internal PieceFiles Files { get; }
    public IReadOnlyList<PieceKind> States { get; }
    public int Count => Verified.Count;

    internal Pieces(JsonElement data, PieceFiles files, CancellationToken cancellation = default)
    {
        MetadataReady = data.GetProperty("metadata_ready").GetBoolean();
        PieceSize = data.GetProperty("piece_size").GetInt32();
        PeerCount = data.GetProperty("peer_count").GetInt32();
        Verified = data.GetProperty("verified")
            .EnumerateArray()
            .Select(value =>
            {
                cancellation.ThrowIfCancellationRequested();
                return value.GetBoolean();
            })
            .ToArray();
        Wanted = data.GetProperty("wanted")
            .EnumerateArray()
            .Select(value =>
            {
                cancellation.ThrowIfCancellationRequested();
                return value.GetBoolean();
            })
            .ToArray();
        Availability = data.GetProperty("availability")
            .EnumerateArray()
            .Select(value =>
            {
                cancellation.ThrowIfCancellationRequested();
                return value.GetInt32();
            })
            .ToArray();
        Downloading = data.GetProperty("downloading")
            .EnumerateArray()
            .ToDictionary(
                value =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return value.GetProperty("index").GetInt32();
                },
                value => value.GetProperty("progress").GetDouble()
            );
        Files = files;
        if (Availability.Count != Count || Wanted.Count != Count)
            throw new InvalidDataException("Incomplete piece availability.");
        States = Classify(cancellation);
    }

    private PieceKind[] Classify(CancellationToken cancellation)
    {
        var threshold = Math.Max(1, (int)Math.Ceiling(Availability.DefaultIfEmpty().Max() * 0.15));
        var states = new PieceKind[Count];
        for (var index = 0; index < Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Verified[index])
                states[index] = PieceKind.Verified;
            else if (Downloading.ContainsKey(index))
                states[index] = PieceKind.Downloading;
            else if (PeerCount == 0)
                states[index] = PieceKind.Missing;
            else if (Availability[index] == 0)
                states[index] = PieceKind.Unavailable;
            else if (Availability[index] <= threshold)
                states[index] = PieceKind.Rare;
            else
                states[index] = PieceKind.Common;
        }
        return states;
    }

    internal bool SameMap(Pieces other) =>
        MetadataReady == other.MetadataReady
        && PieceSize == other.PieceSize
        && PeerCount == other.PeerCount
        && Verified.SequenceEqual(other.Verified)
        && Wanted.SequenceEqual(other.Wanted)
        && Availability.SequenceEqual(other.Availability)
        && Files == other.Files
        && Downloading.Count == other.Downloading.Count
        && Downloading.All(piece =>
            other.Downloading.TryGetValue(piece.Key, out var progress) && progress == piece.Value
        );

    internal int[] Counts(int first, int end)
    {
        var counts = new int[Enum.GetValues<PieceKind>().Length];
        for (var index = first; index < end; index++)
            counts[(int)States[index]]++;
        return counts;
    }

    internal static string Name(Strings text, PieceKind kind) =>
        text.Get("pieces", kind.ToString().ToLowerInvariant());

    internal static string Label(Strings text, PieceKind kind, int count) =>
        text.Format("pieces", "count", Name(text, kind), count);

    internal static Conclusion Waiting(Strings text) =>
        Known(text, "metadata", InfoBarSeverity.Informational);

    internal Conclusion Conclude(Strings text)
    {
        if (!MetadataReady)
            return Waiting(text);
        if (Enumerable.Range(0, Count).All(index => !Wanted[index] || Verified[index]))
            return Known(text, "complete", InfoBarSeverity.Success);
        if (PeerCount == 0)
            return Known(text, "no_peers", InfoBarSeverity.Warning);
        var unavailable = new int[Count + 1];
        for (var index = 0; index < Count; index++)
            unavailable[index + 1] =
                unavailable[index]
                + (Wanted[index] && States[index] == PieceKind.Unavailable ? 1 : 0);
        if (unavailable[Count] == 0)
            return Known(text, "available", InfoBarSeverity.Success);
        var names = Files.Names(
            text,
            pieces => unavailable[pieces.End] > unavailable[pieces.First]
        );
        return new(
            text.Get("pieces", "cannot_finish"),
            text.FormatCount(
                "pieces",
                "unavailable_files",
                unavailable[Count],
                string.Join(", ", names)
            ),
            InfoBarSeverity.Error
        );
    }

    // A conclusion whose reason is fixed text.
    private static Conclusion Known(Strings text, string key, InfoBarSeverity severity) =>
        new(text.Get("pieces", key), text.Get("pieces", key + "_reason"), severity);

    internal PieceDetail Describe(Strings text, int first, int end)
    {
        var copies = Enumerable
            .Range(first, end - first)
            .Where(index => States[index] is not (PieceKind.Verified or PieceKind.Downloading))
            .Select(index => Availability[index])
            .ToArray();
        var range =
            end == first + 1
                ? text.Format("pieces", "piece", end)
                : text.Format("pieces", "range", first + 1, end);
        var peers =
            copies.Length == 0 ? null
            : PeerCount == 0 ? text.Get("pieces", "unknown")
            : end == first + 1 ? text.FormatCount("pieces", "copies", copies[0])
            : copies.Min() == copies.Max() ? text.FormatCount("pieces", "copies_each", copies[0])
            : text.Format("pieces", "copies_range", copies.Min(), copies.Max());
        var files = string.Join(
            ", ",
            Files.Names(text, pieces => pieces.First < end && pieces.End > first)
        );
        return new PieceDetail(range, peers, Counts(first, end), files);
    }
}

// The payload files of a map, in file order. A torrent above the file
// browser's window is read without its names, which would cost tens of
// megabytes, so its map counts files instead of naming them.
internal abstract record PieceFiles
{
    private PieceFiles() { }

    // No files, while the map's files are unread. It is one instance, so two
    // such maps compare equal.
    internal static readonly PieceFiles None = new Named([]);

    // How the map lists the files whose pieces `covers` accepts.
    internal abstract string[] Names(Strings text, Func<PieceRange, bool> covers);

    internal sealed record Named(IReadOnlyList<PieceFile> Files) : PieceFiles
    {
        private const int Listed = 3;

        internal override string[] Names(Strings text, Func<PieceRange, bool> covers)
        {
            string[] paths = [.. Files.Where(file => covers(file.Pieces)).Select(file => file.Path)];
            return paths.Length <= Listed
                ? paths
                : [.. paths.Take(Listed), text.Format("pieces", "more", paths.Length - Listed)];
        }
    }

    internal sealed record Counted(IReadOnlyList<PieceRange> Files) : PieceFiles
    {
        internal override string[] Names(Strings text, Func<PieceRange, bool> covers)
        {
            var count = Files.Count(covers);
            return count == 0 ? [] : [text.FormatCount("pieces", "files", count)];
        }
    }
}

internal sealed record PieceFile(string Path, PieceRange Pieces);

// Whether the download can finish: the answer, and the reason behind it.
public sealed record Conclusion(string Answer, string Reason, InfoBarSeverity Severity)
{
    internal string Line(Strings text) => text.Format("pieces", "status", Answer, Reason);
}

// What one square of the map holds; Counts is indexed by PieceKind.
public sealed record PieceDetail(string Range, string? Peers, int[] Counts, string Files)
{
    // The kinds present, in legend order.
    private IEnumerable<PieceKind> Kinds =>
        Enum.GetValues<PieceKind>().Reverse().Where(kind => Counts[(int)kind] > 0);

    internal string Line(Strings text) =>
        new[]
        {
            Range,
            Peers,
            string.Join(", ", Kinds.Select(kind => Pieces.Label(text, kind, Counts[(int)kind]))),
            Files,
        }
            .OfType<string>()
            .Where(part => part.Length > 0)
            .Aggregate((line, part) => text.Format("pieces", "detail", line, part));
}
