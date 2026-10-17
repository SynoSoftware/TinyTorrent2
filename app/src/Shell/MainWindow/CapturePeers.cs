using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
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
                            incoming = false,
                            state = index % 3 == 0 ? "known" : "connected",
                            role = index % 3 == 0 ? "unknown" : index % 3 == 1 ? "seed" : "leecher",
                            encryption = "rc4",
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
        var completion = typeof(PipeClient).GetField(
            nameof(PipeClient.CommandCompleted),
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var onCompleted = completion.GetValue(client);
        // Theme and layout saves otherwise refresh the empty engine peer list over the fixture.
        completion.SetValue(client, null);
        await CaptureLayout();
        typeof(Inspector)
            .GetMethod("Invalidate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(inspector, null);
        fetching.SetValue(inspector, true);
        locating.SetValue(inspector, true);
        originField.SetValue(inspector, origin);
        originKey.SetValue(inspector, (Model.PublicAddress, Model.Geolocation.Version));
        try
        {
            typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, peers);
            if (ReviewMode == CaptureMode.Memory)
            {
                await CaptureTableMemory(inspector, pane, outcomes);
                return;
            }
            foreach (var theme in new[] { "dark", "light" })
            {
                await Model.Settings.SelectTheme(theme);
                await CaptureReady(Model, () => Model.Theme == theme && Model.CanClose);
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
            var renderer = typeof(PeerMap)
                .GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(centred)!;
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
                for (var zoom = 1.25f; zoom <= maxZoom; zoom *= 1.25f)
                {
                    List<double> frames = [];
                    var previous = (float)detail.GetValue(renderer)!;
                    centred.FrameMeasured = frames.Add;
                    View(zoom, focus);
                    await Task.Delay(500);
                    centred.FrameMeasured = null;
                    var current = (float)detail.GetValue(renderer)!;
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
                const int Steps = 120;
                for (var step = 1; step <= Steps; step++)
                {
                    viewport.Hold(zoom, focus, offset + direction * speed * step);
                    await Task.Delay(16);
                }
                await Task.Delay(500);
                centred.FrameMeasured = null;
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
                var rate = typeof(Peer).GetProperty(nameof(Peer.DownloadRate))!;
                var allocated = GC.GetTotalAllocatedBytes(false);
                centred.FrameMeasured = frames.Add;
                for (var step = 0; step < 120; step++)
                {
                    var started = Stopwatch.GetTimestamp();
                    if (updates && step % 6 == 0)
                    {
                        for (var index = 0; index < peers.Length; index++)
                            rate.SetValue(peers[index], rates[index] * (step % 12 == 0 ? 1.1 : 1));
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
                    rate.SetValue(peers[index], rates[index]);
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
            var mapCanvas = typeof(PeerMap).GetField(
                "_canvas",
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
                    ((Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl)mapCanvas.GetValue(chooser)!).Invalidate();
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
            var native = typeof(PeerMap).GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var activeRenderer = (MapRenderer)native.GetValue(chooser)!;
            var routes = typeof(MapRenderer).GetField("_routes", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pending = typeof(PeerMap).GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic)!;
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
                    ((Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl)mapCanvas.GetValue(chooser)!).Invalidate();
                    await drawn.Task.WaitAsync(TimeSpan.FromSeconds(20));
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
            typeof(Peer).GetProperty(nameof(Peer.DownloadRate))!.SetValue(active, download * 1.1);
            inspector.UpdateMap(false);
            await DrawMap(() => true);
            if (arcs.Length == 0 || !arcs.SequenceEqual(Arcs()))
                throw new InvalidOperationException("A rate-only update replaced route geometry.");
            typeof(Peer).GetProperty(nameof(Peer.DownloadRate))!.SetValue(active, download);
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
            if (!Equals(retainedChoice, selectedField.GetValue(chooser)))
                throw new InvalidOperationException("Returning peers changed the exact chosen destination.");
            activeRenderer.ReleaseGeometry();
            typeof(PeerMap).GetMethod("Prepare", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(chooser, null);
            await DrawMap(() => true);
            if (Arcs().Length != arcs.Length || inspector.Place != retainedPlace)
                throw new InvalidOperationException("Resource recreation changed the place or lost routes.");
            // Detach the real control without restarting engine acquisition
            // over the synthetic peer fixture.
            var host = (ContentControl)pane.FindName("PeerDrawing");
            host.Content = null;
            await CaptureLayout();
            if (native.GetValue(chooser) is not null || ((System.Collections.ICollection)routes.GetValue(activeRenderer)!).Count != 0)
                throw new InvalidOperationException("Hiding the map retained native resources.");
            host.Content = chooser;
            await CaptureLayout();
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
                var groups = new Dictionary<string, Peer[]>();
                typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, null);
                var started = Stopwatch.GetTimestamp();
                apply.Invoke(inspector, [target.TorrentId, rows, groups]);
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
                await CaptureLayout();
                List<double> frames = [];
                drawing.FrameMeasured = frames.Add;
                inspector.Map.Update(known, null, Model.Text);
                await CaptureUi($"peers-known-{count}");
                var rendered = inspector.Map.Destinations.Count;
                if (!ReferenceEquals(inspector.Peers, known) || rendered != count)
                    throw new InvalidOperationException("The measured map lost its peer fixture.");
                var managed = GC.GetTotalMemory(true);
                using var process = Process.GetCurrentProcess();
                var privateBytes = process.PrivateMemorySize64;
                List<double> unchanged = [];
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var sample = 0; sample < 30; sample++)
                {
                    started = Stopwatch.GetTimestamp();
                    apply.Invoke(inspector, [target.TorrentId, rows, groups]);
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
            }
        }
        finally
        {
            completion.SetValue(client, onCompleted);
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
