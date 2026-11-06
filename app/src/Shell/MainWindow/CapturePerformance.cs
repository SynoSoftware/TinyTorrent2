using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;
using Windows.Foundation;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private bool IsPerformanceReview => ReviewMode is CaptureMode.Performance or CaptureMode.PerformanceMap or CaptureMode.PerformanceSelection;

    private async Task CapturePerformance(List<object> outcomes)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Read<T>(object owner, string name) => (T)owner.GetType().GetField(name, flags)!.GetValue(owner)!;
        static void Set(object owner, string name, object value) => owner.GetType().GetProperty(name)!.SetValue(owner, value);
        var directory = _captureDirectory!;
        Directory.CreateDirectory(directory);
        using var input = new PerformanceInput(WinRT.Interop.WindowNative.GetWindowHandle(this));
        await ShowTorrents();
        Model.Filter = TorrentFilter.All;
        Model.IsFilterOpen = false;
        Model.CloseInspector();
        await CaptureLayout();
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
        var origin = input.Screen(default);
        if (AppWindow.ClientSize.Width != 1920 || AppWindow.ClientSize.Height != 1080
            || origin.X < display.X || origin.Y < display.Y
            || origin.X + 1920 > display.X + display.Width || origin.Y + 1080 > display.Y + display.Height)
            throw new InvalidOperationException("The visible 1920x1080 client area must fit on one monitor.");
        var targets = Model.Torrents.OrderBy(torrent => torrent.Name, StringComparer.Ordinal).ToArray();
        var inspector = Model.Inspector;
        var phases = new List<object>();
        var clicks = new List<PerformanceClick>();
        var presses = new List<PerformancePress>();
        (string? TorrentId, long Qpc) selection = default;
        var lastPress = 0L;
        var lastPressTime = 0L;
        var inputOffset = 0L;
        var samples = new List<object>();
        var pictures = new ConcurrentQueue<object>();
        var mapFrames = new ConcurrentQueue<object>();
        var work = new ConcurrentQueue<object>();
        var received = new ConcurrentQueue<object>();
        var inputMaps = new HashSet<PeerMap>();
        var motions = new Dictionary<MapMotion, TypedEventHandler<ICanvasAnimatedControl, CanvasAnimatedDrawEventArgs>>();
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "fixture.json")));
        var cities = fixture.RootElement.GetProperty("cities").GetBoolean();
        var arcLimit = fixture.RootElement.GetProperty("arcLimit").GetInt32();
        var locations = fixture.RootElement.GetProperty("locations").GetInt32();
        var data = new PerformanceData(targets, fixture.RootElement.GetProperty("leechers").Deserialize<int[]>()!, locations, await Geography.Load(),
            fixture.RootElement.GetProperty("connections").GetInt32(), fixture.RootElement.GetProperty("swapsPerSecond").GetInt32());
        var placement = _placement ?? new Placement();
        var layout = placement.Inspector ?? new InspectorLayout(null, null);
        _placement = placement with { Inspector = layout with { View = layout.View with { Cities = cities, ArcLimit = arcLimit } } };
        if (InspectorContent.Content is InspectorPane existing) existing.Layout = _placement.Inspector;
        PeerMap? map = null;
        FrameworkElement? overlay = null;
        var phase = string.Empty;
        var phaseStarted = 0L;
        var selecting = false;
        var firstPicture = 0L;
        var opening = 0L;
        var awaitingConnections = false;
        Torrent? connectionsTarget = null;
        var connectionsPicture = 0L;
        var connectionsCount = 0;
        var reorder = Torrents.CanReorder;
        using var feeding = new CancellationTokenSource();
        Task? feed = null;
        var updates = new List<object>();
        var tables = new List<object>();
        var lastTableSample = 0L;

        static object[] VisibleRows(Syno.TableView.Table table)
        {
            if (!table.IsLoaded || table.Visibility != Visibility.Visible
                || Read<ListView?>(table, "_surface") is not { } surface || surface.Items.Count == 0
                || surface.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstVisibleIndex < 0) return [];
            var first = panel.FirstVisibleIndex;
            var last = Math.Min(panel.LastVisibleIndex, surface.Items.Count - 1);
            if (last < first) return [];
            return Enumerable.Range(first, last - first + 1)
                .Select(index => surface.Items[index]).ToArray();
        }

        Vector2 Screen(FrameworkElement element, Point point)
        {
            var position = element.TransformToVisual(Root).TransformPoint(point);
            var scale = Root.XamlRoot.RasterizationScale;
            return input.Screen(new Vector2((float)(position.X * scale), (float)(position.Y * scale)));
        }
        void Selected(object? sender, Syno.TableView.Selection next)
        {
            if (!selecting || next.Current is not Torrent target) return;
            selection = (target.TorrentId, Stopwatch.GetTimestamp());
            foreach (var pending in presses.Where(press => press.Selected != 0 && press.Rendered == 0
                && press.TorrentId != target.TorrentId)) pending.Superseded = true;
            var pendingPress = presses.LastOrDefault(press => press.TorrentId == target.TorrentId && press.Selected == 0);
            if (pendingPress is not null) pendingPress.Selected = selection.Qpc;
        }
        void Pressed(object sender, PointerRoutedEventArgs args)
        {
            var point = args.GetCurrentPoint(Torrents);
            if (!point.Properties.IsLeftButtonPressed) return;
            var now = Stopwatch.GetTimestamp();
            lastPressTime = (long)point.Timestamp;
            for (var element = args.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
                if (element is ListViewItem { Content: Torrent torrent })
                {
                    if (selecting)
                    {
                        var selected = selection.TorrentId == torrent.TorrentId && selection.Qpc > lastPress ? selection.Qpc : 0;
                        var alreadySelected = selected == 0 && ReferenceEquals(Torrents.Selection.Current, torrent);
                        presses.Add(new PerformancePress { TorrentId = torrent.TorrentId, InputTime = lastPressTime, Received = now,
                            Selected = alreadySelected ? now : selected, AlreadySelected = alreadySelected });
                    }
                    break;
                }
            lastPress = now;
        }
        void Rendered(object? sender, RenderedEventArgs args)
        {
            var now = Stopwatch.GetTimestamp();
            if (selecting)
                foreach (var press in presses.Where(press => press.Selected != 0 && press.Rendered == 0 && !press.Superseded
                    && press.TorrentId == (Torrents.Selection.Current as Torrent)?.TorrentId)) press.Rendered = now;
            if (phase.Length == 0) return;
            if (Stopwatch.GetElapsedTime(lastTableSample, now).TotalMilliseconds >= 100)
            {
                lastTableSample = now;
                tables.Add(new { phase, qpc = now, table = "torrents", subject = "all",
                    rows = VisibleRows(Torrents).OfType<Torrent>().Select(torrent => new { key = torrent.TorrentId, rate = torrent.DownloadRate }).ToArray() });
                if (InspectorContent.Content is InspectorPane current && current.FindName("Peers") is Syno.TableView.Table table)
                    tables.Add(new { phase, qpc = now, table = "peers", subject = inspector.Target?.TorrentId ?? "all",
                        rows = VisibleRows(table).OfType<Peer>().Select(peer => new { key = peer.Key, rate = peer.DownloadRate }).ToArray() });
            }
            if (awaitingConnections && connectionsPicture == 0 && inspector.Target == connectionsTarget
                && InspectorContent.Content is InspectorPane pane
                && Read<PeerMap?>(pane, "_peerMap") is { IsLoaded: true, Visibility: Visibility.Visible } peerMap
                && Read<MapMotion?>(peerMap, "_motion") is { } motion && motion.Control.Opacity > 0
                && motion.ShowsConnections(inspector.Map.Subject, connectionsCount)) connectionsPicture = now;
            if (opening != 0 && firstPicture == 0 && map?.MotionPictures > 0
                && Read<MapMotion>(map, "_motion").Control.Opacity > 0) firstPicture = now;
            var offset = overlay?.RenderTransform as TranslateTransform;
            samples.Add(new { phase, qpc = now, marquee = overlay?.Visibility == Visibility.Visible
                ? new { x = offset?.X ?? 0, y = offset?.Y ?? 0, width = overlay.ActualWidth, height = overlay.ActualHeight }
                : null });
        }
        async Task Until(Func<bool> ready, string message, TimeSpan? timeout = null)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (feed?.IsFaulted == true) await feed;
                if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(20))) throw new TimeoutException(message);
                await Task.Delay(10);
            }
        }
        async Task Measure(string name, PerformanceInput.Plan plan)
        {
            phaseStarted = Stopwatch.GetTimestamp();
            Volatile.Write(ref phase, name);
            var timing = await input.Run(plan, directory);
            if (feed?.IsFaulted == true) await feed;
            if (timing.Clicks.Length > 0)
            {
                if (timing.Clicks.Length != clicks.Count) throw new InvalidOperationException("The input driver did not send every planned click.");
                for (var index = 0; index < clicks.Count; index++)
                {
                    clicks[index].Sent = timing.Clicks[index];
                    clicks[index].InputTime = timing.ClickTimes[index] + inputOffset;
                }
            }
            phases.Add(new { name, started = timing.Started, ended = timing.Ended,
                requestedMs = plan.Milliseconds, timing.WorstLatenessMs, plan, timing.Delivered });
            Volatile.Write(ref phase, string.Empty);
        }
        void Observe(MapMotion motion)
        {
            if (motions.ContainsKey(motion)) return;
            if (map is not null && inputMaps.Add(map))
                map.CaptureInput = (kind, delta) => received.Enqueue(new { phase = Volatile.Read(ref phase),
                    qpc = Stopwatch.GetTimestamp(), kind, delta });
            TypedEventHandler<ICanvasAnimatedControl, CanvasAnimatedDrawEventArgs> drawn = (_, _) => {
                if (motion.DrawnView is { } view)
                {
                    var dots = motion.DrawnDots;
                    mapFrames.Enqueue(new { phase = Volatile.Read(ref phase), qpc = Stopwatch.GetTimestamp(),
                        x = view.Center.X, y = view.Center.Y, scale = view.AxisScale.Y, arcs = motion.DrawnRoutes,
                        visibleRoutes = motion.DrawnVisibleRoutes,
                        locations = motion.KnownLocations, visibleLocations = motion.DrawnLocations,
                        dots = new { count = dots.Count, route = dots.Route, upload = dots.Upload, start = dots.Start, progress = dots.Progress } });
                }
            };
            motions.Add(motion, drawn);
            motion.Control.Draw += drawn;
            motion.PicturePublished = view => pictures.Enqueue(new { phase = Volatile.Read(ref phase),
                qpc = Stopwatch.GetTimestamp(), x = view.Center.X, y = view.Center.Y, scale = view.AxisScale.Y });
            motion.WorkMeasured += (name, ms) => work.Enqueue(new { phase = Volatile.Read(ref phase), name, ms });
        }
        async Task Connections(string name, long started, int count)
        {
            connectionsPicture = 0;
            connectionsCount = count;
            connectionsTarget = Model.Current;
            awaitingConnections = true;
            phaseStarted = started;
            Volatile.Write(ref phase, name);
            await Until(() => InspectorContent.Content is InspectorPane pane
                && Read<PeerMap?>(pane, "_peerMap") is { IsLoaded: true } current && (map = current).MotionPictures > 0,
                "The docked Peers map did not draw.");
            Observe(Read<MapMotion>(map!, "_motion"));
            Root.InvalidateArrange();
            await CaptureLayout();
            await Until(() => connectionsPicture != 0 && !inspector.IsLoading && !inspector.IsReplacing
                && inspector.Peers?.Count == count && inspector.Map.Capture().Observations.Length == count,
                "The docked Peers map did not visibly show its connections.");
            await ViewDrawn();
            if (inspector.Map.Cities != cities || Read<int>(map!, "_arcLimit") != arcLimit)
                throw new InvalidOperationException("The docked map did not use the requested detail and arc limit.");
            var peers = (Syno.TableView.Table)((InspectorPane)InspectorContent.Content).FindName("Peers");
            var rows = Read<ListView>(peers, "_surface");
            await Until(() => ReferenceEquals(peers.ItemsSource, inspector.ShownPeers) && rows.Items.Count == count
                && rows.ContainerFromIndex(0) is ListViewItem { IsLoaded: true, Content: Peer first }
                && (connectionsTarget is null || first.TorrentId == connectionsTarget.TorrentId),
                "The peer table did not show the map's current connections.");
            phases.Add(new { name, started, ended = Stopwatch.GetTimestamp(), firstPicture = connectionsPicture, peers = count });
            outcomes.Add(new { journey = name, firstConnectionsMs = (connectionsPicture - started) * 1000d / Stopwatch.Frequency,
                peers = count, state = map!.MotionState });
            awaitingConnections = false;
            Volatile.Write(ref phase, string.Empty);
        }
        async Task Screenshot(string name)
        {
            File.Delete(Path.Combine(directory, "screenshot-done"));
            await File.WriteAllTextAsync(Path.Combine(directory, "screenshot-request.tmp"), name);
            File.Move(Path.Combine(directory, "screenshot-request.tmp"), Path.Combine(directory, "screenshot-request"), true);
            await Until(() => File.Exists(Path.Combine(directory, "screenshot-done")), "The visible map screenshot was not captured.");
        }
        async Task OpenMap(string name)
        {
            FocusRows(FocusState.Programmatic);
            await CaptureLayout();
            if (HasEditorFocus() || !CanShowMap(true))
                throw new InvalidOperationException("The torrent workspace is not ready for its global map shortcut.");
            firstPicture = 0;
            opening = Stopwatch.GetTimestamp();
            phaseStarted = opening;
            Volatile.Write(ref phase, name);
            input.Keys(0x11, 0x10, 0x4D);
            await Until(() => inspector.ShowsAllPeers && Model.IsInspectorMaximized,
                "Ctrl+Shift+M did not open the global map workspace.");
            await Until(() => InspectorContent.Content is InspectorPane pane
                && Read<PeerMap?>(pane, "_peerMap") is { } current && (map = current).MotionPictures > 0,
                "The map did not draw after Ctrl+Shift+M.");
            var motion = Read<MapMotion>(map!, "_motion");
            Observe(motion);
            await CaptureLayout();
            await Until(() => firstPicture != 0, "No XAML frame completed after the first visible map picture.");
            await Until(() => !inspector.IsLoading && !inspector.IsReplacing && inspector.Peers?.Count == data.PeerCount
                && inspector.Map.Capture().Observations.Length == data.PeerCount,
                "The global map did not receive all synthetic peers.");
            await Until(() => inspector.Map.Cities == cities && motion.ShowsConnections(inspector.Map.Subject, data.PeerCount),
                "The global map did not visibly show the requested connections.");
            await ViewDrawn();
            if (Read<int>(map!, "_arcLimit") != arcLimit)
                throw new InvalidOperationException("The global map did not use the requested arc limit.");
            outcomes.Add(new { journey = "overview destination grouping", sourceLocations = data.Locations.Count,
                destinations = inspector.Map.Destinations.Count, retainedArcs = motion.DrawnRoutes,
                locationMarkers = motion.KnownLocations, visibleMarkers = motion.DrawnLocations,
                cities, arcLimit, cityDestinationsReduced = cities ? inspector.Map.Destinations.Count < data.Locations.Count : (bool?)null });
            phases.Add(new { name, started = opening, ended = Stopwatch.GetTimestamp(), firstPicture });
            Volatile.Write(ref phase, string.Empty);
        }
        async Task Pan(string name, int milliseconds, bool right)
        {
            var drawing = (FrameworkElement)map!.FindName("Drawing");
            var left = Screen(drawing, new Point(24, drawing.ActualHeight / 2));
            var far = Screen(drawing, new Point(drawing.ActualWidth - 24, drawing.ActualHeight / 2));
            var from = right ? left : far; var to = right ? far : left;
            await Measure(name, input.Drag(milliseconds, from, to));
        }
        async Task PanDegrees(string name, double degrees)
        {
            var viewport = Read<MapViewport>(map!, "_viewport");
            var drawing = (FrameworkElement)map!.FindName("Drawing");
            var distance = degrees * viewport.AxisScale.X;
            var before = viewport.Center;
            var from = Screen(drawing, new Point(drawing.ActualWidth / 2 + distance / 2, drawing.ActualHeight / 2));
            var to = Screen(drawing, new Point(drawing.ActualWidth / 2 - distance / 2, drawing.ActualHeight / 2));
            await Measure(name, input.Drag(2000, from, to));
            await ViewDrawn();
            outcomes.Add(new { journey = name, requestedDegrees = degrees,
                actualDegrees = MapViewport.Wrap(viewport.Center.X - before.X, 0),
                before = new { longitude = before.X + viewport.Meridian, latitude = -before.Y },
                after = new { longitude = viewport.Center.X + viewport.Meridian, latitude = -viewport.Center.Y } });
        }
        async Task ViewDrawn()
        {
            await Until(() => Read<MapMotion>(map!, "_motion").DrawnView == Read<MapViewport>(map!, "_viewport").View,
                "The map did not draw the requested view.");
        }
        void ApplySummaries(PerformanceSource.Status status)
        {
            foreach (var summary in status.Torrents)
            {
                var torrent = targets.Single(torrent => torrent.TorrentId == summary.TorrentId);
                Set(torrent, nameof(Torrent.PeerCount), summary.Connected);
                Set(torrent, nameof(Torrent.SeedCount), summary.Seeds);
                Set(torrent, nameof(Torrent.DownloadRate), summary.DownloadRate);
                Set(torrent, nameof(Torrent.UploadRate), summary.UploadRate);
                torrent.RefreshText();
            }
            Torrents.RefreshView();
        }
        async Task StartUpdates()
        {
            if (feed is not null) return;
            var peers = InspectorContent.Content is InspectorPane pane && pane.FindName("Peers") is Syno.TableView.Table table
                ? VisibleRows(table).Length : 10;
            await PerformanceSource.SetViewport(data.Source.Pipe, new(Math.Max(10, VisibleRows(Torrents).Length), Math.Max(2, peers)));
            await File.WriteAllTextAsync(Path.Combine(directory, "source-go"), "");
            feed = Feed();
        }
        async Task Feed()
        {
            var tick = 0; var summary = 0;
            while (!feeding.IsCancellationRequested)
            {
                var status = await PerformanceSource.State(data.Source.Pipe, tick, feeding.Token);
                tick = status.Tick;
                var received = Stopwatch.GetTimestamp();
                if (status.Tick / 3 != summary)
                {
                    summary = status.Tick / 3;
                    ApplySummaries(status);
                }
                inspector.Observe(Read<string>(inspector, "_session"), false);
                updates.Add(new { phase, tick, status.Produced, received, applied = Stopwatch.GetTimestamp(),
                    connected = status.Torrents.Sum(torrent => torrent.Connected), connecting = status.Torrents.Sum(torrent => torrent.Connecting),
                    handshaking = status.Torrents.Sum(torrent => torrent.Handshaking), pendingDisconnect = status.Torrents.Sum(torrent => torrent.PendingDisconnect) });
            }
        }

        // Discard queued idle summaries before injecting the synthetic counts.
        Model.PauseCaptureSnapshots(true);
        inspector.CapturePeers = data.Peers;
        inspector.CaptureLocation = data.Location;
        inspector.CaptureOrigin = data.Origin;
        typeof(Inspector).GetField("_locating", flags)!.SetValue(inspector, true);
        Torrents.SelectionChanged += Selected;
        var pressed = new PointerEventHandler(Pressed);
        Torrents.AddHandler(UIElement.PointerPressedEvent, pressed, true);
        Microsoft.UI.Xaml.UnhandledExceptionEventHandler unhandled = (_, args) =>
            File.WriteAllText(Path.Combine(directory, "unhandled.log"), args.Exception.ToString());
        Application.Current.UnhandledException += unhandled;
        CompositionTarget.Rendered += Rendered;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "source.json.tmp"), JsonSerializer.Serialize(data.Source));
            File.Move(Path.Combine(directory, "source.json.tmp"), Path.Combine(directory, "source.json"), true);
            await Until(() => File.Exists(Path.Combine(directory, "source-ready")), "The external peer source did not start.");
            for (var index = 0; index < targets.Length; index++)
            {
                var torrent = targets[index]; var count = data.Leechers[index];
                Set(torrent, nameof(Torrent.KnownPeerCount), count * 3);
                Set(torrent, nameof(Torrent.SwarmSeedCount), count * 2);
                Set(torrent, nameof(Torrent.SwarmLeecherCount), count);
                Set(torrent, nameof(Torrent.State), TorrentState.Downloading);
                torrent.RefreshText();
            }
            ApplySummaries(await PerformanceSource.State(data.Source.Pipe));
            Torrents.SortInterval = TimeSpan.FromSeconds(3);
            Torrents.Layout = Torrents.Layout with { SortColumnId = "down", SortDirection = Syno.TableView.SortDirection.Descending };
            await CaptureLayout();
            await File.WriteAllTextAsync(Path.Combine(directory, "locations.json"), JsonSerializer.Serialize(
                data.Locations.Select(location => new { location.Code, city = location.City?.Name("en"), location.Point })));
            await File.WriteAllTextAsync(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new {
                frequency = Stopwatch.Frequency, width = AppWindow.ClientSize.Width, height = AppWindow.ClientSize.Height,
                scale = Root.XamlRoot.RasterizationScale, refreshHz = input.RefreshHz, detail = cities ? "cities" : "countries",
                locations, arcLimit,
                connections = data.Source.Connections, swapsPerSecond = data.Source.SwapsPerSecond,
                locationCountries = data.Locations.GroupBy(location => location.Code).Select(group => new { code = group.Key, locations = group.Count() }),
                torrents = targets.Select((torrent, index) => new { torrent.TorrentId, leechers = data.Leechers[index],
                    seeds = data.Leechers[index] * 2, rows = data.Leechers[index] * 3 }) }));
            await Until(() => File.Exists(Path.Combine(directory, "go")), "The display recorder did not start.", TimeSpan.FromMinutes(2));
            await Measure("window-baseline", input.Idle(500));
            if (ReviewMode != CaptureMode.PerformanceSelection)
            {
                await OpenMap("map-first-open");
                await Screenshot("ready-full-screen-map");
                await StartUpdates();
                var viewport = Read<MapViewport>(map!, "_viewport");
                viewport.Fit();
                await Pan("world-pan-right", 1000, true);
                await Pan("world-pan-left", 1000, false);
                viewport.Fit();
                await CaptureLayout();
                var home = viewport.Screen(data.Origin.Point!.Value);
                input.Position(Screen((FrameworkElement)map!.FindName("Drawing"), new Point(home.X, home.Y)));
                await Measure("origin-zoom-in", input.Zoom(2000, 2640));
                var zoomReleased = Stopwatch.GetTimestamp();
                await ViewDrawn();
                outcomes.Add(new { journey = "128x zoom target", expectedZoom = 128, actualZoom = viewport.Zoom,
                    reached = viewport.Zoom == 128, viewFeedbackMs = Stopwatch.GetElapsedTime(zoomReleased).TotalMilliseconds });
                var center = viewport.Coordinates(data.Origin.Point.Value);
                viewport.SetView(128, center, viewport.CalculateRatio(128, center.Y));
                await ViewDrawn();
                home = viewport.Screen(data.Origin.Point.Value);
                input.Position(Screen((FrameworkElement)map.FindName("Drawing"), new Point(home.X, home.Y)));
                await Measure("origin-zoom-out", input.Zoom(2000, -2640));
                await ViewDrawn();
                outcomes.Add(new { journey = "1x zoom target", expectedZoom = 1, actualZoom = viewport.Zoom, reached = viewport.Zoom == 1 });
                viewport.SetView(16, center, viewport.CalculateRatio(16, center.Y));
                await ViewDrawn();
                await PanDegrees("origin-16x-pan-east", 30);
                await PanDegrees("origin-16x-pan-return", -30);
                await PanDegrees("origin-16x-pan-west", -30);
                viewport.Fit();
                await ViewDrawn();
                var before = map.MotionFrames;
                await Measure("map-1x-animation", input.Idle(3000));
                outcomes.Add(new { journey = "1x moving dots", frames = map.MotionFrames - before, state = map.MotionState });
                await Screenshot("full-screen-map");
                if (ReviewMode == CaptureMode.PerformanceMap) return;
                await GoBack();
            }
            var inspectorOpened = Stopwatch.GetTimestamp();
            Model.OpenInspector();
            Model.IsInspectorMaximized = false;
            inspector.Select(InspectorSection.Peers);
            _splitHeight = Workspace.ActualHeight / 2;
            UpdateInspectorSize();
            await SelectTorrent(new Syno.TableView.Selection([], null));
            await Until(() => InspectorContent.Content is InspectorPane { IsLoaded: true } current
                && current.FindName("Peers") is FrameworkElement, "The expanded Peers inspector did not load its controls.");
            var pane = (InspectorPane)InspectorContent.Content;
            pane.Layout = pane.Layout with { View = PeerView.Default with { Side = .5, Cities = cities, ArcLimit = arcLimit } };
            await CaptureLayout();
            var table = (FrameworkElement)pane.FindName("Peers");
            if (Grid.GetColumn(table) != 2) throw new InvalidOperationException("The peer table must be right of the map.");
            await Connections("inspector-all-map-open", inspectorOpened, data.PeerCount);
            var peersTable = (Syno.TableView.Table)table;
            peersTable.SortInterval = TimeSpan.FromSeconds(1);
            peersTable.Layout = peersTable.Layout with { SortColumnId = "down", SortDirection = Syno.TableView.SortDirection.Descending };
            await Screenshot("no-selection-map");
            await Measure("no-selection-animation", input.Idle(3000));
            var surface = Read<ListView>(Torrents, "_surface");
            var heavy = targets.Single(torrent => torrent.TorrentId == data.Source.Torrents.MaxBy(torrent => torrent.Connections)!.TorrentId);
            var selectedCount = data.Leechers[Array.IndexOf(targets, heavy)] * 3;
            var firstRow = (FrameworkElement)surface.ContainerFromIndex(0);
            lastPressTime = 0;
            var initial = await input.Run(input.Clicks([Screen(firstRow, new Point(32, firstRow.ActualHeight / 2))], 150), directory);
            await Until(() => lastPressTime != 0, "The calibration click did not reach the torrent table.");
            inputOffset = lastPressTime - initial.ClickTimes[0];
            if (Math.Abs(inputOffset) > 100000) throw new InvalidOperationException("The pointer and sender uptime clocks did not agree within 100 ms.");
            var heavyOpened = Stopwatch.GetTimestamp();
            await SelectTorrent(new Syno.TableView.Selection([heavy], heavy));
            await Connections("inspector-selected-map-open", heavyOpened, selectedCount);
            await Measure("selected-map-baseline", input.Idle(1000));
            await Screenshot("selected-map");
            await StartUpdates();
            await Measure("peer-speed-sorting", input.Idle(6000));
            var positions = Enumerable.Range(0, 7).Select(index => {
                var row = (FrameworkElement?)surface.ContainerFromIndex(index)
                    ?? throw new InvalidOperationException("The seven click rows must be visible.");
                return Screen(row, new Point(32, row.ActualHeight / 2));
            }).ToArray();
            for (var index = 0; index < positions.Length * 3; index++) clicks.Add(new PerformanceClick {
                Position = positions[index % positions.Length], Visit = index / positions.Length + 1 });
            selecting = true;
            await Measure("torrent-clicks", input.Clicks(clicks.Select(click => click.Position).ToArray(), 150));
            var released = Stopwatch.GetTimestamp();
            while (lastPressTime < clicks[^1].InputTime - 50000 && Stopwatch.GetElapsedTime(released).TotalSeconds < 2)
                await Task.Delay(10);
            await CaptureLayout();
            selecting = false;
            await SelectTorrent(new Syno.TableView.Selection([heavy], heavy));
            await Until(() => ReferenceEquals(((Syno.TableView.Table)table).ItemsSource, inspector.ShownPeers)
                && inspector.ShownPeers?.Count == selectedCount, "The final peer table did not publish.");
            await Connections("selection-map-settlement", released, selectedCount);
            outcomes.Add(new { journey = "heavy detail after clicks", ms = Stopwatch.GetElapsedTime(released).TotalMilliseconds,
                peers = inspector.Peers?.Count, mapWidth = Read<PeerMap>(pane, "_peerMap").ActualWidth, tableWidth = table.ActualWidth });
            await Screenshot("marquee-map");
            overlay = Read<FrameworkElement>(Torrents, "_marqueeOverlay");
            // A missed gutter must not turn a benchmark gesture into a queue move.
            Torrents.CanReorder = false;
            var from = Screen(surface, new Point(surface.ActualWidth - 32, 40));
            var to = Screen(surface, new Point(24, surface.ActualHeight - 40));
            await Measure("torrent-marquee", input.Drag(2000, from, to));
            overlay = null;
            await SelectTorrent(new Syno.TableView.Selection([heavy], heavy));
            await Connections("churn-map-open", Stopwatch.GetTimestamp(), selectedCount);
            await File.WriteAllTextAsync(Path.Combine(directory, "source-churn"), "");
            var dockedViewport = Read<MapViewport>(map!, "_viewport");
            dockedViewport.Fit();
            await ViewDrawn();
            var dockedHome = dockedViewport.Screen(data.Origin.Point!.Value);
            input.Position(Screen((FrameworkElement)map!.FindName("Drawing"), new Point(dockedHome.X, dockedHome.Y)));
            await Measure("docked-live-zoom-in", input.Zoom(2000, 2640));
            await Measure("docked-live-zoom-out", input.Zoom(2000, -2640));
            var dockedCenter = dockedViewport.Coordinates(data.Origin.Point.Value);
            dockedViewport.SetView(16, dockedCenter, dockedViewport.CalculateRatio(16, dockedCenter.Y));
            await ViewDrawn();
            await PanDegrees("docked-live-pan-east", 30);
            await PanDegrees("docked-live-pan-return", -30);
            await PanDegrees("docked-live-pan-west", -30);
            await Measure("peer-connection-churn", input.Idle(10000));
        }
        finally
        {
            if (phase.Length > 0) phases.Add(new { name = phase, started = phaseStarted, ended = Stopwatch.GetTimestamp(), failed = true });
            feeding.Cancel();
            Exception? feedFailure = null;
            if (feed is not null)
                try { await feed; }
                catch (OperationCanceledException) when (feeding.IsCancellationRequested) { }
                catch (Exception error) { feedFailure = error; }
            Torrents.SelectionChanged -= Selected;
            Torrents.RemoveHandler(UIElement.PointerPressedEvent, pressed);
            Application.Current.UnhandledException -= unhandled;
            CompositionTarget.Rendered -= Rendered;
            foreach (var (motion, drawn) in motions)
            {
                motion.Control.Draw -= drawn;
                motion.PicturePublished = null;
            }
            foreach (var observed in inputMaps) observed.CaptureInput = null;
            inspector.CapturePeers = null; inspector.CaptureLocation = null; inspector.CaptureOrigin = null;
            typeof(Inspector).GetField("_locating", flags)!.SetValue(inspector, false);
            Torrents.CanReorder = reorder;
            Model.PauseCaptureSnapshots(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "timing.json"), JsonSerializer.Serialize(new {
                phases, samples, updates, tables, feedFailure = feedFailure?.ToString(), pictures = pictures.ToArray(), mapFrames = mapFrames.ToArray(), work = work.ToArray(),
                received = received.ToArray(),
                fixture = new { detail = cities ? "cities" : "countries", expected = data.PeerCount, summaries = targets.Sum(torrent => torrent.PeerCount),
                    parsed = inspector.Peers?.Count, observations = inspector.Map.Capture().Observations.Length,
                    inspector.Map.Cities, inspector.IsLoading, inspector.IsReplacing, motion = map?.MotionState },
                presses, inputOffset,
                clicks = clicks.Select(click => {
                    var matches = presses.Where(press => Math.Abs(press.InputTime - click.InputTime) < 50000).ToArray();
                    var press = matches.Length == 1 ? matches[0] : null;
                    return new { press?.TorrentId, click.Visit, sent = click.Sent, inputTime = click.InputTime,
                        matches = matches.Length, superseded = press?.Superseded ?? false,
                        received = press?.Received ?? 0, selected = press?.Selected ?? 0, rendered = press?.Rendered ?? 0,
                        alreadySelected = press?.AlreadySelected ?? false };
                }) }));
            if (feedFailure is not null) throw feedFailure;
        }
    }

    private sealed class PerformanceClick
    {
        internal Vector2 Position { get; init; }
        internal int Visit { get; init; }
        internal long Sent, InputTime;
    }

    private sealed class PerformancePress
    {
        public required string TorrentId { get; init; }
        public long InputTime { get; init; }
        public long Received { get; init; }
        public bool AlreadySelected { get; init; }
        internal bool Superseded;
        public long Selected { get; set; }
        public long Rendered { get; set; }
    }
}

