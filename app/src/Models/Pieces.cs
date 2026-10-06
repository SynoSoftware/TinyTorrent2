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

    private static string Label(Strings text, PieceKind kind, int count) => text.Format("pieces", "count", Name(text, kind), count);

    internal (string Text, InfoBarSeverity Severity) Summary(Strings text)
    {
        if (!MetadataReady) return (text.Get("pieces", "metadata"), InfoBarSeverity.Informational);
        if (States.All(state => state == PieceKind.Verified)) return (text.Get("pieces", "complete"), InfoBarSeverity.Success);
        if (Peers == 0) return (text.Get("pieces", "no_peers"), InfoBarSeverity.Warning);
        var unavailable = new int[Count + 1];
        for (var index = 0; index < Count; index++)
            unavailable[index + 1] = unavailable[index] + (States[index] == PieceKind.Unavailable ? 1 : 0);
        if (unavailable[Count] == 0) return (text.Get("pieces", "available"), InfoBarSeverity.Success);
        var names = Names(text, Files.Where(file => unavailable[file.End] > unavailable[file.First]));
        return (text.FormatCount("pieces", "unavailable_files", unavailable[Count], string.Join(", ", names)), InfoBarSeverity.Error);
    }

    internal (string Range, string Facts) Describe(Strings text, int first, int end)
    {
        var counts = Counts(first, end);
        var copies = Enumerable.Range(first, end - first)
            .Where(index => States[index] is not (PieceKind.Verified or PieceKind.Downloading))
            .Select(index => Availability[index]).ToArray();
        var range = end == first + 1 ? text.Format("pieces", "piece", end) : text.Format("pieces", "range", first + 1, end);
        var parts = new List<string>();
        if (copies.Length > 0)
            parts.Add(Peers == 0 ? text.Get("pieces", "unknown")
                : end == first + 1 ? text.Format("pieces", "copies", copies[0])
                : copies.Min() == copies.Max() ? text.Format("pieces", "copies_each", copies[0])
                : text.Format("pieces", "copies_range", copies.Min(), copies.Max()));
        parts.Add(string.Join(", ", Enum.GetValues<PieceKind>().Reverse().Where(kind => counts[(int)kind] > 0)
            .Select(kind => Label(text, kind, counts[(int)kind]))));
        var files = Names(text, Files.Where(file => file.First < end && file.End > first));
        if (files.Length > 0) parts.Add(string.Join(", ", files));
        return (range, parts.Aggregate((line, part) => text.Format("pieces", "detail", line, part)));
    }

    private static string[] Names(Strings text, IEnumerable<PieceFile> files)
    {
        var paths = files.Select(file => file.Path).ToArray();
        return paths.Length <= Listed ? paths : [.. paths.Take(Listed), text.Format("pieces", "more", paths.Length - Listed)];
    }
}

public sealed record PieceFile(string Path, int First, int End);
