#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Syno.TinyTorrent.Shell;

public static class PerformanceSource
{
    public sealed record Torrent(string TorrentId, int Rows, int Seeds, int Connections, int Steady);
    public sealed record Configuration(string Pipe, Torrent[] Torrents, int Connections, int SwapsPerSecond);
    public sealed record Viewport(int Torrents, int Peers);
    public sealed record Summary(string TorrentId, int Connected, int Seeds, int Connecting, int Handshaking,
        int PendingDisconnect, double DownloadRate, double UploadRate);
    public sealed record Status(int Tick, long Produced, Summary[] Torrents);
    private enum ReadKind
    {
        Status,
        Peers,
        Viewport,
    }
    private sealed record Request(ReadKind Kind, string? TorrentId = null, Viewport? Viewport = null, int? AfterTick = null);
    private sealed record Cache(byte[][] Peers, byte[] Status, int Tick);
    private sealed record Candidate(int Rank, int Churn, int Bank);

    public static Configuration Create(string[] torrentIds, int[] rows, int[] seeds, int connections, int swapsPerSecond)
    {
        var heavy = Array.IndexOf(rows, rows.Max());
        var counts = new int[rows.Length];
        counts[heavy] = 3000;
        var others = Enumerable.Range(0, rows.Length).Where(value => value != heavy).ToArray();
        for (var index = 0; index < connections - counts[heavy]; index++)
            counts[others[index % others.Length]]++;
        var steady = counts.Select(count => count * 4 / 5).ToArray();
        for (var index = 0; steady.Sum() < connections * 4 / 5; index = (index + 1) % rows.Length)
            if (steady[index] < counts[index]) steady[index]++;
        return new("TinyTorrent.Benchmark." + Guid.NewGuid().ToString("N"),
            torrentIds.Select((torrentId, index) => new Torrent(torrentId, rows[index], seeds[index], counts[index], steady[index])).ToArray(),
            connections, swapsPerSecond);
    }

    public static async Task<JsonElement> Peers(string pipe, string torrentId) =>
        await Read<JsonElement>(pipe, new(ReadKind.Peers, torrentId)).ConfigureAwait(false);

    public static Task<Status> State(string pipe, int? afterTick = null, CancellationToken cancellation = default) =>
        Read<Status>(pipe, new(ReadKind.Status, AfterTick: afterTick), cancellation);
    public static Task<bool> SetViewport(string pipe, Viewport viewport) => Read<bool>(pipe, new(ReadKind.Viewport, Viewport: viewport));
    public static string Endpoint(int torrent, int row) => $"198.{18 + torrent}.{row / 256}.{row % 256}:51413";