internal sealed class PerformanceData
{
    internal int[] Leechers { get; }
    internal int PeerCount => Leechers.Sum() * 3;
    internal IReadOnlyList<Location> Locations { get; }
    internal PerformanceSource.Configuration Source { get; }
    private readonly Dictionary<string, Location> _locations = [];
    internal Location Origin { get; } = new("GB", new Place("GB", null), null, new GeoPoint(0, 51.5));

    internal PerformanceData(Torrent[] torrents, int[] leechers, int locationCount, IReadOnlyDictionary<string, Geography.Country> world,
        int connections, int swapsPerSecond)
    {
        Leechers = leechers;
        Source = PerformanceSource.Create(torrents.Select(torrent => torrent.TorrentId).ToArray(),
            leechers.Select(count => count * 3).ToArray(), leechers.Select(count => count * 2).ToArray(), connections, swapsPerSecond);
        var countries = world.Values.Where(country => country.Code != "AQ" && country.Areas.Length > 0)
            .OrderBy(country => country.Code, StringComparer.Ordinal).ToArray();
        Locations = countries.SelectMany(country => country.Areas.Select((area, index) => new { country, area, index }))
            .OrderBy(place => place.index).ThenBy(place => place.country.Code, StringComparer.Ordinal)
            .DistinctBy(place => (place.country.Code, place.area.Point)).Take(locationCount)
            .Select(place => new Location(place.country.Code, new Place(place.country.Code, null),
                new Place(null, new Dictionary<string, string> { ["en"] = place.area.Name }), place.area.Point)).ToArray();
        if (Locations.Count != locationCount)
            throw new InvalidOperationException("The bundled countries do not supply enough distinct benchmark locations.");
        for (var torrent = 0; torrent < torrents.Length; torrent++)
            for (var row = 0; row < Leechers[torrent] * 3; row++)
                _locations.Add(PerformanceSource.Endpoint(torrent, row), Locations[(row + torrent * 3) % Locations.Count]);
    }
    internal Task<JsonElement> Peers(Torrent torrent) => PerformanceSource.Peers(Source.Pipe, torrent.TorrentId);
    internal Location? Location(string endpoint) => _locations.GetValueOrDefault(endpoint);
}
