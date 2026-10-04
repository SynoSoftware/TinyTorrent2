using System.Text.Json;

namespace Syno.TinyTorrent;

public sealed class Pieces
{
    public bool MetadataReady { get; }
    public int PieceSize { get; }
    public int Peers { get; }
    public IReadOnlyList<bool> Verified { get; }
    public IReadOnlyList<int> Availability { get; }
    public IReadOnlyDictionary<int, double> Downloading { get; }
    public IReadOnlyList<PieceFile> Files { get; }
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
    }

    internal PieceKind[] Classify()
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

    internal string Summary(Strings text, PieceKind[] states)
    {
        if (!MetadataReady) return text.Get("pieces", "metadata");
        if (states.All(state => state == PieceKind.Verified)) return text.Get("pieces", "complete");
        if (Peers == 0) return text.Get("pieces", "no_peers");
        var unavailable = new int[Count + 1];
        for (var index = 0; index < Count; index++)
            unavailable[index + 1] = unavailable[index] + (states[index] == PieceKind.Unavailable ? 1 : 0);
        if (unavailable[Count] == 0) return text.Get("pieces", "available");
        var names = Files.Where(file => unavailable[file.End] > unavailable[file.First]).Select(file => file.Path);
        return text.FormatCount("pieces", "unavailable_files", unavailable[Count], string.Join(", ", names));
    }

    internal string Describe(Strings text, int first, int end, PieceKind[] states)
    {
        var counts = new int[6];
        for (var index = first; index < end; index++) counts[(int)states[index]]++;
        var lines = new List<string>
        {
            text.Format("pieces", "range", first + 1, end),
            string.Join(", ", Files.Where(file => file.First < end && file.End > first).Select(file => file.Path))
        };
        foreach (var kind in Enum.GetValues<PieceKind>().Reverse())
            lines.Add(text.Format("pieces", "count", text.Get("pieces", kind.ToString().ToLowerInvariant()), counts[(int)kind]));
        if (end == first + 1 && states[first] is not PieceKind.Verified and not PieceKind.Downloading)
            lines.Add(Peers == 0 ? text.Get("pieces", "unknown") : text.Format("pieces", "copies", Availability[first]));
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed record PieceFile(string Path, int First, int End);
