using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Torrents;
using Windows.Graphics;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    // Replays the peer table's part of a torrent click through the real
    // inspector: the peers of one torrent replace another's, as the peer cache
    // shows them, and the table, its layout and the map follow. It records the
    // UI thread's CPU cycles, because wall time on the owner's laptop depends on
    // whatever else runs, and the busiest frame, which holds back input and
    // painting.
    private async Task CapturePeerSwitch(
        Inspector inspector,
        InspectorPane pane,
        IReadOnlyList<Peer> located,
        List<object> outcomes
    )
    {
        const int Count = 200;
        var table = (Syno.TableView.Table)pane.FindName("Peers");
        var property = typeof(Inspector).GetProperty(nameof(Inspector.Peers))!;
        var torrent = located[0].TorrentId;
        Peer[] TorrentPeers(int network) =>
            Enumerable
                .Range(0, Count)
                .Select(index => new Peer(
                    Model.Text,
                    torrent,
                    $"198.{network}.{index / 256}.{index % 256}:51413",
                    located[index % located.Count].Location,
                    ConnectedPeer(index)
                ))
                .ToArray();
        var first = TorrentPeers(18);
        var second = TorrentPeers(19);
        // A poll of the same torrent: five peers leave, five arrive.
        var polled = first.Where((_, index) => index % 40 != 3).Concat(TorrentPeers(20).Take(5)).ToArray();
        AppWindow.Resize(new SizeInt32(1600, 1000));
        pane.Layout = pane.Layout with { View = PeerView.Default };
        await CaptureLayout();

        var frames = new List<ulong>();
        EventHandler<object> rendering = (_, _) => frames.Add(CaptureCycles.Current());
        CompositionTarget.Rendering += rendering;
        try
        {
            var result = new Dictionary<string, object>
            {
                ["journey"] = "peer table switch cycles",
                ["peers"] = Count,
                ["idle"] = await Measure("idle", 6, _ => null),
                ["switch"] = await Measure("switch", 12, step => step % 2 == 0 ? second : first),
                ["poll"] = await Measure("poll", 12, step => step % 2 == 0 ? polled : first),
            };
            // The table alone, so the map's share of a switch shows apart.
            pane.Layout = pane.Layout with { View = PeerView.Default with { Layout = PeerLayout.Table } };
            await CaptureLayout();
            result["tableSwitch"] = await Measure("table switch", 12, step => step % 2 == 0 ? second : first);
            pane.Layout = pane.Layout with { View = PeerView.Default };
            await CaptureLayout();
            table.Layout = table.Layout with
            {
                SortColumnId = "down",
                SortDirection = Syno.TableView.SortDirection.Descending,
            };
            await CaptureLayout();
            result["sortedSwitch"] = await Measure("sorted switch", 12, step => step % 2 == 0 ? second : first);
            outcomes.Add(result);
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
        }

        // Each step shows its peers, then lets 1.2 s of frames pass, so the
        // count covers the change and all layout it causes. Two unmeasured
        // steps first realize the rows and warm the code.
        async Task<object> Measure(string name, int steps, Func<int, Peer[]?> peers)
        {
            var totals = new List<double>();
            var worst = new List<double>();
            for (var step = -2; step < steps; step++)
            {
                frames.Clear();
                var start = CaptureCycles.Current();
                var shown = peers(step + 2);
                if (shown is not null)
                {
                    property.SetValue(inspector, shown);
                    inspector.Refresh();
                }
                await Task.Delay(1200);
                var end = CaptureCycles.Current();
                if (step < 0)
                    continue;
                if (shown is not null && !ReferenceEquals(table.ItemsSource, inspector.ShownPeers))
                    throw new InvalidOperationException($"The {name} step did not reach the peer table.");
                var marks = frames.Prepend(start).Append(end).ToArray();
                totals.Add((end - start) / 1e6);
                worst.Add(marks.Zip(marks.Skip(1), (from, to) => (to - from) / 1e6).Max());
            }
            totals.Sort();
            worst.Sort();
            return new
            {
                medianMegacycles = totals[totals.Count / 2],
                medianWorstFrameMegacycles = worst[worst.Count / 2],
                megacycles = totals,
                worstFrameMegacycles = worst,
            };
        }
    }

    private static JsonElement ConnectedPeer(int index) =>
        JsonSerializer.SerializeToElement(new
        {
            client = $"Capture client {index % 17}.{index % 9}",
            transport = index % 4 == 0 ? "utp" : "tcp",
            incoming = index % 2 == 0,
            state = "connected",
            role = index % 3 == 0 ? "seed" : "leecher",
            encryption = index % 2 == 0 ? "rc4" : "plain",
            sources = new[] { index % 3 == 0 ? "dht" : "tracker" },
            interested = true,
            choked = index % 5 == 0,
            remote_interested = true,
            remote_choked = false,
            read_wait = Array.Empty<string>(),
            write_wait = Array.Empty<string>(),
            progress = index % 100 / 100.0,
            download_rate = index % 4 == 0 ? 0 : (index % 7 + 1) * 32768,
            upload_rate = index % 3 == 0 ? 0 : (index % 5 + 1) * 24576,
            downloaded = (index + 1) * 1048576L,
            uploaded = (index + 1) * 524288L,
        });
}
