using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Models;

public sealed class Pieces
{
    private const int Listed = 3;

    public bool MetadataReady { get; }
    public int PieceSize { get; }
    public int Peers { get; }
    public IReadOnlyList<bool> Verified { get; }
    public IReadOnlyList<int> Availability { get; }
    public IReadOnlyDictionary<int, double> Downloading { get; }
    public IReadOnlyList<PieceFile> Files { get; }
    public IReadOnlyList<PieceKind> States { get; }
    public int Count => Verified.Count;

    internal Pieces(JsonElement data, IReadOnlyList<PieceFile> files)
    {
        MetadataReady = data.GetProperty("metadata_ready").GetBoolean();
        PieceSize = data.GetProperty("piece_size").GetInt32();
        Peers = data.GetProperty("peers").GetInt32();
        Verified = data.GetProperty("verified").EnumerateArray().Select(value => value.GetBoolean()).ToArray();
        Availability = data.GetProperty("availability").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        Downloading = data.GetProperty("downloading").EnumerateArray().ToDictionary(
            value => value.GetProperty("index").GetInt32(), value => value.GetProperty("progress").GetDouble());
        Files = data.TryGetProperty("files", out var topology) ? topology.EnumerateArray().Select(file => new PieceFile(
            file.GetProperty("path").GetString()!, file.GetProperty("first_piece").GetInt32(),
            file.GetProperty("end_piece").GetInt32())).ToArray() : files;
        if (Availability.Count != Count) throw new InvalidDataException("Incomplete piece availability.");
        States = Classify();
    }

    private PieceKind[] Classify()
    {
        var threshold = Math.Max(1, (int)Math.Ceiling(Availability.DefaultIfEmpty().Max() * 0.15));
        var states = new PieceKind[Count];
        for (var index = 0; index < Count; index++)
        {
            if (Verified[index]) states[index] = PieceKind.Verified;
            else if (Downloading.ContainsKey(index)) states[index] = PieceKind.Downloading;
            else if (Peers == 0) states[index] = PieceKind.Missing;
            else if (Availability[index] == 0) states[index] = PieceKind.Unavailable;
            else if (Availability[index] <= threshold) states[index] = PieceKind.Rare;
            else states[index] = PieceKind.Common;
        }
        return states;
    }

    internal bool SameMap(Pieces other) => MetadataReady == other.MetadataReady && PieceSize == other.PieceSize && Peers == other.Peers &&
        Verified.SequenceEqual(other.Verified) && Availability.SequenceEqual(other.Availability) && Files.SequenceEqual(other.Files) &&
        Downloading.Count == other.Downloading.Count && Downloading.All(piece => other.Downloading.TryGetValue(piece.Key, out var progress) && progress == piece.Value);

    internal int[] Counts(int first, int end)
    {
        var counts = new int[Enum.GetValues<PieceKind>().Length];
        for (var index = first; index < end; index++) counts[(int)States[index]]++;
        return counts;
    }

    internal static string Name(Strings text, PieceKind kind) => text.Get("pieces", kind.ToString().ToLowerInvariant());

    internal static string Label(Strings text, PieceKind kind, int count) => text.Format("pieces", "count", Name(text, kind), count);

    internal static Conclusion Waiting(Strings text) => Known(text, "metadata", InfoBarSeverity.Informational);

    internal Conclusion Conclude(Strings text)
    {
        if (!MetadataReady) return Waiting(text);
        if (States.All(state => state == PieceKind.Verified)) return Known(text, "complete", InfoBarSeverity.Success);
        if (Peers == 0) return Known(text, "no_peers", InfoBarSeverity.Warning);
        var unavailable = new int[Count + 1];
        for (var index = 0; index < Count; index++)
            unavailable[index + 1] = unavailable[index] + (States[index] == PieceKind.Unavailable ? 1 : 0);
        if (unavailable[Count] == 0) return Known(text, "available", InfoBarSeverity.Success);
        var names = Names(text, Files.Where(file => unavailable[file.End] > unavailable[file.First]));
        return new(text.Get("pieces", "cannot_finish"),
            text.FormatCount("pieces", "unavailable_files", unavailable[Count], string.Join(", ", names)), InfoBarSeverity.Error);
    }

    // A conclusion whose reason is fixed text.
    private static Conclusion Known(Strings text, string key, InfoBarSeverity severity) =>
        new(text.Get("pieces", key), text.Get("pieces", key + "_reason"), severity);

    internal PieceDetail Describe(Strings text, int first, int end)
    {
        var copies = Enumerable.Range(first, end - first)
            .Where(index => States[index] is not (PieceKind.Verified or PieceKind.Downloading))
            .Select(index => Availability[index]).ToArray();
        var range = end == first + 1 ? text.Format("pieces", "piece", end) : text.Format("pieces", "range", first + 1, end);
        var peers = copies.Length == 0 ? null
            : Peers == 0 ? text.Get("pieces", "unknown")
            : end == first + 1 ? text.Format("pieces", "copies", copies[0])
            : copies.Min() == copies.Max() ? text.Format("pieces", "copies_each", copies[0])
            : text.Format("pieces", "copies_range", copies.Min(), copies.Max());
        var files = string.Join(", ", Names(text, Files.Where(file => file.First < end && file.End > first)));
        return new PieceDetail(range, peers, Counts(first, end), files);
    }

    private static string[] Names(Strings text, IEnumerable<PieceFile> files)
    {
        var paths = files.Select(file => file.Path).ToArray();
        return paths.Length <= Listed ? paths : [.. paths.Take(Listed), text.Format("pieces", "more", paths.Length - Listed)];
    }
}

public sealed record PieceFile(string Path, int First, int End);

// Whether the download can finish: the answer, and the reason behind it.
public sealed record Conclusion(string Answer, string Reason, InfoBarSeverity Severity)
{
    internal string Line(Strings text) => text.Format("pieces", "status", Answer, Reason);
}

// What one square of the map holds; Counts is indexed by PieceKind.
public sealed record PieceDetail(string Range, string? Peers, int[] Counts, string Files)
{
    // The kinds present, in legend order.
    private IEnumerable<PieceKind> Kinds => Enum.GetValues<PieceKind>().Reverse().Where(kind => Counts[(int)kind] > 0);

    internal string Line(Strings text) => new[]
    {
        Range, Peers, string.Join(", ", Kinds.Select(kind => Pieces.Label(text, kind, Counts[(int)kind]))), Files
    }.OfType<string>().Where(part => part.Length > 0).Aggregate((line, part) => text.Format("pieces", "detail", line, part));
}
