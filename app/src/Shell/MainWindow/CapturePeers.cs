using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Torrents;
using Windows.Graphics;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private static void CaptureRates(Peer peer, double download, double? upload = null)
    {
        var before = peer.Capture();
        peer.Apply(Peer.Prepare(before, before with { DownloadRate = download, UploadRate = upload ?? before.UploadRate }));
    }

    private static MapRenderer? CaptureRenderer(PeerMap map)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var motion = (MapMotion?)typeof(PeerMap).GetField("_motion", flags)!.GetValue(map);
        return motion?.Renderer;
    }

    private static bool CaptureMapIdle(PeerMap map)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        return typeof(PeerMap).GetField("_working", flags)!.GetValue(map) is null
            && ((MapMotion?)typeof(PeerMap).GetField("_motion", flags)!.GetValue(map))?.Ready.IsCompleted != false
            && new[] { "_frameQueued", "_preparationQueued" }
                .All(name => !(bool)typeof(PeerMap).GetField(name, flags)!.GetValue(map)!);
    }

    private async Task CaptureMapPerformance(List<object> outcomes)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Read<T>(object owner, string name) => (T)owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
        static async Task Until(Func<bool> ready)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(15))
                    throw new TimeoutException("The map performance fixture did not settle.");
                await Task.Delay(16);
            }
        }
        static object Summary(List<double> samples)
        {
            var ordered = samples.Order().ToArray();
            return new { count = ordered.Length, median = ordered[ordered.Length / 2],
                p95 = ordered[Math.Min(ordered.Length - 1, (int)(ordered.Length * .95))], max = ordered[^1] };
        }
        async Task Snapshot(string name)
        {
            var image = Path.Combine(_captureDirectory!, name + ".png");
            var snapshot = new ProcessStartInfo("winapp") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "ui", "screenshot", "--window",
                WinRT.Interop.WindowNative.GetWindowHandle(this).ToString(), "--output", image })
                snapshot.ArgumentList.Add(argument);
            using var capture = Process.Start(snapshot)!;
            try
            {
                await capture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                if (!capture.HasExited)
                {
                    capture.Kill(entireProcessTree: true);
                    await capture.WaitForExitAsync();
                }
            }
            if (capture.ExitCode != 0)
                throw new InvalidOperationException("The full-content map capture failed.");
            outcomes.Add(new { journey = "map full-content composition", image });
        }
        var origin = new Location("GB", new Place("GB", null), null, new GeoPoint(0, 51.5));
        Peer MakePeer(int index, double rate, double up = 0, GeoPoint? point = null) => new(Model.Text, "map-performance", $"192.0.2.{index}:51413",
            new Location("US", new Place("US", null), null,
                point ?? new GeoPoint(-125 + index % 100 * .6, 25 + index / 100 % 50 * .5)),
            JsonSerializer.SerializeToElement(new
            {
                state = "connected", role = "seed", client = "Map performance", transport = "tcp",
                encryption = "plain", sources = new[] { "tracker" }, incoming = false,
                interested = true, choked = false, remote_interested = false, remote_choked = false,
                read_wait = Array.Empty<string>(), write_wait = Array.Empty<string>(),
                progress = 1, download_rate = rate, upload_rate = up, downloaded = 0, uploaded = 0,
            }));
        var model = new MapModel(Model.Inspector);
        var peers = Enumerable.Range(0, 128).Select(index => MakePeer(index, 65536)).ToArray();
        model.Update(peers, origin, Model.Text, cities: true);
        var map = new PeerMap(model) { Width = 1000, Height = 650 };
        Grid.SetRow(map, 1);
        Root.Children.Add(map);
        try
        {
            await Until(() => CaptureRenderer(map) is not null && map.MotionFrames > 0);
            var renderer = CaptureRenderer(map)!;
            var viewport = Read<MapViewport>(map, "_viewport");
            var world = Read<Dictionary<string, Geography.Country>>(renderer, "_world");
            async Task Draw()
            {
                typeof(PeerMap).GetMethod("Invalidate", Flags)!.Invoke(map, null);
                Read<MapMotion>(map, "_motion").CaptureRedraw();
                await map.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            }
            var work = new Dictionary<string, List<double>>();
            void Record(string phase, double ms)
            {
                if (!work.TryGetValue(phase, out var samples))
                    work[phase] = samples = [];
                samples.Add(ms);
            }
            map.WorkMeasured = Record;
            async Task Settle()
            {
                await Until(() => CaptureMapIdle(map) && !Read<bool>(renderer, "_interpolating"));
            }
            if (ReviewMode == CaptureMode.MapArcs)
            {
                map.SetAnimation(MapAnimation.Off, MapDotTotal.Normal);
                viewport.Fit();
                work.Clear();
                var source = new GeoPoint(0, 10);
                origin = origin with { Point = source };
                model.Update(peers, origin, Model.Text, cities: true);
                await Settle();
                outcomes.Add(new { journey = "map overview arc construction", routes = 128,
                    phases = work.ToDictionary(pair => pair.Key, pair => Summary(pair.Value)) });
                foreach (var longitude in new[] { 179.5, 179.99 })
                {
                    var destination = new GeoPoint(longitude, 10);
                    var circle = new MapRenderer.GreatCircle(source, destination);
                    var crest = circle.At(.5);
                    viewport.Fit();
                    model.Update([MakePeer(0, 1048576, 0, destination)], origin, Model.Text, cities: true);
                    await Settle();
                    var route = Read<System.Collections.IEnumerable>(renderer, "_routes").Cast<object>().Single();
                    T Property<T>(string name) => (T)route.GetType().GetProperty(name, Flags)!.GetValue(route)!;
                    var overview = Property<float>("Degrees");
                    var maximum = viewport.Scale * MapViewport.MaxZoom;
                    int Misses(CanvasGeometry arc)
                    {
                        using var scaled = arc.Transform(Matrix3x2.CreateScale(maximum));
                        var misses = 0;
                        for (var sample = 1; sample < 8192; sample++)
                            if (!scaled.StrokeContainsPoint(viewport.Coordinates(circle.At(sample / 8192.0)) * maximum, 1))
                                misses++;
                        return misses;
                    }
                    var coarseMisses = Misses(Property<CanvasGeometry>("Arc"));
                    work.Clear();
                    viewport.SetView(MapViewport.MaxZoom, viewport.Coordinates(crest),
                        viewport.CalculateRatio(MapViewport.MaxZoom, (float)-crest.Latitude));
                    var started = Stopwatch.GetTimestamp();
                    await Draw();
                    var pictureMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    await Settle();
                    var arc = Property<CanvasGeometry>("Arc");
                    var misses = Misses(arc);
                    if (misses != 0 || Property<float>("Degrees") != overview)
                        throw new InvalidOperationException("A polar guide missed the analytical dot path or zoom changed trip timing.");
                    map.SetAnimation(MapAnimation.Fast, MapDotTotal.High);
                    await Settle();
                    var frames = map.MotionFrames;
                    await Until(() => map.MotionFrames > frames + 3);
                    await Snapshot("map-polar-" + longitude.ToString("0.##", CultureInfo.InvariantCulture));
                    viewport.SetView(MapViewport.MaxZoom, new Vector2(-179.8f, -10),
                        viewport.CalculateRatio(MapViewport.MaxZoom, -10));
                    await Draw();
                    await Settle();
                    if (!ReferenceEquals(arc, Property<CanvasGeometry>("Arc")))
                        throw new InvalidOperationException("Panning unnecessarily replaced a refined guide.");
                    await Snapshot("map-wrapped-" + longitude.ToString("0.##", CultureInfo.InvariantCulture));
                    viewport.Fit();
                    await Draw();
                    if (!ReferenceEquals(arc, Property<CanvasGeometry>("Arc")))
                        throw new InvalidOperationException("Zooming out unnecessarily replaced a refined guide.");
                    outcomes.Add(new { journey = "map polar guide alignment", longitude, samples = 8191,
                        toleranceDips = .5, coarseMisses, misses, pictureMs,
                        phases = work.ToDictionary(pair => pair.Key, pair => Summary(pair.Value)) });
                    map.SetAnimation(MapAnimation.Off, MapDotTotal.Normal);
                }
                return;
            }
            foreach (var scenario in new[] { "fixed", "canada", "norway", "canada-fast", "canada-natural", "canada-interrupted" })
            {
                var canada = scenario.StartsWith("canada", StringComparison.Ordinal);
                var interrupted = scenario == "canada-interrupted";
                var ratio = scenario == "canada-natural" ? viewport.CalculateRatio(32, -70) : 1;
                viewport.SetView(scenario == "fixed" ? 1 : canada && !interrupted ? 32 : 128,
                    scenario == "fixed" ? Vector2.Zero : canada ? new Vector2(-100, -70) : new Vector2(20, -70), ratio);
                work.Clear();
                List<double> coldFrames = [];
                map.FrameMeasured = coldFrames.Add;
                var cold = Stopwatch.GetTimestamp();
                await Draw();
                var coldMs = Stopwatch.GetElapsedTime(cold).TotalMilliseconds;
                if (interrupted)
                    for (var step = 0; step < 6; step++)
                    {
                        viewport.SetView(viewport.Zoom, viewport.Center + new Vector2(.25f, 0), ratio);
                        await Draw();
                        await Task.Delay(16);
                    }
                await Settle();
                map.FrameMeasured = null;
                outcomes.Add(new { journey = "map detail transition", scenario, firstPictureMs = coldMs,
                    frames = Summary(coldFrames),
                    phases = work.ToDictionary(pair => pair.Key, pair => Summary(pair.Value)) });
                work.Clear();
                List<double> frames = [];
                for (var frame = 0; frame < 48; frame++)
                {
                    if (scenario != "fixed")
                        viewport.SetView(viewport.Zoom, viewport.Center + new Vector2(scenario == "canada-fast" ? .25f : .025f, 0), ratio);
                    var started = Stopwatch.GetTimestamp();
                    await Draw();
                    frames.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    await Task.Delay(16);
                }
                outcomes.Add(new { journey = "map picture phases", scenario, frames = Summary(frames),
                    phases = work.ToDictionary(pair => pair.Key, pair => Summary(pair.Value)) });
                await Settle();
                if (scenario != "fixed")
                    await Snapshot("map-" + scenario);
            }
            viewport.Fit();
            await Draw();
            GeoPoint[] places = [new(-118, 34), new(151, -33), new(139, 35), new(13, 52),
                new(77, 28), new(-43, -22), new(-74, 40), new(28, -26), new(37, 56), new(-79, 9)];
            model.Update(places.Select((point, index) => MakePeer(index, index % 2 == 0 ? 131072 : 16384,
                index % 2 == 0 ? 16384 : 131072, point)).ToArray(), origin, Model.Text, cities: true);
            model.ResetPeak();
            map.SetAnimation(MapAnimation.Fast, MapDotTotal.High);
            await Settle();
            var visibleFrames = map.MotionFrames;
            await Until(() => map.MotionFrames > visibleFrames + 3);
            await Snapshot("map-world-routes");
            map.SetAnimation(MapAnimation.Normal, MapDotTotal.Normal);
            if (ReviewMode == CaptureMode.MapGeography)
                return;
            var crowded = Enumerable.Range(0, 300).Select(index => MakePeer(index, (index + 1) * 1024)).ToArray();
            model.Update(crowded, origin, Model.Text, cities: true);
            map.SetArcLimit(25);
            await Settle();
            MapModel.Destination[] Shown() => Read<System.Collections.IEnumerable>(renderer, "_routes")
                .Cast<object>().Where(route => route.GetType().GetProperty("Arc", Flags)!.GetValue(route) is not null)
                .Select(route => (MapModel.Destination)route.GetType().GetProperty("Destination", Flags)!.GetValue(route)!)
                .ToArray();
            var shown = Shown();
            if (model.Destinations.Count != 300 || shown.Length != 25 || shown.Min(destination => destination.Group.Down) != 276 * 1024
                || Read<MapMotion.Traffic>(renderer, "_traffic").Routes.Count != 25)
                throw new InvalidOperationException("The arc cap lost markers or failed to retain the fastest routes and matching dots.");
            crowded[0].Update(MakePeer(0, 0, 1048576));
            model.Update(crowded, origin, Model.Text, cities: true);
            await Settle();
            if (!Shown().Any(destination => destination.Group.Up == 1048576)
                || Shown().Where(destination => destination.Group.Down > 0).Min(destination => destination.Group.Down) != 277 * 1024)
                throw new InvalidOperationException("The fastest-arc selection did not follow a rate change.");
            map.SetArcLimit(MapRenderer.DefaultLimit);
            await Settle();
            if (Shown().Length != Math.Min(crowded.Length, MapRenderer.DefaultLimit))
                throw new InvalidOperationException("Changing the arc cap did not apply without reopening the map.");
            outcomes.Add(new { journey = "map fastest arc cap", peers = 300, limited = 25,
                markers = model.Destinations.Count, changedLimit = Shown().Length });
            viewport.Fit();
            await Draw();
            await Settle();
            await Snapshot("map-fastest-arcs");
            viewport.SetView(1, Vector2.Zero, 1);
            await Draw();
            await Settle();
            foreach (var count in new[] { 1000, 10000 })
            {
                var population = Enumerable.Range(0, count).Select(index => MakePeer(index, 65536)).ToArray();
                model.Update(population, null, Model.Text, cities: true);
                await Settle();
                work.Clear();
                for (var sample = 0; sample < 8; sample++)
                {
                    for (var index = 0; index < population.Length; index++)
                        population[index].Update(MakePeer(index, 65536 + sample * 1024));
                    model.Update(population, null, Model.Text, cities: true);
                    await Settle();
                }
                outcomes.Add(new { journey = "map rate preparation", count,
                    phases = work.ToDictionary(pair => pair.Key, pair => Summary(pair.Value)) });
            }
            model.Update(peers, origin, Model.Text, cities: true);
            model.ResetPeak();
            map.SetAnimation(MapAnimation.Fast, MapDotTotal.High);
            await Settle();
            foreach (var hidden in new[] { false, true, false })
            {
                viewport.SetView(hidden ? 128 : 1, hidden ? new Vector2(120, 60) : Vector2.Zero, 1);
                await Draw();
                await Settle();
                if (!hidden)
                {
                    var ready = map.MotionFrames;
                    await Until(() => map.MotionFrames > ready + 3);
                }
                var cycles = map.MotionCycles;
                var frames = map.MotionFrames;
                var watch = Stopwatch.StartNew();
                await Task.Delay(1000);
                outcomes.Add(new { journey = "map motion interval", hidden, elapsedMs = watch.Elapsed.TotalMilliseconds,
                    megacycles = (map.MotionCycles - cycles) / 1e6, frames = map.MotionFrames - frames, map.MotionPaused,
                    state = map.MotionState });
            }
        }
        finally
        {
            Root.Children.Remove(map);
        }
    }

    private async Task CaptureMap(List<object> outcomes)
    {
        await CapturePeerPreparation(outcomes);
        var view = new MapView(new Windows.Foundation.Size(100, 100), 0, Vector2.Zero,
            Vector2.One, Matrix3x2.CreateTranslation(50, 50));
        if (!view.Shows(new Windows.Foundation.Rect(-100, -1, 200, 2), 13)
            || !view.Shows(new Windows.Foundation.Rect(350, -1, 10, 2), 13)
            || view.Shows(new Windows.Foundation.Rect(100, 100, 10, 10), 13))
            throw new InvalidOperationException("Route bounds lost a crossing, wrapped turn or offscreen rejection.");
        var polar = new MapRenderer.GreatCircle(new GeoPoint(0, 10), new GeoPoint(179.5, 10));
        if (polar.Bounds(new Windows.Foundation.Rect(0, -88.46362, 180, 78.46362)).Top > -88.58)
            throw new InvalidOperationException("Analytical polar motion escaped the sampled route bounds.");
        var hidden = new MapDots<object>(1024);
        var hiddenRoute = new object();
        hidden.Update([new(hiddenRoute, 1048576, 0, 320)], 400000, 1048576, .5, 1, 0);
        hidden.Update([new(hiddenRoute, 1, 0, 320)], 400000, 1048576, .5, 1, Stopwatch.Frequency);
        if (hidden.Wait(departures: false) != TimeSpan.FromSeconds(10))
            throw new InvalidOperationException("Hidden traffic lost the size-change deadline.");
        hidden.Advance(20 * Stopwatch.Frequency);
        if (hidden.Bytes != 1024 || hidden.Flying.Count != 0 || hidden.Wait(departures: false) is not null)
            throw new InvalidOperationException("Hidden traffic did not settle its size or replayed expired dots.");
        hidden.Update([new(hiddenRoute, 1048576, 1048576, 320)], 10000, 1048576, .5, 1, 20 * Stopwatch.Frequency);
        hidden.Advance(80 * Stopwatch.Frequency);
        if (hidden.Flying.Count > 1 || hidden.Flying.Any(dot => dot.Progress(80 * Stopwatch.Frequency) >= 1))
            throw new InvalidOperationException("A batched hidden interval exceeded the cap or replayed an expired dot.");
        outcomes.Add(new { journey = "map hidden accounting and bounds", size = hidden.Bytes, cap = 1 });

        var route = new object();
        var dots = new MapDots<object>(1024);
        dots.Update([new(route, 65536, 0, 320)], 400000, 1048576, .5, 1, 0);
        dots.Advance(Stopwatch.Frequency / 2);
        if (dots.Flying.Count == 0)
            throw new InvalidOperationException("The suspension fixture did not start download dots.");
        var bytes = dots.Bytes;
        dots.Clear();
        dots.Update([new(route, 0, 65536, 320)], 400000, 1048576, .5, 1, 20 * Stopwatch.Frequency);
        if (dots.Flying.Count != 0 || dots.Bytes != bytes)
            throw new InvalidOperationException("Resuming counted disabled traffic or lost the last dot size.");
        dots.Advance(21 * Stopwatch.Frequency);
        if (dots.Flying.Count == 0 || dots.Flying.Any(dot => !dot.Upload))
            throw new InvalidOperationException("Resumed traffic did not use only the current upload rate.");
        outcomes.Add(new { journey = "map suspension accounting", disabledSeconds = 20, staleDots = 0 });

        if (!MapRenderer.SystemAnimates)
            throw new InvalidOperationException("The map lifecycle check needs Windows animations on and High Contrast off.");
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Read<T>(object owner, string name) => (T)owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
        static async Task Until(Func<bool> ready, string failure)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException(failure);
                await Task.Delay(16);
            }
        }
        var owner = Model.Inspector;
        var originalPlace = owner.Place;
        var location = new Location("US", new Place("US", null), null, null);
        var origin = new Location("GB", new Place("GB", null), null, new GeoPoint(0, 51.5));
        Peer MakePeer(double down, string role = "seed", double up = 0, string torrent = "map-check") => new(Model.Text, torrent, "192.0.2.1:51413", location,
            JsonSerializer.SerializeToElement(new
            {
                state = "connected", role, client = "Map check", transport = "tcp",
                encryption = "plain", sources = new[] { "tracker" }, incoming = false,
                interested = true, choked = false, remote_interested = false, remote_choked = false,
                read_wait = Array.Empty<string>(), write_wait = Array.Empty<string>(),
                progress = 1, download_rate = down, upload_rate = up, downloaded = 0, uploaded = 0,
            }));
        try
        {
            foreach (var paused in new[] { false, true })
            {
                owner.Place = null;
                var model = new MapModel(owner);
                var peer = MakePeer(65536);
                model.Update([peer], origin, Model.Text, cities: false);
                var map = new PeerMap(model) { Width = 640, Height = 400 };
                Grid.SetRow(map, 1);
                Root.Children.Add(map);
                try
                {
                    await Until(() => CaptureRenderer(map) is not null && map.MotionFrames > 0,
                        "The map did not load and draw moving dots.");
                    var renderer = CaptureRenderer(map)!;
                    var motion = Read<MapMotion>(map, "_motion");
                    await Until(() => CaptureMapIdle(map) && Read<object?>(motion, "_pending") is null
                        && !Read<bool>(renderer, "_interpolating"), "The initial map did not settle.");
                    var pictures = map.MotionPictures;
                    model.Update([MakePeer(65536, torrent: "map-replacement")], origin, Model.Text, cities: false);
                    await Until(() => CaptureMapIdle(map), "An equivalent torrent switch did not prepare.");
                    if (map.MotionPictures != pictures || Read<object?>(motion, "_pending") is not null)
                        throw new InvalidOperationException("Equivalent peer facts redrew the same map.");
                    model.Update([peer], origin, Model.Text, cities: false);
                    await Until(() => CaptureMapIdle(map), "The original observations did not prepare.");
                    outcomes.Add(new { journey = "map equivalent torrent switch", redrawn = false });
                    if (!paused)
                    {
                        var chosen = model.Destinations[0];
                        map.PlaceChanged += (_, place) => owner.Place = place;
                        typeof(PeerMap).GetMethod("Choose", Flags)!.Invoke(map, [chosen]);
                        var viewport = Read<MapViewport>(map, "_viewport");
                        viewport.SetView(8, new Vector2(10, -50), 1);
                        peer.Update(MakePeer(2097152));
                        model.Update([peer], origin, Model.Text, cities: false);
                        await Until(() => CaptureMapIdle(map) && !Read<bool>(renderer, "_interpolating"),
                            "The map's first offscreen activity fade did not settle.");
                        if (map.Described is not { } described || described.Key != chosen.Key
                            || described.Group.Down != 2097152 || !model.Destinations.Contains(described))
                            throw new InvalidOperationException("The chosen destination retained its previous group or speed.");
                        if (chosen.Group.Down != 65536)
                            throw new InvalidOperationException("Worker aggregation mutated a published inspection snapshot.");
                        peer.Update(MakePeer(2097153));
                        model.Update([peer], origin, Model.Text, cities: false);
                        await Until(() => CaptureMapIdle(map), "The unchanged map preparation did not finish.");
                        if (Read<bool>(renderer, "_interpolating"))
                            throw new InvalidOperationException("An offscreen country's old strength restarted an activity fade.");
                        outcomes.Add(new { journey = "map live selection and offscreen fade", selectedDown = 2097152, repeatedFade = false });

                        viewport.SetView(128, new Vector2(120, 60), 1);
                        await Until(() => motion.Control.Paused && CaptureMapIdle(map),
                            "The all-routes-hidden view did not pause.");
                        var revision = Read<long>(motion, "_revision");
                        var hiddenFrames = map.MotionFrames;
                        await Task.Delay(150);
                        if (map.MotionFrames != hiddenFrames)
                            throw new InvalidOperationException("The hidden view continued presenting dots.");
                        viewport.Fit();
                        typeof(PeerMap).GetMethod("Invalidate", Flags)!.Invoke(map, null);
                        await map.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                        typeof(MapMotion).GetMethod("Sleep", Flags)!.Invoke(motion,
                            [revision, null, Stopwatch.GetTimestamp()]);
                        if (motion.Control.Paused)
                            throw new InvalidOperationException("A stale sleep paused a newer visible picture.");
                        await Until(() => map.MotionFrames > hiddenFrames, "Pan-back did not resume moving dots.");
                        outcomes.Add(new { journey = "map hidden sleep and pan-back", staleSleepRejected = true });

                        var retained = Read<MapModel.Presentation>(renderer, "_model").Destinations[0];
                        var retainedRate = retained.Group.Down;
                        var retainedRoute = Read<System.Collections.IEnumerable>(renderer, "_routes").Cast<object>().Single();
                        var arrangement = Read<int>(renderer, "_arrangement");
                        peer.Update(MakePeer(0, "leecher", 65536));
                        model.Update([peer], origin, Model.Text, cities: false);
                        await Until(() => CaptureMapIdle(map), "The role and direction update did not prepare.");
                        var updated = Read<MapModel.Presentation>(renderer, "_model").Destinations[0];
                        if (updated.Key != retained.Key || retained.Group.Down != retainedRate
                            || !ReferenceEquals(retainedRoute, Read<System.Collections.IEnumerable>(renderer, "_routes").Cast<object>().Single())
                            || updated.Group.Seeds != 0 || updated.Group.Leechers != 1 || updated.Group.Down != 0 || updated.Group.Up != 65536
                            || Read<int>(renderer, "_arrangement") == arrangement)
                            throw new InvalidOperationException("Retained membership lost a role, rate or direction change.");
                        peer.Update(MakePeer(65536));
                        model.Update([peer], origin, Model.Text, cities: false);
                        await Until(() => CaptureMapIdle(map) && !Read<bool>(renderer, "_interpolating"),
                            "The fade fixture did not settle.");
                        var strengths = Read<Dictionary<MapModel.DestinationKey, float>>(renderer, "_displayed");
                        var before = strengths[retained.Key];
                        for (var sample = 1; sample <= 4; sample++)
                        {
                            peer.Update(MakePeer(65536 + sample * 2500));
                            model.Update([peer], origin, Model.Text, cities: false);
                            await Task.Delay(400);
                        }
                        if (strengths[retained.Key] <= before)
                            throw new InvalidOperationException("Live rate updates postponed every fade step.");
                        outcomes.Add(new { journey = "map retained aggregation and live fade", reusedDestination = true, progressedDuringUpdates = true });

                        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        map.DotBytesChanged += (_, _) =>
                        {
                            if (!DispatcherQueue.HasThreadAccess)
                                notified.TrySetException(new InvalidOperationException("Map preferences were delivered off the UI thread."));
                            else
                                notified.TrySetResult();
                        };
                        SendMessageW(WinRT.Interop.WindowNative.GetWindowHandle(this), 0x001A, 0, 0);
                        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await Until(() => CaptureMapIdle(map), "The preference notification did not finish preparing the map.");
                        outcomes.Add(new { journey = "map preference notification", uiThread = true, scope = "Notification dispatch with unchanged observations; Windows preferences unchanged." });
                    }
                    if (paused)
                    {
                        map.SetAnimation(MapAnimation.Off, MapDotTotal.Normal);
                        await Until(() => motion.Control.Paused && CaptureMapIdle(map),
                            "Animation Off did not pause the motion control.");
                    }
                    List<CanvasRenderTarget> resources = [];
                    await motion.Control.RunOnGameLoopThreadAsync(() =>
                    {
                        foreach (var name in new[] { "_shown", "_pending" })
                            if (Read<object?>(motion, name) is { } picture)
                                resources.Add((CanvasRenderTarget)picture.GetType().GetProperty("Bitmap", Flags)!.GetValue(picture)!);
                        foreach (var picture in Read<System.Collections.IEnumerable>(motion, "_free"))
                            resources.Add((CanvasRenderTarget)picture.GetType().GetProperty("Bitmap", Flags)!.GetValue(picture)!);
                        resources.Add(Read<CanvasRenderTarget>(motion, "_mark"));
                    });
                    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    motion.Control.GameLoopStopped += (_, _) => stopped.TrySetResult();
                    var retiring = Read<MapWork>(map, "_work");
                    var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var release = new ManualResetEventSlim();
                    retiring.Started = () =>
                    {
                        if (DispatcherQueue.HasThreadAccess)
                            throw new InvalidOperationException("Map preparation ran on the UI thread.");
                        preparing.TrySetResult();
                        if (!release.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("The map fixture did not release its worker.");
                    };
                    try
                    {
                        model.Update([MakePeer(1048576)], origin, Model.Text, cities: false);
                        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Root.Children.Remove(map);
                    }
                    finally
                    {
                        release.Set();
                        retiring.Started = null;
                    }
                    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await motion.Released.WaitAsync(TimeSpan.FromSeconds(10));
                    await retiring.Released.WaitAsync(TimeSpan.FromSeconds(10));
                    foreach (var resource in resources.Distinct())
                    {
                        try
                        {
                            using var drawing = resource.CreateDrawingSession();
                        }
                        catch (Exception error) when (error is ObjectDisposedException || error.HResult == unchecked((int)0x80000013))
                        {
                            continue;
                        }
                        throw new InvalidOperationException("An unloaded map left an owned bitmap open.");
                    }
                    outcomes.Add(new { journey = "map resource disposal", paused, closedResources = resources.Distinct().Count() });
                }
                finally
                {
                    Root.Children.Remove(map);
                }
            }
        }
        finally
        {
            owner.Place = originalPlace;
        }
    }

    private async Task CapturePeerPreparation(List<object> outcomes)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var inspector = Model.Inspector;
        Model.OpenInspector();
        inspector.Show(null, InspectorSection.Peers, allPeers: true);
        Model.SetInspector(open: true, compact: false);
        await CaptureLayout();
        var pane = (InspectorPane)InspectorContent.Content;
        // Table only deliberately clears the map's place during pane refresh.
        pane.Layout = pane.Layout with { View = PeerView.Default with { Layout = PeerLayout.Both, Cities = false } };
        await CaptureReady(inspector, () => inspector.IsActive && inspector.IsVisible
            && inspector.IsAvailable && !inspector.IsLoading);
        var invalidate = typeof(Inspector).GetMethod("Invalidate", Flags)!;
        var fetching = typeof(Inspector).GetField("_fetching", Flags)!;
        var context = typeof(Inspector).GetField("_context", Flags)!;
        var apply = typeof(Inspector).GetMethod("ApplyPeers", Flags)!;
        void StopReading()
        {
            invalidate.Invoke(inspector, null);
            fetching.SetValue(inspector, true);
        }
        Task<bool> Apply(JsonElement rows) => (Task<bool>)apply.Invoke(inspector,
            ["peer-check", rows, context.GetValue(inspector)!])!;
        StopReading();
        typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, null);
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            inspector.PreparingPeers = () =>
            {
                if (DispatcherQueue.HasThreadAccess)
                    throw new InvalidOperationException("Peer preparation ran on the UI thread.");
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The peer fixture did not release its worker.");
            };
            Task<bool> stale;
            try
            {
                stale = Apply(PeerRows(3));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                StopReading();
                if (stale.IsCompleted)
                    throw new InvalidOperationException("Peer preparation did not remain on its blocked worker.");
            }
            finally
            {
                release.Set();
                inspector.PreparingPeers = null;
            }
            if (await stale || inspector.Peers is { Count: > 0 })
                throw new InvalidOperationException("Cancelled peer preparation published stale rows.");
            await Apply(PeerRows(3));
            var peers = inspector.Peers;
            if (peers is not { Count: 3 })
                throw new InvalidOperationException("The current peer fixture was replaced before its rows were applied.");
            var before = peers[0].Capture();
            peers[0].Update(new Peer(Model.Text, "peer-check", peers[0].Endpoint, null,
                JsonSerializer.SerializeToElement(new { state = "known", sources = new[] { "dht" } })));
            var captured = peers[0].Capture();
            var notified = false;
            peers[0].PropertyChanged += (_, _) =>
            {
                if (!DispatcherQueue.HasThreadAccess)
                    throw new InvalidOperationException("Peer notifications ran off the UI thread.");
                notified = true;
            };
            await Apply(PeerRows(3));
            if (!ReferenceEquals(inspector.Peers, peers) || !ReferenceEquals(inspector.Peers[0], peers[0])
                || captured.State != PeerState.Known || peers[0].State != before.State || !notified)
                throw new InvalidOperationException("Prepared peer changes lost row identity or mutated a captured record.");
            var locating = typeof(Inspector).GetField("_locating", Flags)!;
            var lookup = Stopwatch.StartNew();
            while ((bool)locating.GetValue(inspector)!)
            {
                if (lookup.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("The peer fixture's location lookup did not finish.");
                await Task.Delay(16);
            }
            peers[0].Location = new Location("US", new Place("US", null), null, null);
            peers[1].Location = new Location("JP", new Place("JP", null), null, null);
            inspector.Place = PeerPlace.Of(peers[0].Location!, false);
            inspector.Place = PeerPlace.Of(peers[1].Location!, false);
            await inspector.PeersReady.WaitAsync(TimeSpan.FromSeconds(5));
            if (inspector.ShownPeers is not { Count: 1 } shown || !ReferenceEquals(shown[0], peers[1]))
                throw new InvalidOperationException("A superseded place filter published its rows.");
            inspector.Place = null;
            await inspector.PeersReady.WaitAsync(TimeSpan.FromSeconds(5));
            if (!ReferenceEquals(inspector.ShownPeers, peers))
                throw new InvalidOperationException("Clearing the place did not restore the current peer source.");
            outcomes.Add(new { journey = "peer worker cancellation and latest filter", rowIdentity = true,
                immutableRecords = true, notificationsOnUi = true, staleRowsDiscarded = true });
        }
        finally
        {
            inspector.PreparingPeers = null;
            inspector.Place = null;
            fetching.SetValue(inspector, false);
            Model.CloseInspector();
        }
    }

    private async Task CaptureSelection(List<object> outcomes)
    {
        await ShowTorrents();
        Model.Filter = TorrentFilter.All;
        var targets = Model.Torrents.Take(2).ToArray();
        if (targets.Length != 2)
            throw new InvalidOperationException("Selection review requires two torrents.");
        Torrents.Selection = new Syno.TableView.Selection([targets[0]], targets[0]);
        Run(Model.Properties);
        var inspector = Model.Inspector;
        await CaptureLayout();
        var preparingSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparedInBackground = false;
        var preparation = Torrents.PrepareSelection(async (_, cancellation) =>
        {
            preparedInBackground = !DispatcherQueue.HasThreadAccess;
            preparingSelection.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        });
        await preparingSelection.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Torrents.Selection = new Syno.TableView.Selection([targets[1]], targets[1]);
        try
        {
            await preparation.WaitAsync(TimeSpan.FromSeconds(20));
            throw new InvalidOperationException("Changing selection did not cancel its preparation.");
        }
        catch (OperationCanceledException)
        {
        }
        if (!preparedInBackground || Model.Current != targets[1])
            throw new InvalidOperationException("Background preparation blocked immediate selection.");
        outcomes.Add(new { journey = "nonblocking TableView preparation", workerThread = true, cancelledOnSelection = true });
        Torrents.Selection = new Syno.TableView.Selection([targets[0]], targets[0]);
        foreach (var section in new[]
        {
            InspectorSection.General, InspectorSection.Files, InspectorSection.Trackers, InspectorSection.Pieces,
            InspectorSection.Speed, InspectorSection.Peers,
        })
        {
            inspector.Select(section);
            await CaptureReady(inspector, () => !inspector.IsLoading && !inspector.HasError);
            await CaptureLayout();
            var notifications = 0;
            var windowNotifications = 0;
            System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => notifications++;
            System.ComponentModel.PropertyChangedEventHandler windowChanged = (_, _) => windowNotifications++;
            inspector.PropertyChanged += changed;
            Model.PropertyChanged += windowChanged;
            var times = new List<double>();
            try
            {
                for (var index = 0; index < 20; index++)
                {
                    var target = targets[(index + 1) % targets.Length];
                    var clock = Stopwatch.StartNew();
                    Torrents.Selection = new Syno.TableView.Selection([target], target);
                    times.Add(clock.Elapsed.TotalMilliseconds);
                    if (Model.Current != target)
                        throw new InvalidOperationException("Selection did not accept the latest row immediately.");
                    if (notifications != 0 || windowNotifications != 0)
                        throw new InvalidOperationException("Selection ran inspector or window bindings inline.");
                }
                await CaptureReady(inspector, () => !inspector.IsLoading && !inspector.HasError);
                await CaptureLayout();
                if (inspector.Target != targets[0] || notifications == 0)
                    throw new InvalidOperationException("Deferred inspector refresh lost the final selection.");
                outcomes.Add(new
                {
                    journey = "selection before inspector refresh",
                    section = section.ToString(),
                    selections = times.Count,
                    maximumMilliseconds = times.Max(),
                    averageMilliseconds = times.Average(),
                    notifications,
                });
            }
            finally
            {
                inspector.PropertyChanged -= changed;
                Model.PropertyChanged -= windowChanged;
            }
        }

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var invalidate = typeof(Inspector).GetMethod("Invalidate", flags)!;
        var context = typeof(Inspector).GetField("_context", flags)!;
        var fetching = typeof(Inspector).GetField("_fetching", flags)!;
        var apply = typeof(Inspector).GetMethod("ApplyPeers", flags)!;
        invalidate.Invoke(inspector, null);
        fetching.SetValue(inspector, true);
        var rows = PeerRows(10000);
        try
        {
            var cold = Stopwatch.StartNew();
            var pane = (InspectorPane)InspectorContent.Content;
            var table = (Syno.TableView.Table)pane.FindName("Peers");
            var firstBatch = 0;
            Task<bool>? preparing = null;
            System.ComponentModel.PropertyChangedEventHandler published = (_, _) =>
            {
                if (firstBatch != 0 || inspector.Peers is not { Count: > 0 } ready)
                    return;
                firstBatch = ready.Count;
            };
            inspector.PropertyChanged += published;
            try
            {
                preparing = (Task<bool>)apply.Invoke(inspector,
                    [targets[0].TorrentId, rows, context.GetValue(inspector)!])!;
                await preparing;
                cold.Stop();
                inspector.Refresh();
                await CaptureReady(inspector, () => ReferenceEquals(table.ItemsSource, inspector.ShownPeers));
                await CaptureLayout();
            }
            finally
            {
                inspector.PropertyChanged -= published;
            }
            var coldMilliseconds = cold.Elapsed.TotalMilliseconds;
            if (firstBatch != 10000 || inspector.Peers is not { Count: 10000 } peers)
                throw new InvalidOperationException("Background peer preparation lost rows.");
            var first = peers[0];
            first.Update(new Peer(Model.Text, targets[0].TorrentId, first.Endpoint, null,
                JsonSerializer.SerializeToElement(new { state = "known", role = "leecher", sources = new[] { "dht" } })));
            await (Task<bool>)apply.Invoke(inspector,
                [targets[0].TorrentId, rows, context.GetValue(inspector)!])!;
            if (!ReferenceEquals(inspector.Peers![0], first) || first.Role != PeerRole.Seed)
                throw new InvalidOperationException("Background refresh replaced a live peer or lost its update.");
            inspector.Refresh();
            await CaptureReady(inspector, () => ReferenceEquals(table.ItemsSource, inspector.ShownPeers));
            await CaptureLayout();
            var displayed = table.ItemsSource;
            var heading = ((TextBlock)pane.FindName("HeadingName")).Text;

            first.Update(new Peer(Model.Text, targets[0].TorrentId, first.Endpoint, null,
                JsonSerializer.SerializeToElement(new { state = "known", role = "leecher", sources = new[] { "dht" } })));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var cancellation = ((CancellationTokenSource)typeof(Inspector).GetField("_reading", flags)!
                .GetValue(inspector)!).Token;
            inspector.PreparingPeers = () =>
            {
                if (DispatcherQueue.HasThreadAccess)
                    throw new InvalidOperationException("Peer preparation ran on the UI thread.");
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The peer preparation fixture did not release its worker.");
            };
            Task<bool> applying;
            try
            {
                applying = (Task<bool>)apply.Invoke(inspector,
                    [targets[0].TorrentId, rows, context.GetValue(inspector)!])!;
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Torrents.Selection = new Syno.TableView.Selection([targets[1]], targets[1]);
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = new FrameCallback(DispatcherQueue, () =>
                {
                    invalidate.Invoke(inspector, null);
                    fetching.SetValue(inspector, true);
                    changed.TrySetResult();
                });
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (applying.IsCompleted || Model.Current != targets[1] || inspector.Target != targets[1]
                    || !cancellation.IsCancellationRequested)
                    throw new InvalidOperationException("Selection waited for background peer preparation.");
            }
            finally
            {
                release.Set();
                inspector.PreparingPeers = null;
            }
            await applying;
            if (inspector.Peers is { } stale && stale.Any(peer => peer.TorrentId == targets[0].TorrentId))
                throw new InvalidOperationException("A stale background reply replaced the new selection.");
            await CaptureLayout();
            if (!ReferenceEquals(table.ItemsSource, displayed)
                || ((TextBlock)pane.FindName("HeadingName")).Text != heading)
                throw new InvalidOperationException("Pending inspector work cleared the displayed detail.");
            await CaptureUi("selection-peers-waiting");
            await (Task<bool>)apply.Invoke(inspector,
                [targets[1].TorrentId, PeerRows(128), context.GetValue(inspector)!])!;
            inspector.Refresh();
            await CaptureReady(inspector, () => ReferenceEquals(table.ItemsSource, inspector.ShownPeers));
            await CaptureLayout();
            if (ReferenceEquals(table.ItemsSource, displayed)
                || inspector.Peers is not { Count: 128 } replacement
                || replacement.Any(peer => peer.TorrentId != targets[1].TorrentId))
                throw new InvalidOperationException("The ready replacement did not reach the inspector table.");
            await CaptureUi("selection-peers-replaced");
            outcomes.Add(new { journey = "selection during populated peer refresh", peers = 10000,
                coldPreparationMilliseconds = coldMilliseconds,
                firstPublishedPeers = firstBatch,
                previousReadCancelled = true, staleReplyDiscarded = true,
                displayedRowsRetained = true, replacementPeers = 128 });
        }
        finally
        {
            fetching.SetValue(inspector, false);
            inspector.RefreshDetail();
        }
        await CaptureReady(inspector, () => !inspector.IsLoading && !inspector.HasError);
        await CaptureInspectorLifecycle(outcomes);
    }

    private async Task CaptureInspectorLifecycle(List<object> outcomes)
    {
        var inspector = Model.Inspector;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var context = typeof(Inspector).GetField("_context", flags)!;
        var reading = typeof(Inspector).GetField("_reading", flags)!;
        var frame = typeof(Inspector).GetField("_subjectFrame", flags)!;
        Model.CloseInspector();
        var closed = context.GetValue(inspector);
        foreach (var torrent in Model.Torrents)
            Torrents.Selection = new Syno.TableView.Selection([torrent], torrent);
        await CaptureLayout();
        if (inspector.IsActive || !Equals(closed, context.GetValue(inspector)) || frame.GetValue(inspector) is not null)
            throw new InvalidOperationException("Closed inspector observed torrent selection.");
        Model.OpenInspector();
        await CaptureReady(inspector, () => !inspector.IsLoading && !inspector.IsReplacing);
        if (inspector.Target != Model.Current)
            throw new InvalidOperationException("Reopened inspector missed the current selection.");
        Model.IsInspectorCompact = true;
        closed = context.GetValue(inspector);
        var target = Model.Torrents.First(torrent => torrent != Model.Current);
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await CaptureLayout();
        if (inspector.IsActive || !Equals(closed, context.GetValue(inspector)))
            throw new InvalidOperationException("Compact inspector observed torrent selection.");
        Model.IsInspectorCompact = false;
        await CaptureReady(inspector, () => !inspector.IsLoading && !inspector.IsReplacing);
        if (inspector.Target != target)
            throw new InvalidOperationException("Expanded inspector missed the current selection.");

        Model.Features.CanStart = true;
        Model.RequestSnapshot();
        await Model.Navigation.Navigate(WindowPage.Library);
        Model.Library.Configuration = LibraryConfiguration.Files;
        await CaptureReady(Model.Library, () => Model.Library.IsAvailable && Model.Library.Rows.Count >= 2);
        var rows = Model.Library.Rows.DistinctBy(row => row.Name).Take(2).ToArray();
        Model.Library.Select(rows[0]);
        await CaptureReady(inspector, () => inspector.Library is not null && !inspector.IsReplacing);
        var pane = (InspectorPane)InspectorContent.Content;
        if (((TextBlock)pane.FindName("HeadingName")).Text != rows[0].Name)
            throw new InvalidOperationException("Library inspector did not display its selected file.");
        var open = ((Button)typeof(InspectorPane).GetField("_libraryOpen", flags)!.GetValue(pane)!).Command;
        var oldOrigin = Library.OriginActions.Create(Model.Library) as Controls.MenuCommand;
        Model.Library.Select(rows[1]);
        if (open?.CanExecute(null) == true || oldOrigin?.Command.CanExecute(null) == true)
            throw new InvalidOperationException("Retained Library actions targeted the new selection.");
        Model.Library.Select(rows[0]);
        Model.CloseInspector();
        closed = context.GetValue(inspector);
        var detail = typeof(Library.Browser).GetField("_loadedDetail", flags)!;
        var held = detail.GetValue(Model.Library);
        Model.Library.Select(rows[1]);
        await CaptureLayout();
        if (inspector.IsActive || !Equals(closed, context.GetValue(inspector))
            || !ReferenceEquals(held, detail.GetValue(Model.Library)) || frame.GetValue(inspector) is not null)
            throw new InvalidOperationException("Closed inspector read Library detail.");
        await CaptureReady(Model.Library, () => Model.Library.HasOrigins(rows[1].Entry.EntryId));
        if (Library.OriginActions.Create(Model.Library) is not Controls.MenuCommand origin
            || !origin.Command.CanExecute(null))
            throw new InvalidOperationException("Closed Library inspector prevented Show in Torrents.");
        Model.OpenInspector();
        await CaptureReady(inspector, () => inspector.Library is not null && !inspector.IsReplacing);
        if (((TextBlock)pane.FindName("HeadingName")).Text != rows[1].Name)
            throw new InvalidOperationException("Reopened Library inspector missed the current file.");
        Model.IsMinimized = true;
        closed = context.GetValue(inspector);
        Model.Library.Select(rows[0]);
        await CaptureLayout();
        if (inspector.IsActive || !Equals(closed, context.GetValue(inspector)))
            throw new InvalidOperationException("Minimized inspector observed Library selection.");
        Model.IsMinimized = false;
        var cancellation = ((CancellationTokenSource)reading.GetValue(inspector)!).Token;
        await Model.Navigation.Navigate(WindowPage.Torrents);
        if (!cancellation.IsCancellationRequested || inspector.Library is not null)
            throw new InvalidOperationException("Workspace navigation retained old inspector work.");
        await CaptureReady(inspector, () => !inspector.IsLoading && !inspector.IsReplacing);
        outcomes.Add(new { journey = "shared inspector lifetime", closedTorrentsIdle = true,
            closedLibraryIdle = true, compactTorrentsIdle = true, reopenFollowsSelection = true, minimizedLibraryIdle = true,
            workspaceCancelsPrevious = true, retainedActionsGuarded = true, closedOriginsAvailable = true });
    }

    private async Task CaptureSpeedCoverage(Inspector inspector, InspectorPane pane, string theme, List<string> completed)
    {
        inspector.Select(InspectorSection.Speed);
        await CaptureLayout();
        var graph = (SpeedGraph)pane.FindName("Speed");
        var history = typeof(Inspector).GetProperty(nameof(Inspector.History))!;
        var original = inspector.History;
        typeof(Inspector).GetMethod("Invalidate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(inspector, null);
        typeof(Inspector).GetField("_fetching", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inspector, true);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var hours in new[] { 6, 24 })
            {
                var start = now - hours * 3600;
                var samples = Enumerable.Range(0, hours * 60)
                    .Select(minute =>
                    {
                        var first = start + minute * 60;
                        var gap = minute == hours * 30 ? 3 : 0;
                        return new SpeedSample(first + 59, 0,
                            minute % 120 < 6 ? 65536 : 0, first + gap, 60 - gap);
                    }).ToArray();
                history.SetValue(inspector, samples);
                inspector.Refresh();
                var ranges = (SelectorBar)graph.FindName("Ranges");
                ranges.SelectedItem = (SelectorBarItem)graph.FindName(hours == 6 ? "SixHours" : "Day");
                var name = $"speed-{theme}-{hours}h-brief-gap";
                await CaptureUi(name);
                completed.Add(name);
            }
        }
        finally
        {
            history.SetValue(inspector, original);
            inspector.Select(InspectorSection.Peers);
            inspector.Refresh();
            await CaptureLayout();
        }
    }

    private async Task CapturePeers(Torrent target, List<object> outcomes, List<string> completed)
    {
        await ShowTorrents();
        Model.Filter = TorrentFilter.All;
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        Run(Model.Properties);
        var inspector = Model.Inspector;
        inspector.Select(InspectorSection.Peers);
        await CaptureReady(
            inspector,
            () => !inspector.IsLoading && inspector.Peers is not null && !inspector.HasError
        );
        await Model.Select([], null);
        await CaptureReady(
            inspector,
            () =>
                inspector.IsGlobal
                && !inspector.IsLoading
                && inspector.Peers is not null
                && !inspector.HasError
        );
        inspector.Select(InspectorSection.Speed);
        await CaptureReady(
            inspector,
            () =>
                inspector.IsGlobal
                && !inspector.IsLoading
                && inspector.History is not null
                && !inspector.HasError
        );
        outcomes.Add(
            new
            {
                journey = "engine-backed global peers and empty-selection speed",
                samples = inspector.History!.Count,
            }
        );
        inspector.Select(InspectorSection.Peers);
        await CaptureReady(
            inspector,
            () => !inspector.IsLoading && inspector.Peers is not null && !inspector.HasError
        );
        if (Model.Geolocation.Find(IPAddress.Parse("8.8.8.8")) is null)
            throw new InvalidOperationException(
                "The bundled country database did not resolve the capture address."
            );

        var countries = await Geography.Load();
        var codes = new[]
        {
            "US",
            "CA",
            "BR",
            "GB",
            "FR",
            "NL",
            "DE",
            "RU",
            "IN",
            "CN",
            "JP",
            "AU",
            "ZA",
            "SG",
        };
        var peers = Enumerable
            .Range(0, 100)
            .Select(index =>
            {
                var code = codes[index % codes.Length];
                var country = countries[code];
                var area =
                    country.Areas.Length == 0
                        ? null
                        : country.Areas[index / codes.Length % country.Areas.Length];
                var place = new Place(
                    code,
                    new Dictionary<string, string> { ["en"] = new RegionInfo(code).EnglishName }
                );
                var city = area is null
                    ? null
                    : new Place(null, new Dictionary<string, string> { ["en"] = area.Name });
                return new Peer(
                    Model.Text,
                    target.TorrentId,
                    $"192.0.2.{index + 1}:51413",
                    new Location(code, place, city, area?.Point ?? country.Anchor),
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            client = "Capture fixture",
                            transport = "tcp",
                            incoming = index % 2 == 0,
                            state = index switch
                            {
                                1 => "connecting",
                                2 => "handshake",
                                4 => "unknown",
                                _ => index % 3 == 0 ? "known" : "connected",
                            },
                            role = index % 3 == 0 ? "unknown" : index % 3 == 1 ? "seed" : "leecher",
                            encryption = (index % 5) switch
                            {
                                0 => "unknown",
                                1 => "plain",
                                2 => "handshake",
                                3 => "rc4",
                                _ => "tls",
                            },
                            sources = new[] { "tracker" },
                            interested = true,
                            choked = false,
                            remote_interested = true,
                            remote_choked = false,
                            read_wait = Array.Empty<string>(),
                            write_wait = Array.Empty<string>(),
                            progress = .65,
                            download_rate = index % 3 == 0 ? 0 : (index % 7 + 1) * 32768,
                            upload_rate = index % 3 == 0 ? 0 : (index % 5 + 1) * 24576,
                            downloaded = 1048576,
                            uploaded = 524288,
                        }
                    )
                );
            })
            .ToArray();
        var origin = new Location("GB", new Place("GB", null), null, new GeoPoint(-.12, 51.5));
        var fetching = typeof(Inspector).GetField(
            "_fetching",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var originField = typeof(Inspector).GetField(
            "_origin",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var originKey = typeof(Inspector).GetField(
            "_originKey",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var originalOrigin = originField.GetValue(inspector);
        var originalKey = originKey.GetValue(inspector);
        var pane = (InspectorPane)InspectorContent.Content;
        var original = inspector.Peers;
        var locating = typeof(Inspector).GetField("_locating", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var locations = peers.Select(peer => peer.Location!).ToArray();
        var client = (PipeClient)typeof(Inspector)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(inspector)!;
        var replied = typeof(PipeClient).GetField(
            nameof(PipeClient.CommandReplied),
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var onReplied = replied.GetValue(client);
        // Theme and layout saves otherwise refresh the empty engine peer list over the fixture.
        replied.SetValue(client, null);
        await CaptureLayout();
        void RestorePeers()
        {
            typeof(Inspector)
                .GetMethod("Invalidate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(inspector, null);
            fetching.SetValue(inspector, true);
            locating.SetValue(inspector, true);
            originField.SetValue(inspector, origin);
            originKey.SetValue(inspector, (Model.PublicAddress, Model.Geolocation.Version));
            typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, peers);
            inspector.Refresh();
        }
        try
        {
            // The switch measures the table's default columns.
            if (ReviewMode == CaptureMode.PeerSwitch)
            {
                RestorePeers();
                await CapturePeerSwitch(inspector, pane, peers, outcomes);
                return;
            }
            var connections = (Syno.TableView.Table)pane.FindName("Peers");
            connections.Layout = connections.Layout with
            {
                Order = ["connection", "state", "down", "up", "downloaded", "uploaded", "city", "endpoint"],
            };
            RestorePeers();
            if (ReviewMode == CaptureMode.Memory)
            {
                await CaptureTableMemory(inspector, pane, outcomes);
                return;
            }
            foreach (var theme in new[] { "dark", "light" })
            {
                await Model.Settings.SelectTheme(theme);
                await CaptureReady(Model, () => Model.Theme == theme && Model.CanClose);
                pane.Layout = pane.Layout with { View = pane.Layout.View with { Layout = PeerLayout.Table } };
                AppWindow.Resize(new SizeInt32(1040, 680));
                await CaptureLayout();
                connections.FitWidth();
                await CaptureUi($"peers-{theme}-connection-icons");
                outcomes.Add(new
                {
                    journey = "zero-count presentation",
                    theme,
                    fields = CaptureElements(Root).OfType<Field>().Where(field => field.DimsZeros)
                        .Select(field => new
                        {
                            field.Label,
                            ranges = CaptureElements(field).OfType<TextBlock>()
                                .SelectMany(block => block.TextHighlighters)
                                .Select(highlighter => new
                                {
                                    color = (highlighter.Foreground as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color.ToString(),
                                    spans = highlighter.Ranges.Select(range => new { range.StartIndex, range.Length }).ToArray(),
                                }).ToArray(),
                        }).ToArray(),
                });
                await CaptureSpeedCoverage(inspector, pane, theme, completed);
                RestorePeers();
                foreach (var precision in new[] { "countries", "coordinates", "mixed" })
                {
                    for (var index = 0; index < peers.Length; index++)
                    {
                        var location = locations[index];
                        if (precision == "countries" || precision == "mixed" && index % 4 == 0)
                            location = new Location(location.Code, location.Country, null, null);
                        else if (index == 0)
                            location = new Location(
                                location.Code,
                                location.Country,
                                null,
                                location.Point
                            );
                        peers[index].Location = location;
                    }
                    foreach (var portrait in new[] { false, true })
                    {
                        AppWindow.Resize(
                            portrait ? new SizeInt32(760, 1100) : new SizeInt32(1280, 900)
                        );
                        await CaptureLayout();
                        Split.SetValue(portrait ? 800 : 360);
                        inspector.Refresh();
                        pane.Layout = pane.Layout with { View = PeerView.Default };
                        foreach (
                            var layout in new[]
                            {
                                PeerLayout.Map,
                                PeerLayout.Both,
                                PeerLayout.Table,
                            }
                        )
                        {
                            var more = CaptureElements(pane)
                                .OfType<Button>()
                                .Single(button =>
                                    AutomationProperties.GetAutomationId(button) == "PeerMore"
                                );
                            var menu = (MenuFlyout)more.Flyout;
                            menu.ShowAt(more);
                            await CaptureLayout();
                            var key = layout switch
                            {
                                PeerLayout.Map => "map",
                                PeerLayout.Table => "table",
                                _ => "both",
                            };
                            var choice = menu
                                .Items.OfType<MenuFlyoutItem>()
                                .Single(item => item.Text == Model.Text.Get("peer_map", key));
                            if (pane.Layout.View.Layout != layout)
                            {
                                var automation =
                                    FrameworkElementAutomationPeer.CreatePeerForElement(choice);
                                (
                                    (IInvokeProvider)automation.GetPattern(PatternInterface.Invoke)
                                ).Invoke();
                            }
                            menu.Hide();
                            await CaptureLayout();
                            if (pane.Layout.View.Layout != layout)
                                throw new InvalidOperationException(
                                    "The peer view menu did not apply its choice."
                                );
                            var map = CaptureElements(pane).OfType<PeerMap>().SingleOrDefault();
                            inspector.Map.Update(peers, origin, Model.Text);
                            var name =
                                $"peers-{theme}-{precision}-{(portrait ? "portrait" : "landscape")}-{layout}";
                            await CaptureUi(name);
                            if (!ReferenceEquals(inspector.Peers, peers))
                                throw new InvalidOperationException("The engine replaced the peer fixture.");
                            completed.Add(name);
                            if (layout == PeerLayout.Both)
                            {
                                var splitter = (Splitter)pane.FindName("PeerSplit");
                                var orientation = splitter.Orientation;
                                var before = splitter.Value;
                                splitter.SetValue(before * .75);
                                await CaptureLayout();
                                var after =
                                    orientation == Orientation.Horizontal
                                        ? pane.Layout.View.Stacked
                                        : pane.Layout.View.Side;
                                if (
                                    splitter.Value == before
                                    || after is < .15 or > .85
                                    || splitter.Orientation != orientation
                                )
                                    throw new InvalidOperationException(
                                        "The peer splitter must resize without changing orientation."
                                    );
                                inspector.Map.Update(peers, origin, Model.Text);
                                await CaptureUi(name + "-resized");
                                completed.Add(name + "-resized");
                                if (precision == "countries")
                                {
                                    var view = pane.Layout.View;
                                    foreach (var closeMap in new[] { true, false })
                                    {
                                        pane.Layout = pane.Layout with { View = view };
                                        foreach (var mapClosed in new[] { closeMap, !closeMap })
                                        {
                                            var button = (Button)pane.FindName(
                                                mapClosed ? "MapClose" : "TableClose"
                                            );
                                            var automation = FrameworkElementAutomationPeer.CreatePeerForElement(button);
                                            ((IInvokeProvider)automation.GetPattern(PatternInterface.Invoke)).Invoke();
                                            await CaptureLayout();
                                            var remaining = mapClosed ? PeerLayout.Table : PeerLayout.Map;
                                            if (
                                                pane.Layout.View.Layout != remaining
                                                || ((Splitter)pane.FindName("PeerSplit")).Visibility != Microsoft.UI.Xaml.Visibility.Collapsed
                                                || CaptureElements(pane).OfType<PeerMap>().Any() == mapClosed
                                            )
                                                throw new InvalidOperationException(
                                                    "Closing a peer pane must show the other pane alone."
                                                );
                                        }
                                    }
                                    pane.Layout = pane.Layout with { View = view };
                                    outcomes.Add(new { journey = "peer pane close and switch", theme, portrait });
                                }
                            }
                        }
                    }
                }
            }
            AppWindow.Resize(new SizeInt32(760, 600));
            await CaptureLayout();
            Split.SetValue(300);
            pane.Layout = pane.Layout with
            {
                View = pane.Layout.View with { Layout = PeerLayout.Both, Stacked = .5 },
            };
            await CaptureLayout();
            inspector.Map.Update(peers, null, Model.Text);
            await CaptureUi("peers-small-unknown-origin");
            completed.Add("peers-small-unknown-origin");
            var estimated = peers[14];
            var located = peers[28];
            var country = locations[28];
            estimated.Location = new Location(country.Code, country.Country, null, null);
            located.Location = new Location(
                country.Code,
                country.Country,
                null,
                countries["US"].Areas[0].Point
            );
            typeof(Inspector)
                .GetProperty(nameof(Inspector.Peers))!
                .SetValue(inspector, new[] { estimated, located });
            inspector.Refresh();
            var table = (Syno.TableView.Table)pane.FindName("Peers");
            table.Selection = new Syno.TableView.Selection([located], located);
            await CaptureUi("peers-coincident-selection");
            completed.Add("peers-coincident-selection");
            var selected = CaptureElements(pane).OfType<PeerMap>().Single();
            outcomes.Add(
                new
                {
                    journey = "coordinate selection at an estimated destination",
                    detail = AutomationProperties.GetHelpText(selected),
                }
            );
            outcomes.Add(
                new
                {
                    journey = "native peer map and layout review",
                    peers = peers.Length,
                    scope = "Synthetic country-only, coordinate and mixed records in the real inspector; real country database lookup, header menu and splitter owner; no public transfers.",
                }
            );
            AppWindow.Resize(new SizeInt32(1280, 900));
            await CaptureLayout();
            typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, peers);
            inspector.Refresh();
            pane.Layout = pane.Layout with
            {
                View = PeerView.Default with { Layout = PeerLayout.Map },
            };
            await CaptureLayout();
            var centred = CaptureElements(pane).OfType<PeerMap>().Single();
            var viewport = (MapViewport)typeof(PeerMap)
                .GetField("_viewport", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(centred)!;
            // The journeys use the ratio that zooming or panning to the view gives.
            void View(float zoom, Vector2 focus) =>
                viewport.SetView(zoom, focus, viewport.CalculateRatio(zoom, focus.Y));
            var navigation = (StackPanel)centred.FindName("Navigation");
            var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = navigation.RegisterPropertyChangedCallback(
                Microsoft.UI.Xaml.UIElement.VisibilityProperty,
                (_, _) =>
                {
                    if (navigation.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed)
                        hidden.TrySetResult();
                }
            );
            try
            {
                // Only a zoom shows the Fit button.
                typeof(PeerMap).GetMethod("ZoomAt", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(centred, [2f, viewport.Offset]);
                if (navigation.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
                    throw new InvalidOperationException("A zoom did not show the map's Fit button.");
                await CaptureUi("peers-navigation-visible");
                await hidden.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await CaptureUi("peers-navigation-idle");
                Torrents.FocusRows(Microsoft.UI.Xaml.FocusState.Keyboard);
                await CaptureLayout();
                centred.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
                await CaptureLayout();
                var fit = (Button)centred.FindName("FitButton");
                if (!fit.Focus(Microsoft.UI.Xaml.FocusState.Keyboard))
                    throw new InvalidOperationException("The map Fit button could not receive keyboard focus.");
                await CaptureLayout();
                await Task.Delay(TimeSpan.FromSeconds(3));
                await CaptureUi("peers-navigation-focused");
                centred.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                completed.Add("peer map navigation idle and focus");
            }
            finally
            {
                navigation.UnregisterPropertyChangedCallback(Microsoft.UI.Xaml.UIElement.VisibilityProperty, token);
            }
            List<(string Name, Location Origin)> homes =
            [
                ("tokyo", new Location("JP", new Place("JP", null), null, new GeoPoint(139.69, 35.69))),
                (
                    "buenos-aires",
                    new Location("AR", new Place("AR", null), null, new GeoPoint(-58.38, -34.6))
                ),
            ];
            if (originalOrigin is Location own)
                homes.Add(("engine", own));
            foreach (var (place, home) in homes)
            {
                var meridian = (float)(
                    home.Point?.Longitude ?? countries[home.Code].Anchor.Longitude
                );
                inspector.Map.Update(peers, home, Model.Text);
                View(1f, Vector2.Zero);
                await CaptureUi($"peers-meridian-{place}");
                completed.Add($"peers-meridian-{place}");
                View(1f, new Vector2(90, 0));
                if (viewport.Center.X != 90)
                    throw new InvalidOperationException("The fitted map did not scroll.");
                await CaptureUi($"peers-meridian-{place}-scrolled");
                completed.Add($"peers-meridian-{place}-scrolled");
                // The 180° cuts through Chukotka and Fiji, which draw no border.
                var cut = 180 - meridian;
                cut -= 360 * MathF.Round(cut / 360);
                foreach (var (part, latitude) in new[] { ("russia", 66f), ("fiji", -17f) })
                {
                    View(6f, new Vector2(cut, -latitude));
                    await CaptureUi($"peers-meridian-{place}-{part}");
                    completed.Add($"peers-meridian-{place}-{part}");
                }
                // Opposite the origin, then one turn further, which must look the same.
                View(3f, new Vector2(175, -40));
                await CaptureUi($"peers-meridian-{place}-edge");
                completed.Add($"peers-meridian-{place}-edge");
                View(3f, new Vector2(175 + 360, -40));
                if (viewport.Center.X != 175)
                    throw new InvalidOperationException("Panning a whole turn did not wrap.");
                outcomes.Add(new { journey = "peer map centred on origin", place, meridian });
            }
            // Zooming in to Europe at 600% traces finer outlines for every
            // country that has one.
            var renderer = CaptureRenderer(centred)!;
            var land = (System.Collections.IDictionary)typeof(MapRenderer)
                .GetField("_land", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(renderer)!;
            var detail = typeof(MapRenderer).GetField(
                "_detail",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            inspector.Map.Update(peers, homes[0].Origin, Model.Text);
            View(1f, Vector2.Zero);
            await CaptureUi("peers-detail-fitted");
            var fitted = (float)detail.GetValue(renderer)!;
            View(6f, new Vector2(10 - 139.69f, -50));
            await CaptureUi("peers-detail-europe");
            completed.Add("peers-detail-europe");
            var zoomed = (float)detail.GetValue(renderer)!;
            var outlined = countries.Values.Count(country => country.Rings.Length > 0);
            if (zoomed >= fitted || land.Count != outlined)
                throw new InvalidOperationException(
                    $"Zooming in kept outline detail {fitted} -> {zoomed} for {land.Count} of {outlined} countries."
                );
            outcomes.Add(new { journey = "peer map outline detail", fitted, zoomed, land = land.Count });
            // Each wheel step zooms by a quarter; one in about three changes the
            // outline detail and traces the land again.
            var maxZoom = MapViewport.MaxZoom;
            foreach (var (area, focus) in new[] { ("fit", Vector2.Zero), ("europe", new Vector2(10 - 139.69f, -50)) })
            {
                View(1f, focus);
                await Task.Delay(500);
                using var before = Process.GetCurrentProcess();
                var fittedBytes = before.PrivateMemorySize64;
                List<object> steps = [];
                var zoomFrames = 0;
                var cyclesBefore = CaptureCycles.Current();
                var motionBefore = centred.MotionCycles;
                for (var zoom = 1.25f; zoom <= maxZoom; zoom *= 1.25f)
                {
                    List<double> frames = [];
                    var previous = (float)detail.GetValue(renderer)!;
                    centred.FrameMeasured = frames.Add;
                    View(zoom, focus);
                    await Task.Delay(500);
                    centred.FrameMeasured = null;
                    var current = (float)detail.GetValue(renderer)!;
                    zoomFrames += frames.Count;
                    steps.Add(new
                    {
                        zoom,
                        detail = current,
                        retraced = current != previous,
                        frames = frames.Count,
                        maxMs = frames.DefaultIfEmpty().Max(),
                        sumMs = frames.Sum(),
                    });
                }
                using var after = Process.GetCurrentProcess();
                outcomes.Add(new
                {
                    journey = "peer map zoom timing",
                    area,
                    fittedBytes,
                    zoomedBytes = after.PrivateMemorySize64,
                    frames = zoomFrames,
                    uiMegacycles = (CaptureCycles.Current() - cyclesBefore) / 1e6,
                    layerMegacycles = (centred.MotionCycles - motionBefore) / 1e6,
                    steps,
                    scope = "Map only at 1280 by 900, synthetic peers, draw and preparation CPU time; excludes GPU execution.",
                });
            }
            // Drags cross the fill window's edges at a steady and a fast pointer
            // speed; the vertical ones also change the ratio with the latitude.
            var offset = viewport.Offset;
            foreach (
                var (area, zoom, focus, direction, speed) in new[]
                {
                    ("canada-islands", 32f, new Vector2(-110 - 139.69f, -72), new Vector2(-1, 0), 20f),
                    ("canada-islands-fast", 32f, new Vector2(-110 - 139.69f, -72), new Vector2(-1, 0), 100f),
                    ("europe-north", 8f, new Vector2(10 - 139.69f, -35), new Vector2(0, 1), 4f),
                    ("norway-north", 128f, new Vector2(8 - 139.69f, -61), new Vector2(0, 1), 20f),
                }
            )
            {
                View(zoom, focus);
                await Task.Delay(1000);
                List<double> frames = [];
                List<double> starts = [];
                var watch = Stopwatch.StartNew();
                centred.FrameMeasured = ms =>
                {
                    frames.Add(ms);
                    starts.Add(watch.Elapsed.TotalMilliseconds);
                };
                var picturesBefore = centred.MotionPictures;
                var dragCycles = CaptureCycles.Current();
                var dragMotion = centred.MotionCycles;
                const int Steps = 120;
                for (var step = 1; step <= Steps; step++)
                {
                    viewport.Hold(zoom, focus, offset + direction * speed * step);
                    await Task.Delay(16);
                }
                var dragMs = watch.Elapsed.TotalMilliseconds;
                var shown = centred.MotionPictures - picturesBefore;
                await Task.Delay(500);
                centred.FrameMeasured = null;
                var uiCycles = CaptureCycles.Current() - dragCycles;
                var layerCycles = centred.MotionCycles - dragMotion;
                // The time between frames also counts what Direct2D does after
                // Draw returns, such as tessellating plain lines.
                var intervals = starts.Zip(starts.Skip(1), (from, to) => to - from).Order().ToList();
                frames.Sort();
                outcomes.Add(new
                {
                    journey = "peer map drag timing",
                    area,
                    zoom,
                    speed,
                    steps = Steps,
                    uiMegacycles = uiCycles / 1e6,
                    uiMegacyclesPerFrame = frames.Count == 0 ? 0 : uiCycles / 1e6 / frames.Count,
                    layerMegacycles = layerCycles / 1e6,
                    shown,
                    shownFps = shown / (dragMs / 1000),
                    elapsedMs = watch.Elapsed.TotalMilliseconds,
                    frames = frames.Count,
                    medianMs = frames.Count == 0 ? 0 : frames[frames.Count / 2],
                    p95Ms = frames.Count == 0 ? 0 : frames[(int)(frames.Count * .95)],
                    maxMs = frames.DefaultIfEmpty().Max(),
                    over16Ms = frames.Count(frame => frame > 16.7),
                    intervalMedianMs = intervals.Count == 0 ? 0 : intervals[intervals.Count / 2],
                    intervalMaxMs = intervals.DefaultIfEmpty().Max(),
                    scope = "Map only, synthetic peers, draw and preparation CPU time; excludes GPU execution.",
                });
            }
            foreach (var updates in new[] { false, true })
            {
                var focus = new Vector2(10 - 139.69f, -50);
                View(8f, focus);
                await Task.Delay(1000);
                List<double> frames = [];
                List<double> submissions = [];
                var rates = peers.Select(peer => peer.DownloadRate).ToArray();
                var allocated = GC.GetTotalAllocatedBytes(false);
                centred.FrameMeasured = frames.Add;
                for (var step = 0; step < 120; step++)
                {
                    var started = Stopwatch.GetTimestamp();
                    if (updates && step % 6 == 0)
                    {
                        for (var index = 0; index < peers.Length; index++)
                            CaptureRates(peers[index], rates[index] * (step % 12 == 0 ? 1.1 : 1));
                        inspector.Map.Update(peers, homes[0].Origin, Model.Text);
                    }
                    viewport.Hold(8f, focus, offset + new Vector2(step * 4, step));
                    submissions.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    await Task.Delay(16);
                }
                await Task.Delay(500);
                centred.FrameMeasured = null;
                outcomes.Add(new
                {
                    journey = "peer updates during drag",
                    updates,
                    drawMs = frames,
                    submissionMs = submissions,
                    allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated,
                    scope = "Same 100 peers and 8x view; drawMs includes preparation. Submission overlaps preparation and must not be added to drawMs; excludes GPU execution.",
                });
                for (var index = 0; index < peers.Length; index++)
                    CaptureRates(peers[index], rates[index]);
                inspector.Map.Update(peers, homes[0].Origin, Model.Text);
            }
            View(1f, Vector2.Zero);
            await CaptureLayout();
            // Choosing a place on the map limits the table to every peer there;
            // clicking empty map and Escape both clear the choice through Choose(null).
            pane.Layout = pane.Layout with
            {
                View = PeerView.Default with { Layout = PeerLayout.Both },
            };
            inspector.Refresh();
            await CaptureUi("peers-place-none");
            var chooser = CaptureElements(pane).OfType<PeerMap>().Single();
            var choose = typeof(PeerMap).GetMethod(
                "Choose",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            var choices = inspector.Map.Destinations.ToArray();
            var chosen = choices.First(choice => choice.Key.Code == "JP");
            choose.Invoke(chooser, [chosen]);
            await CaptureUi("peers-place-japan");
            completed.Add("peers-place-japan");
            var japan = inspector.Place
                ?? throw new InvalidOperationException("Choosing a map place did not filter the table.");
            var expected = peers.Count(peer => japan.Contains(peer.Location));
            var states = inspector.ShownPeers!.Select(peer => peer.State).Distinct().Count();
            if (
                inspector.ShownPeers!.Count != expected
                || expected == 0
                || expected == peers.Length
                || !ReferenceEquals(table.ItemsSource, inspector.ShownPeers)
            )
                throw new InvalidOperationException("The table does not show exactly the chosen place's peers.");
            var selectedField = typeof(PeerMap).GetField("_selected", BindingFlags.Instance | BindingFlags.NonPublic)!;
            inspector.Place = null;
            await CaptureLayout();
            if (selectedField.GetValue(chooser) is not null)
                throw new InvalidOperationException("Clearing Inspector.Place left a chosen map destination.");
            outcomes.Add(new
            {
                journey = "authoritative place cleared",
                tableCleared = inspector.Place is null && ReferenceEquals(table.ItemsSource, inspector.Peers),
                mapCleared = selectedField.GetValue(chooser) is null,
            });
            choose.Invoke(chooser, [chosen]);
            var sameCountry = choices.Where(choice => choice.Key.Code == "US").Take(2).ToArray();
            if (sameCountry.Length == 2)
            {
                choose.Invoke(chooser, [sameCountry[0]]);
                var first = selectedField.GetValue(chooser);
                var place = inspector.Place;
                choose.Invoke(chooser, [sameCountry[1]]);
                var second = selectedField.GetValue(chooser);
                await inspector.PeersReady;
                var row = inspector.ShownPeers!.First();
                table.Selection = new Syno.TableView.Selection([row], row);
                await CaptureLayout();
                if (Equals(first, second) || inspector.Place != place)
                    throw new InvalidOperationException("Estimated destinations or table selection changed the country filter.");
                outcomes.Add(new { journey = "two destinations share one place", distinct = !Equals(first, second), preserved = inspector.Place == place });
            }
            choose.Invoke(chooser, [null]);
            await CaptureUi("peers-place-cleared");
            completed.Add("peers-place-cleared");
            if (inspector.Place is not null || !ReferenceEquals(table.ItemsSource, inspector.Peers))
                throw new InvalidOperationException("Clearing the map place did not show every peer.");
            outcomes.Add(new { journey = "map place filters peers table", place = japan.ToString(), rows = expected, states, all = peers.Length });
            // With Countries detail, two selected US rows mark at most the two
            // most populous US destinations and one Japanese row marks Japan's.
            var several = peers
                .Where(peer => peer.Location?.Code == "US")
                .Take(2)
                .Concat(peers.Where(peer => peer.Location?.Code == "JP").Take(1))
                .ToArray();
            table.Selection = new Syno.TableView.Selection(several, several[0]);
            await CaptureUi("peers-selection-several");
            completed.Add("peers-selection-several");
            var isMarked = typeof(PeerMap).GetMethod(
                "IsMarked",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            var ranks = inspector.Map.Destinations
                .Where(destination => (bool)isMarked.Invoke(chooser, [destination])!)
                .Select(destination => destination.Rank)
                .ToArray();
            if (ranks.Count(rank => rank == 0) != 2 || ranks.Any(rank => rank > 1))
                throw new InvalidOperationException("Selected rows did not mark the most populous destinations.");
            outcomes.Add(new { journey = "table multi-selection marks estimated destinations", rows = several.Length, ranks });
            // Selecting every row marks every route. The frames that follow
            // rebuild what changed; the redraws show the cost of a marked map.
            var invalidateMap = typeof(PeerMap).GetMethod(
                "Invalidate",
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;
            foreach (var (scope, rows) in new[] { ("none", Array.Empty<Peer>()), ("all", peers) })
            {
                List<double> frames = [];
                chooser.FrameMeasured = frames.Add;
                table.Selection = new Syno.TableView.Selection(rows, rows.FirstOrDefault());
                await Task.Delay(1000);
                var settled = frames.Count;
                for (var sample = 0; sample < 20; sample++)
                {
                    invalidateMap.Invoke(chooser, null);
                    ((MapMotion)typeof(PeerMap).GetField("_motion", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(chooser)!).CaptureRedraw();
                    await Task.Delay(50);
                }
                chooser.FrameMeasured = null;
                outcomes.Add(new
                {
                    journey = "peer map selection timing",
                    scope,
                    rows = rows.Length,
                    settleMs = frames.Take(settled).ToArray(),
                    redrawMs = frames.Skip(settled).ToArray(),
                });
            }
            // A rate refresh must keep native routes, and redraws must not
            // publish peer changes or redistribute the destinations.
            var owner = inspector.Map;
            var activeRenderer = CaptureRenderer(chooser)!;
            var routes = typeof(MapRenderer).GetField("_routes", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = typeof(PeerMap).GetField("_preparationQueued", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object[] Arcs() => ((System.Collections.IEnumerable)routes.GetValue(activeRenderer)!)
                .Cast<object>().Select(route => route.GetType()
                    .GetProperty("Arc", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(route))
                .OfType<object>().ToArray();

            async Task DrawMap(Func<bool> ready)
            {
                var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                chooser.FrameMeasured = _ =>
                {
                    if (!(bool)pending.GetValue(chooser)! && ready())
                        drawn.TrySetResult();
                };
                try
                {
                    invalidateMap.Invoke(chooser, null);
                    await drawn.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await inspector.PeersReady;
                }
                finally
                {
                    chooser.FrameMeasured = null;
                }
            }

            inspector.UpdateMap(false);
            await DrawMap(() => true);
            var arcs = Arcs();
            var active = peers.First(peer => peer.DownloadRate > 0);
            var download = active.DownloadRate;
            CaptureRates(active, download * 1.1);
            inspector.UpdateMap(false);
            await DrawMap(() => true);
            if (arcs.Length == 0 || !arcs.SequenceEqual(Arcs()))
                throw new InvalidOperationException("A rate-only update replaced route geometry.");
            CaptureRates(active, download);
            inspector.UpdateMap(false);
            await DrawMap(() => true);
            var notifications = 0;
            System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => notifications++;
            owner.PropertyChanged += changed;
            var destinations = owner.Destinations.ToArray();
            await DrawMap(() => true);
            owner.PropertyChanged -= changed;
            if (notifications != 0 || !destinations.SequenceEqual(owner.Destinations))
                throw new InvalidOperationException("Drawing changed map presentation data.");
            choose.Invoke(chooser, [owner.Destinations.First(destination => destination.Key.Code == "JP")]);
            var retainedPlace = inspector.Place;
            var retainedChoice = selectedField.GetValue(chooser);
            typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(
                inspector, peers.Where(peer => peer.Location?.Code != "JP").ToArray());
            inspector.Refresh();
            await DrawMap(() => owner.Destinations.All(destination => destination.Key.Code != "JP"));
            if (inspector.Place != retainedPlace || inspector.ShownPeers!.Count != 0)
                throw new InvalidOperationException("Losing the last peer cleared the chosen place.");
            typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, peers);
            inspector.Refresh();
            await DrawMap(() => owner.Destinations.Any(destination => destination.Key.Code == "JP"));
            if (((MapModel.Destination?)retainedChoice)?.Key != ((MapModel.Destination?)selectedField.GetValue(chooser))?.Key)
                throw new InvalidOperationException("Returning peers changed the exact chosen destination.");
            // Detach the real control without restarting engine acquisition
            // over the synthetic peer fixture.
            var host = (ContentControl)pane.FindName("PeerDrawing");
            var retiring = (MapWork)typeof(PeerMap).GetField("_work", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chooser)!;
            var retiringMotion = (MapMotion)typeof(PeerMap).GetField("_motion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chooser)!;
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            retiringMotion.Control.GameLoopStopped += (_, _) => stopped.TrySetResult();
            host.Content = null;
            await CaptureLayout();
            await retiring.Released.WaitAsync(TimeSpan.FromSeconds(10));
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await retiringMotion.Released.WaitAsync(TimeSpan.FromSeconds(10));
            if (CaptureRenderer(chooser) is not null || ((System.Collections.ICollection)routes.GetValue(activeRenderer)!).Count != 0)
                throw new InvalidOperationException("Hiding the map retained native resources.");
            host.Content = chooser;
            await CaptureLayout();
            await chooser.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            activeRenderer = CaptureRenderer(chooser)!;
            if (Arcs().Length != arcs.Length || inspector.Place != retainedPlace)
                throw new InvalidOperationException("Resource recreation changed the place or lost routes.");
            if (!ReferenceEquals(owner, inspector.Map) || inspector.Place != retainedPlace)
                throw new InvalidOperationException("Showing the map replaced its model or chosen place.");
            outcomes.Add(new { journey = "map ownership and resources", retainedRoutes = arcs.Length, redrawNotifications = notifications, placePreserved = true });
            inspector.Place = null;
            table.Selection = new Syno.TableView.Selection(several, several[0]);
            pane.Layout = pane.Layout with
            {
                View = PeerView.Default with { Layout = PeerLayout.Both },
            };
            await CaptureLayout();
            var drawing = CaptureElements(pane).OfType<PeerMap>().Single();
            foreach (var count in new[] { 100, 1000, 10000 })
            {
                var rows = PeerRows(count);
                var apply = typeof(Inspector).GetMethod("ApplyPeers", BindingFlags.Instance | BindingFlags.NonPublic)!;
                typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, null);
                var context = typeof(Inspector).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(inspector)!;
                var started = Stopwatch.GetTimestamp();
                await (Task<bool>)apply.Invoke(inspector, [target.TorrentId, rows, context])!;
                var create = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var known = inspector.Peers!;
                var lookup = await Task.Run(() =>
                {
                    var watch = Stopwatch.StartNew();
                    for (var index = 0; index < count; index++)
                        Model.Geolocation.Find(IPAddress.Parse($"87.68.{index / 256}.{index % 256}"));
                    return watch.Elapsed.TotalMilliseconds;
                });
                for (var index = 0; index < known.Count; index++)
                {
                    var location = locations[index % locations.Length];
                    known[index].Location = location with
                    {
                        Point = new GeoPoint(
                            location.Point!.Value.Longitude + index * .0001,
                            location.Point.Value.Latitude
                        ),
                    };
                }
                List<double> preparations = [];
                drawing.PreparationMeasured = preparations.Add;
                inspector.Refresh();
                // The inspector refreshes the pane after the next frame, and the
                // pane then queues its own map update, so both run before the
                // measured map replaces that update.
                var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (new FrameCallback(DispatcherQueue, () => refreshed.TrySetResult()))
                    await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await CaptureLayout();
                List<double> frames = [];
                drawing.FrameMeasured = frames.Add;
                inspector.Map.Update(known, null, Model.Text);
                await CaptureUi($"peers-known-{count}");
                var rendered = inspector.Map.Destinations.Count;
                if (!ReferenceEquals(inspector.Peers, known))
                    throw new InvalidOperationException("The measured map lost its peer fixture: the inspector replaced its peers.");
                if (rendered != count)
                    throw new InvalidOperationException(
                        $"The measured map lost its peer fixture: it shows {rendered} of {count} destinations"
                        + $" in {(inspector.Map.Cities ? "Cities" : "Countries")} detail.");
                var managed = GC.GetTotalMemory(true);
                using var process = Process.GetCurrentProcess();
                var privateBytes = process.PrivateMemorySize64;
                List<double> unchanged = [];
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var sample = 0; sample < 30; sample++)
                {
                    started = Stopwatch.GetTimestamp();
                    await (Task<bool>)apply.Invoke(inspector, [target.TorrentId, rows, context])!;
                    inspector.Map.Update(known, null, Model.Text);
                    unchanged.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                drawing.FrameMeasured = null;
                drawing.PreparationMeasured = null;
                outcomes.Add(new
                {
                    journey = "known peer map timing",
                    peers = count,
                    destinations = known.Select(peer => peer.Location!.Point).Distinct().Count(),
                    rendered,
                    createMs = create,
                    lookupMs = lookup,
                    managedBytes = managed,
                    privateBytes,
                    unchangedAllocatedBytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / unchanged.Count,
                    unchangedMedianMs = unchanged.Order().ElementAt(unchanged.Count / 2),
                    preparationMs = preparations,
                    drawMs = frames,
                    scope = "Native WinUI/Win2D, synthetic located peers, no active arcs; draw and preparation CPU time excludes GPU execution.",
                });
                completed.Add($"peers-known-{count}");
                // Choosing a place blocks the UI thread three times: the click
                // filters the rows, the table takes them, and the next layout
                // realizes them. Clearing the choice does the same with every
                // row. Each is timed alone; the first round includes JIT.
                // Rates on some peers make dots fly, so each round also counts
                // the frames the dots' layer draws while the UI thread is busy.
                // Equal rates would launch every route's dots at the same moment,
                // leaving long gaps with no dot; varied rates keep dots flying.
                foreach (var (peer, index) in known.Take(64).Select((peer, index) => (peer, index)))
                {
                    CaptureRates(peer, 64.0 * 1024 * (1 + index % 13), 8.0 * 1024 * (1 + index % 7));
                }
                // Arcs and dots need the user's location, which the map timing
                // above leaves out.
                inspector.Map.Update(known, (Location?)originField.GetValue(inspector), Model.Text);
                var firstDot = Stopwatch.GetTimestamp();
                var idleFrames = drawing.MotionFrames;
                while (drawing.MotionFrames == idleFrames && Stopwatch.GetElapsedTime(firstDot) < TimeSpan.FromSeconds(15))
                    await Task.Delay(50);
                var firstDotMs = Stopwatch.GetElapsedTime(firstDot).TotalMilliseconds;
                var warmupStart = drawing.MotionFrames;
                await Task.Delay(TimeSpan.FromSeconds(1));
                var warmupFrames = drawing.MotionFrames - warmupStart;
                var motionState = drawing.MotionState;
                var activeArcs = inspector.Map.Destinations.Count(destination => destination.Group.Active > 0);
                var place = inspector.Map.Destinations.MaxBy(destination => destination.Group.Count)!;
                List<object> rounds = [];
                var paneRefresh = typeof(InspectorPane).GetMethod(
                    "Refresh", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(bool)])!;
                for (var round = 0; round < 3; round++)
                {
                    foreach (var choice in new[] { place, null })
                    {
                        var framesBefore = drawing.MotionFrames;
                        var pausedBefore = drawing.MotionPaused;
                        var clicked = Stopwatch.GetTimestamp();
                        choose.Invoke(drawing, [choice]);
                        var clickMs = Stopwatch.GetElapsedTime(clicked).TotalMilliseconds;
                        await inspector.PeersReady;
                        var assigned = Stopwatch.GetTimestamp();
                        table.ItemsSource = inspector.ShownPeers;
                        var assignMs = Stopwatch.GetElapsedTime(assigned).TotalMilliseconds;
                        var preparing = Stopwatch.GetTimestamp();
                        await table.ViewReady;
                        var projectionMs = Stopwatch.GetElapsedTime(preparing).TotalMilliseconds;
                        var laid = Stopwatch.GetTimestamp();
                        Root.UpdateLayout();
                        var layoutMs = Stopwatch.GetElapsedTime(laid).TotalMilliseconds;
                        // A click used to refresh the whole pane as well.
                        var paneStarted = Stopwatch.GetTimestamp();
                        paneRefresh.Invoke(pane, [false]);
                        Root.UpdateLayout();
                        var paneRefreshMs = Stopwatch.GetElapsedTime(paneStarted).TotalMilliseconds;
                        var busyMs = Stopwatch.GetElapsedTime(clicked).TotalMilliseconds;
                        var layerFrames = drawing.MotionFrames - framesBefore;
                        rounds.Add(new
                        {
                            chosen = choice is not null,
                            rows = inspector.ShownPeers!.Count,
                            clickMs,
                            assignMs,
                            projectionMs,
                            layoutMs,
                            paneRefreshMs,
                            busyMs,
                            pausedBefore,
                            layerFrames,
                            layerFps = layerFrames / (busyMs / 1000),
                        });
                        await CaptureLayout();
                    }
                }
                outcomes.Add(new
                {
                    journey = "place choice timing",
                    peers = count,
                    sorted = table.Sort is not null,
                    activeArcs,
                    firstDotMs,
                    warmupFrames,
                    motionState,
                    rounds,
                    scope = "UI-thread CPU time of each step, synthetic located peers; layer frames are the dots'"
                        + " game loop frames while the UI thread is busy with the round.",
                });
                // A click chooses the place under the pointer as PeerMap's
                // OnReleased does, and a click on an empty spot clears it. Each
                // is measured in UI-thread cycles until the end of the frame
                // that shows its result: the map drawn with the choice and the
                // table holding the place's rows. The dots keep flying.
                var nonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
                var mapViewport = (MapViewport)typeof(PeerMap).GetField("_viewport", nonPublic)!.GetValue(drawing)!;
                var selectPeers = typeof(PeerMap).GetMethod("SelectPeers", nonPublic)!;
                var describe = typeof(PeerMap).GetMethod("Describe", nonPublic)!;
                var redraw = typeof(PeerMap).GetMethod("Invalidate", nonPublic)!;
                List<ulong> stamps = [];
                EventHandler<object> stamp = (_, _) => stamps.Add(CaptureCycles.Current());
                List<object> clicks = [];
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += stamp;
                try
                {
                    for (var round = 0; round < 3; round++)
                    {
                        foreach (var point in new[] { mapViewport.Screen(place.Point), new Vector2(2, 2) })
                        {
                            await CaptureLayout();
                            var mapFrames = 0;
                            drawing.FrameMeasured = _ => mapFrames++;
                            var layerBefore = drawing.MotionFrames;
                            stamps.Clear();
                            var clickedAt = Stopwatch.GetTimestamp();
                            var clickStart = CaptureCycles.Current();
                            var hit = ((MapRenderer.Inspection)typeof(PeerMap).GetField("_inspection", nonPublic)!.GetValue(drawing)!).HitTest(point);
                            selectPeers.Invoke(drawing, [Array.Empty<Peer>()]);
                            choose.Invoke(drawing, [hit]);
                            describe.Invoke(drawing, null);
                            redraw.Invoke(drawing, null);
                            var clickCycles = CaptureCycles.Current() - clickStart;
                            while (
                                (mapFrames == 0 || !ReferenceEquals(table.ItemsSource, inspector.ShownPeers))
                                && Stopwatch.GetElapsedTime(clickedAt) < TimeSpan.FromSeconds(3)
                            )
                                await NextFrame();
                            var tableShown = ReferenceEquals(table.ItemsSource, inspector.ShownPeers);
                            await NextFrame();
                            var clickEnd = CaptureCycles.Current();
                            drawing.FrameMeasured = null;
                            var ticks = stamps.ToArray();
                            // Each frame's share runs from one frame's start to
                            // the next; the first from the click, the last to
                            // the end of the frame that shows the result.
                            var shares = ticks.Prepend(clickStart).Append(clickEnd).ToArray();
                            var busiest = shares.Zip(shares.Skip(1), (from, to) => to - from).Max();
                            clicks.Add(new
                            {
                                chosen = hit is not null,
                                rows = inspector.ShownPeers!.Count,
                                tableShown,
                                frames = ticks.Length,
                                mapFrames,
                                clickMegacycles = clickCycles / 1e6,
                                uiMegacycles = (clickEnd - clickStart) / 1e6,
                                busiestFrameMegacycles = busiest / 1e6,
                                ms = Stopwatch.GetElapsedTime(clickedAt).TotalMilliseconds,
                                layerFrames = drawing.MotionFrames - layerBefore,
                            });
                        }
                    }
                }
                finally
                {
                    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= stamp;
                    drawing.FrameMeasured = null;
                }
                outcomes.Add(new
                {
                    journey = "map click timing",
                    peers = count,
                    activeArcs,
                    rounds = clicks,
                    scope = "UI-thread cycles from a map click to the end of the frame that shows the map and table"
                        + " result, with dots flying; the first round includes JIT.",
                });

                async Task NextFrame()
                {
                    var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (new FrameCallback(DispatcherQueue, () => next.TrySetResult()))
                        await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        finally
        {
            replied.SetValue(client, onReplied);
            typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, original);
            originField.SetValue(inspector, originalOrigin);
            originKey.SetValue(inspector, originalKey);
            fetching.SetValue(inspector, false);
            locating.SetValue(inspector, false);
            inspector.Refresh();
            Model.CloseInspector();
        }
    }

    private static JsonElement PeerRows(int count) =>
        JsonSerializer.SerializeToElement(
            Enumerable.Range(0, count).Select(index => new
            {
                endpoint = $"10.{index / 65536}.{index / 256 % 256}.{index % 256}:51413",
                state = "known",
                role = index % 3 == 0 ? "seed" : index % 3 == 1 ? "leecher" : "unknown",
                sources = new[] { "tracker" },
            })
        );
}