    private static async Task<T> Read<T>(string name, Request request, CancellationToken cancellation = default)
    {
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellation).ConfigureAwait(false);
        await Write(pipe, JsonSerializer.SerializeToUtf8Bytes(request), cancellation).ConfigureAwait(false);
        var bytes = await Read(pipe, cancellation).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes)!;
    }

    private static async Task<byte[]> Read(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        var bytes = new byte[BitConverter.ToInt32(header)];
        await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        return bytes;
    }

    private static async Task Write(Stream stream, byte[] bytes, CancellationToken cancellation)
    {
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellation).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
    }

    public static async Task Serve(string directory, CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cancellation = lifetime.Token;
        var path = Path.Combine(directory, "source.json");
        while (!File.Exists(path)) await Task.Delay(10, cancellation).ConfigureAwait(false);
        Configuration configuration;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            configuration = (await JsonSerializer.DeserializeAsync<Configuration>(stream, cancellationToken: cancellation).ConfigureAwait(false))!;
        var viewport = new Viewport(10, 10);
        var heavy = Array.FindIndex(configuration.Torrents, torrent => torrent.Connections == 3000);
        var order = new[] { heavy }.Concat(Enumerable.Range(0, configuration.Torrents.Length).Where(value => value != heavy)).ToArray();
        var candidates = new Dictionary<int, Candidate>[configuration.Torrents.Length];
        var ordinal = 0;
        for (var index = 0; index < candidates.Length; index++)
        {
            var torrent = configuration.Torrents[index];
            var churn = torrent.Connections - torrent.Steady;
            var slots = torrent.Steady + 2 * churn;
            candidates[index] = [];
            for (var rank = 0; rank < torrent.Steady; rank++)
                candidates[index].Add(rank * torrent.Rows / slots, new(rank, -1, 0));
            for (var rank = 0; rank < churn; rank++, ordinal++)
                for (var bank = 0; bank < 2; bank++)
                    candidates[index].Add((torrent.Steady + rank + bank * churn) * torrent.Rows / slots,
                        new(torrent.Steady + rank, ordinal, bank));
        }
        var measurements = new List<object>();
        var cache = Build(0, null, viewport);
        await File.WriteAllTextAsync(Path.Combine(directory, "source-ready"), "", cancellation).ConfigureAwait(false);
        var updating = Update();
        var clients = new List<Task>();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(configuration.Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try { await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false); }
                catch { server.Dispose(); throw; }
                foreach (var client in clients.Where(client => client.IsFaulted)) await client.ConfigureAwait(false);
                clients.RemoveAll(client => client.IsCompleted);
                clients.Add(Handle(server));
                if (updating.IsFaulted) await updating.ConfigureAwait(false);
            }
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(clients).ConfigureAwait(false); } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            try { await updating.ConfigureAwait(false); } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            await File.WriteAllTextAsync(Path.Combine(directory, "source-updates.json"), JsonSerializer.Serialize(measurements)).ConfigureAwait(false);
        }

        async Task Handle(NamedPipeServerStream server)
        {
            await using (server)
            {
                try
                {
                    var request = JsonSerializer.Deserialize<Request>(await Read(server, cancellation).ConfigureAwait(false))!;
                    while (request.Kind == ReadKind.Status && request.AfterTick is { } tick && Volatile.Read(ref cache).Tick <= tick)
                        await Task.Delay(5, cancellation).ConfigureAwait(false);
                    var current = Volatile.Read(ref cache);
                    byte[] reply;
                    if (request.Kind == ReadKind.Viewport)
                    {
                        Volatile.Write(ref viewport, request.Viewport!);
                        reply = JsonSerializer.SerializeToUtf8Bytes(true);
                    }
                    else if (request.Kind == ReadKind.Status) reply = current.Status;
                    else reply = current.Peers[Array.FindIndex(configuration.Torrents, torrent => torrent.TorrentId == request.TorrentId)];
                    await Write(server, reply, cancellation).ConfigureAwait(false);
                }
                catch (IOException) { }
            }
        }

        async Task Update()
        {
            while (!File.Exists(Path.Combine(directory, "source-go"))) await Task.Delay(10, cancellation).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            var tick = 0;
            int? churnStart = null;
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(Math.Max(1, (tick + 1) * 1000 - (int)clock.ElapsedMilliseconds), cancellation).ConfigureAwait(false);
                tick++;
                if (churnStart is null && File.Exists(Path.Combine(directory, "source-churn"))) churnStart = tick;
                Volatile.Write(ref cache, Build(tick, churnStart is { } started ? tick - started : null, Volatile.Read(ref viewport)));
            }
        }

        Cache Build(int tick, int? churnTick, Viewport view)
        {
            var started = Stopwatch.GetTimestamp();
            var peerWeights = Weights(Math.Max(2, view.Peers), (Math.Max(2, view.Peers) + 1) / 2, tick);
            var torrentWeights = Weights(Math.Max(10, view.Torrents), Math.Max(7, view.Torrents - 3), tick / 3);
            var snapshots = new byte[configuration.Torrents.Length][];
            var summaries = new Summary[configuration.Torrents.Length];
            for (var index = 0; index < configuration.Torrents.Length; index++)
            {
                var torrent = configuration.Torrents[index];
                var states = candidates[index].ToDictionary(entry => entry.Key, entry => StateOf(entry.Value));
                var weights = candidates[index].Where(entry => states[entry.Key] == "connected")
                    .ToDictionary(entry => entry.Key, entry => Weight(peerWeights, entry.Value.Rank, tick));
                var totalWeight = weights.Values.Sum();
                var desired = Weight(torrentWeights, Array.IndexOf(order, index), tick) * 6 * 1048576;
                var connected = 0; var seeds = 0; var connecting = 0; var handshaking = 0; var pending = 0;
                var down = 0d; var up = 0d;
                var buffer = new ArrayBufferWriter<byte>();
                using var writer = new Utf8JsonWriter(buffer);
                writer.WriteStartObject(); writer.WritePropertyName("peers"); writer.WriteStartArray();
                for (var row = 0; row < torrent.Rows; row++)
                {
                    var state = states.GetValueOrDefault(row, "known");
                    var seed = row < torrent.Seeds;
                    var rate = state == "connected" ? desired * weights[row] / totalWeight : 0;
                    var upload = seed ? 0 : rate / 4;
                    writer.WriteStartObject();
                    writer.WriteString("endpoint", Endpoint(index, row));
                    writer.WriteString("state", state); writer.WriteString("role", seed ? "seed" : "leecher");
                    writer.WritePropertyName("sources"); writer.WriteStartArray(); writer.WriteStringValue("tracker"); writer.WriteEndArray();
                    if (state != "known")
                    {
                        writer.WriteString("client", "UI benchmark"); writer.WriteString("transport", "tcp"); writer.WriteString("encryption", "plain");
                        writer.WriteBoolean("incoming", !seed); writer.WriteBoolean("interested", true); writer.WriteBoolean("choked", false);
                        writer.WriteBoolean("remote_interested", true); writer.WriteBoolean("remote_choked", false);
                        writer.WritePropertyName("read_wait"); writer.WriteStartArray(); writer.WriteEndArray();
                        writer.WritePropertyName("write_wait"); writer.WriteStartArray(); writer.WriteEndArray();
                        writer.WriteNumber("progress", seed ? 1d : .5); writer.WriteNumber("download_rate", rate); writer.WriteNumber("upload_rate", upload);
                        writer.WriteNumber("downloaded", 1048576L); writer.WriteNumber("uploaded", 524288L);
                    }
                    writer.WriteEndObject();
                    if (state == "connected") { connected++; if (seed) seeds++; down += rate; up += upload; }
                    if (state == "connecting") connecting++;
                    if (state == "handshake") handshaking++;
                    if (churnTick is { } age && candidates[index].TryGetValue(row, out var candidate) && candidate.Churn >= 0)
                    {
                        var elapsed = age - 1 - candidate.Churn / configuration.SwapsPerSecond;
                        if (elapsed >= 0 && elapsed % 4 == 3 && state == "connected") pending++;
                    }
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
                snapshots[index] = buffer.WrittenSpan.ToArray();
                summaries[index] = new(torrent.TorrentId, connected, seeds, connecting, handshaking, pending, down, up);
            }
            var produced = Stopwatch.GetTimestamp();
            measurements.Add(new { tick, churnTick, started, produced, generationMs = (produced - started) * 1000d / Stopwatch.Frequency,
                connected = summaries.Sum(torrent => torrent.Connected), connecting = summaries.Sum(torrent => torrent.Connecting),
                handshaking = summaries.Sum(torrent => torrent.Handshaking), pendingDisconnect = summaries.Sum(torrent => torrent.PendingDisconnect) });
            return new(snapshots, JsonSerializer.SerializeToUtf8Bytes(new Status(tick, produced, summaries)), tick);

            string StateOf(Candidate candidate)
            {
                if (candidate.Churn < 0) return "connected";
                var age = churnTick - 1 - candidate.Churn / configuration.SwapsPerSecond;
                if (age is null or < 0) return candidate.Bank == 0 ? "connected" : "known";
                if (candidate.Bank != (age.Value / 4 + 1) % 2) return "known";
                return (age.Value % 4) switch { 0 => "connecting", 1 => "handshake", _ => "connected" };
            }
        }

    }

    private static double Weight(double[] weights, int rank, int tick) =>
        (rank < weights.Length ? weights[rank] : .01) * (1 + .005 * Math.Sin(tick + rank));

    private static double[] Weights(int visible, int kept, int tick)
    {
        var rotating = visible - kept;
        var weights = Enumerable.Repeat(.01, kept + rotating * 2).ToArray();
        var members = Enumerable.Range(0, kept).Concat(Enumerable.Range(kept + tick % 2 * rotating, rotating)).ToArray();
        var random = new Random(1729 + tick);
        random.Shuffle(members);
        for (var rank = 0; rank < members.Length; rank++) weights[members[rank]] = 2 - (double)rank / visible;
        return weights;
    }
}
