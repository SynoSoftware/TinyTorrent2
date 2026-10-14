using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;
using Windows.Graphics;

namespace Syno.TinyTorrent;

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
        await CaptureReady(inspector, () => !inspector.IsLoading && inspector.Peers is not null && !inspector.HasError);
        await Model.Select([], null);
        await CaptureReady(inspector, () => inspector.IsGlobal && !inspector.IsLoading && inspector.Peers is not null && !inspector.HasError);
        inspector.Select(InspectorSection.Speed);
        await CaptureReady(inspector, () => inspector.IsGlobal && !inspector.IsLoading && inspector.History is not null && !inspector.HasError);
        outcomes.Add(new { journey = "engine-backed global peers and empty-selection speed", samples = inspector.History!.Count });
        inspector.Select(InspectorSection.Peers);
        await CaptureReady(inspector, () => !inspector.IsLoading && inspector.Peers is not null && !inspector.HasError);
        if (Model.Geolocation.Find(IPAddress.Parse("8.8.8.8")) is null)
            throw new InvalidOperationException("The bundled country database did not resolve the capture address.");

        var countries = await Geography.Countries;
        var codes = new[] { "US", "CA", "BR", "GB", "FR", "NL", "DE", "RU", "IN", "CN", "JP", "AU", "ZA", "SG" };
        var peers = Enumerable.Range(0, 100).Select(index =>
        {
            var code = codes[index % codes.Length];
            var country = countries[Geography.Code(code)];
            var area = country.Areas.Length == 0 ? null : country.Areas[index / codes.Length % country.Areas.Length];
            var place = new Place(code, new Dictionary<string, string> { ["en"] = new RegionInfo(code).EnglishName });
            var city = area is null ? null : new Place(null, new Dictionary<string, string> { ["en"] = area.Name });
            var peer = new Peer(Model.Text, Model.Geolocation, JsonSerializer.SerializeToElement(new
            {
                endpoint = $"192.0.2.{index + 1}:51413", client = "Capture fixture", transport = "tcp",
                incoming = false, state = "connected", encryption = "rc4", sources = new[] { "tracker" },
                interested = true, choked = false, remote_interested = true, remote_choked = false,
                read_wait = Array.Empty<string>(), write_wait = Array.Empty<string>(), progress = .65,
                download_rate = index % 3 == 0 ? 0 : (index % 7 + 1) * 32768,
                upload_rate = index % 3 == 0 ? 0 : (index % 5 + 1) * 24576,
                downloaded = 1048576, uploaded = 524288,
            }), target.TorrentId);
            typeof(Peer).GetField("_location", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(peer, new Location(code, place, city, area?.Point ?? country.Anchor));
            typeof(Peer).GetField("_locationVersion", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(peer, Model.Geolocation.Version);
            return peer;
        }).ToArray();
        var origin = new Location("GB", new Place("GB", null), null, new GeoPoint(-.12, 51.5));
        var fetching = typeof(Inspector).GetField("_fetching", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originField = typeof(Inspector).GetField("_origin", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originKey = typeof(Inspector).GetField("_originKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalOrigin = originField.GetValue(inspector);
        var originalKey = originKey.GetValue(inspector);
        var pane = (InspectorPane)InspectorContent.Content;
        var original = inspector.Peers;
        var locations = peers.Select(peer => peer.Location!).ToArray();
        // Keep the disposable engine's empty snapshots from replacing visual fixtures.
        fetching.SetValue(inspector, true);
        originField.SetValue(inspector, origin);
        originKey.SetValue(inspector, (Model.PublicAddress, Model.Geolocation.Version));
        try
        {
            typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, peers);
            foreach (var theme in new[] { "dark", "light" })
            {
                await Model.Settings.SelectTheme(theme);
                await CaptureReady(Model, () => Model.Theme == theme && Model.CanClose);
                foreach (var precision in new[] { "countries", "coordinates", "mixed" })
                {
                    for (var index = 0; index < peers.Length; index++)
                    {
                        var location = locations[index];
                        if (precision == "countries" || precision == "mixed" && index % 3 == 0)
                            location = new Location(location.Code, location.Country, null, null);
                        else if (index == 0)
                            location = new Location(location.Code, location.Country, null, location.Point);
                        typeof(Peer).GetField("_location", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .SetValue(peers[index], location);
                    }
                    foreach (var portrait in new[] { false, true })
                    {
                        AppWindow.Resize(portrait ? new SizeInt32(760, 1100) : new SizeInt32(1280, 900));
                        await CaptureLayout();
                        Split.SetValue(portrait ? 800 : 360);
                        inspector.Refresh();
                        pane.Layout = pane.Layout with { View = PeerView.Default };
                        foreach (var layout in new[] { PeerLayout.Map, PeerLayout.Both, PeerLayout.Table })
                        {
                            var showMap = (ToggleButton)pane.FindName("ShowPeerMap");
                            var showTable = (ToggleButton)pane.FindName("ShowPeerTable");
                            SetToggle(showMap, true);
                            SetToggle(showTable, layout != PeerLayout.Map);
                            SetToggle(showMap, layout != PeerLayout.Table);
                            await CaptureLayout();
                            if (pane.Layout.View.Layout != layout)
                                throw new InvalidOperationException("The peer visibility buttons did not apply their choice.");
                            if (layout == PeerLayout.Map && showMap.IsEnabled || layout == PeerLayout.Table && showTable.IsEnabled)
                                throw new InvalidOperationException("The last peer view must remain visible.");
                            var map = CaptureElements(pane).OfType<PeerMap>().SingleOrDefault();
                            map?.Show(peers, origin, Model.Text);
                            var name = $"peers-{theme}-{precision}-{(portrait ? "portrait" : "landscape")}-{layout}";
                            await CaptureUi(name);
                            completed.Add(name);
                            if (layout == PeerLayout.Both)
                            {
                                var splitter = (Splitter)pane.FindName("PeerSplit");
                                var orientation = splitter.Orientation;
                                var before = splitter.Value;
                                splitter.SetValue(before * .75);
                                await CaptureLayout();
                                var after = orientation == Orientation.Horizontal ? pane.Layout.View.Stacked : pane.Layout.View.Side;
                                if (splitter.Value == before || after is < .15 or > .85 || splitter.Orientation != orientation)
                                    throw new InvalidOperationException("The peer splitter must resize without changing orientation.");
                                map?.Show(peers, origin, Model.Text);
                                await CaptureUi(name + "-resized");
                                completed.Add(name + "-resized");
                            }
                        }
                    }
                }
            }
            AppWindow.Resize(new SizeInt32(760, 600));
            await CaptureLayout();
            Split.SetValue(300);
            pane.Layout = pane.Layout with { View = pane.Layout.View with { Layout = PeerLayout.Both, Stacked = .5 } };
            await CaptureLayout();
            CaptureElements(pane).OfType<PeerMap>().Single().Show(peers, null, Model.Text);
            await CaptureUi("peers-small-unknown-origin");
            completed.Add("peers-small-unknown-origin");
            outcomes.Add(new { journey = "native peer map and layout review", peers = peers.Length,
                scope = "Synthetic country-only, coordinate and mixed records in the real inspector; real country database lookup, header toggles and splitter owner; no public transfers." });
        }
        finally
        {
            typeof(Inspector).GetProperty(nameof(Inspector.Peers))!.SetValue(inspector, original);
            originField.SetValue(inspector, originalOrigin);
            originKey.SetValue(inspector, originalKey);
            fetching.SetValue(inspector, false);
            inspector.Refresh();
            Model.CloseInspector();
        }

        static void SetToggle(ToggleButton button, bool value)
        {
            if (button.IsChecked == value)
                return;
            var automation = FrameworkElementAutomationPeer.CreatePeerForElement(button);
            ((IToggleProvider)automation.GetPattern(PatternInterface.Toggle)).Toggle();
        }
    }
}
