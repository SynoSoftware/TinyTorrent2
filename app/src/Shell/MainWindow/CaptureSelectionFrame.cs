using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;
using Windows.Graphics;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    // Clicks between two torrents through the table's own click selection,
    // with the inspector closed and then open on Peers with 200 located peers
    // cached for each torrent. It records the UI thread's CPU cycles from the
    // click to the end of the first frame that renders, and over the 1.2 s that
    // follow. When the inspector's work stays out of the selection frame, that
    // frame costs the same whether the inspector is open or closed.
    private async Task CaptureSelectionFrame(List<object> outcomes)
    {
        const int Count = 200;
        await ShowTorrents();
        Model.Filter = TorrentFilter.All;
        var targets = Model.Torrents.Take(2).ToArray();
        if (targets.Length != 2)
            throw new InvalidOperationException("The selection frame review requires two torrents.");
        AppWindow.Resize(new SizeInt32(1600, 1000));
        Torrents.Selection = new Syno.TableView.Selection([targets[0]], targets[0]);
        await SelectTorrent();
        var inspector = Model.Inspector;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var cache = (List<(string TorrentId, IReadOnlyList<Peer> Peers)>)typeof(Inspector)
            .GetField("_peerCache", flags)!.GetValue(inspector)!;
        var locating = typeof(Inspector).GetField("_locating", flags)!;
        var click = typeof(Syno.TableView.Table).GetMethod("SelectItem", flags)!;

        var countries = await Geography.Load();
        string[] codes = ["US", "BR", "GB", "FR", "DE", "RU", "IN", "CN", "JP", "AU", "ZA", "SG"];
        Location Located(int index)
        {
            var code = codes[index % codes.Length];
            var country = countries[code];
            var area = country.Areas.Length == 0 ? null : country.Areas[index / codes.Length % country.Areas.Length];
            return new Location(
                code,
                new Place(code, new Dictionary<string, string> { ["en"] = new RegionInfo(code).EnglishName }),
                area is null ? null : new Place(null, new Dictionary<string, string> { ["en"] = area.Name }),
                area?.Point ?? country.Anchor
            );
        }
        Peer[] PeersOf(Torrent torrent, int network) =>
            Enumerable
                .Range(0, Count)
                .Select(index => new Peer(
                    Model.Text,
                    torrent.TorrentId,
                    $"198.{network}.{index / 256}.{index % 256}:51413",
                    Located(index),
                    ConnectedPeer(index)
                ))
                .ToArray();
        var peers = new[] { PeersOf(targets[0], 18), PeersOf(targets[1], 19) };

        // Each click finds its torrent's peers in the cache, as a return to a
        // recently shown torrent does. The engine's read then replaces them
        // with the paused fixture's empty list, so each click also clears the
        // table. Looking the synthetic addresses up would drop their places,
        // so lookups stay off.
        void Seed()
        {
            cache.RemoveAll(entry => targets.Any(target => target.TorrentId == entry.TorrentId));
            for (var index = 0; index < targets.Length; index++)
                cache.Insert(0, (targets[index].TorrentId, peers[index]));
        }

        var frames = new List<ulong>();
        EventHandler<object> rendering = (_, _) => frames.Add(CaptureCycles.Current());
        CompositionTarget.Rendering += rendering;
        locating.SetValue(inspector, true);
        var closed = new List<(double Frame, double Total, double Worst)>();
        var open = new List<(double Frame, double Total, double Worst)>();
        try
        {
            // Alternating closed and open passes keeps machine drift out of the
            // comparison.
            for (var pass = 0; pass < 2; pass++)
            {
                Model.CloseInspector();
                await CaptureLayout();
                closed.AddRange(await Measure(10, false));

                Run(Model.Properties);
                inspector.Select(InspectorSection.Peers);
                ((InspectorPane)InspectorContent.Content).Layout =
                    ((InspectorPane)InspectorContent.Content).Layout with { View = PeerView.Default };
                Seed();
                await CaptureReady(inspector, () => !inspector.IsLoading && inspector.Peers is not null);
                await CaptureLayout();
                open.AddRange(await Measure(10, true));
            }
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
            locating.SetValue(inspector, false);
        }
        outcomes.Add(new
        {
            journey = "selection frame cycles",
            peers = Count,
            closed = Summary(closed),
            peersOpen = Summary(open),
        });

        // Two unmeasured clicks first realize the rows and warm the code.
        async Task<List<(double Frame, double Total, double Worst)>> Measure(int clicks, bool inspecting)
        {
            var measured = new List<(double Frame, double Total, double Worst)>();
            for (var step = -2; step < clicks; step++)
            {
                await Task.Delay(1200);
                var target = Model.Current == targets[0] ? targets[1] : targets[0];
                Seed();
                ulong? painted = null;
                EventHandler<RenderedEventArgs> rendered = (_, _) => painted ??= CaptureCycles.Current();
                frames.Clear();
                var start = CaptureCycles.Current();
                CompositionTarget.Rendered += rendered;
                try
                {
                    click.Invoke(Torrents, [target, false, false]);
                    await Task.Delay(1200);
                }
                finally
                {
                    CompositionTarget.Rendered -= rendered;
                }
                var end = CaptureCycles.Current();
                if (Model.Current != target || inspecting && inspector.Target != target)
                    throw new InvalidOperationException("A measured click did not select its torrent.");
                if (painted is not { } frame)
                    throw new InvalidOperationException("No frame rendered after a measured click.");
                if (step < 0)
                    continue;
                var marks = frames.Prepend(start).Append(end).ToArray();
                measured.Add((
                    (frame - start) / 1e6,
                    (end - start) / 1e6,
                    marks.Zip(marks.Skip(1), (from, to) => (to - from) / 1e6).Max()
                ));
            }
            return measured;
        }

        static object Summary(List<(double Frame, double Total, double Worst)> clicks) => new
        {
            selectionFrameMegacycles = clicks.Select(click => click.Frame).Order().ToArray(),
            clickMegacycles = clicks.Select(click => click.Total).Order().ToArray(),
            worstFrameMegacycles = clicks.Select(click => click.Worst).Order().ToArray(),
        };
    }
}
