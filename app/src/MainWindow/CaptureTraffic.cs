using System.Text.Json;
using Microsoft.UI.Xaml;
using Syno.TinyTorrent.Models;
using Windows.Graphics;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task CaptureTraffic(Torrent target, List<object> outcomes, List<string> completed)
    {
        var store = Model.DataDirectory ?? throw new InvalidOperationException("The traffic capture has no store.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(store, "traffic-capture.json")));
        var fixture = document.RootElement;
        var destination = fixture.GetProperty("destination").GetString();
        if (Model.Torrents.Count != 1 || target.TorrentId != fixture.GetProperty("target").GetString() ||
            target.Size != fixture.GetProperty("size").GetInt64() ||
            !string.Equals(Path.GetFullPath(target.SavePath), destination, StringComparison.OrdinalIgnoreCase) ||
            Model.Preferences.Interface.Input != fixture.GetProperty("interface").GetString())
            throw new InvalidOperationException("The traffic capture does not match its disposable loopback fixture.");
        await CaptureReady(target, () => target.Downloaded >= 524288 && target.DownloadRate > 0 && target.Peers > 0 && target.Progress < 1);
        await ShowTorrents();
        Model.Filter = TorrentFilter.All;
        Model.IsFilterOpen = false;
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        var scale = Root.XamlRoot.RasterizationScale;
        var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
        var started = target.Downloaded;
        foreach (var language in new[] { "en", "es" })
        foreach (var theme in new[] { "light", "dark" })
        {
            await Model.Preferences.SelectTheme(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
            foreach (var size in new[] { new SizeInt32(720, 560), new SizeInt32(1040, 680), new SizeInt32(1280, 800) })
            {
                AppWindow.Resize(new SizeInt32(Math.Max((int)(size.Width * scale), minimum), (int)(size.Height * scale)));
                var prefix = "traffic-" + language + "-" + theme + "-" + size.Width + "x" + size.Height;
                Model.CloseInspector();
                await CaptureReady(target, () => !target.IsPaused && !target.IsError && target.DownloadRate > 0 && target.Peers > 0 && target.Progress < 1);
                await CapturePage(prefix + "-workspace");
                completed.Add(prefix + "-workspace");
                Run(Model.Properties);
                foreach (var section in new[] { InspectorSection.General, InspectorSection.Peers, InspectorSection.Pieces, InspectorSection.Speed })
                {
                    Model.Inspector.Select(section);
                    await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && !Model.Inspector.HasError && (section switch
                    {
                        InspectorSection.General => Model.Inspector.PieceSize > 0 && Model.Inspector.Folder.Length > 0,
                        InspectorSection.Peers => Model.Inspector.Peers is not null && Model.Inspector.Peers.Any(peer => peer.DownloadRate > 0),
                        InspectorSection.Pieces => Model.Inspector.Pieces is { MetadataReady: true, Peers: > 0 } pieces &&
                            pieces.Verified.Any(value => value) && pieces.Verified.Any(value => !value),
                        InspectorSection.Speed => Model.Inspector.History is not null && Model.Inspector.History.Count(sample => sample.DownloadRate > 0) >= 2,
                        _ => false
                    }));
                    if (target.IsPaused || target.IsError || target.DownloadRate <= 0 || target.Progress >= 1 ||
                        Model.Inspector.Peers is not null && Model.Inspector.Peers.Any(peer => !peer.Endpoint.StartsWith("127.0.0.1:", StringComparison.Ordinal)))
                        throw new InvalidOperationException("The inspector capture no longer has an active loopback transfer.");
                    await CapturePage(prefix + "-" + section, InspectorContent.Content as FrameworkElement);
                    completed.Add(prefix + "-" + section);
                    outcomes.Add(new { scene = prefix, section = section.ToString(), target.Downloaded, target.DownloadRate, target.Progress,
                        peers = Model.Inspector.Peers?.Count ?? 0, pieces = Model.Inspector.Pieces?.Count,
                        verified = Model.Inspector.Pieces?.Verified.Count(value => value), samples = Model.Inspector.History?.Count ?? 0 });
                }
            }
        }
        if (target.Downloaded <= started || Model.Torrents.Count != 1 || target.Progress >= 1)
            throw new InvalidOperationException("The traffic capture did not retain an advancing incomplete fixture.");
        outcomes.Add(new { journey = "live loopback inspector", started, finished = target.Downloaded, scenes = completed.Count,
            scope = "Real rate-limited local payload, peer rows, mixed pieces and speed history; XAML scenes exclude desktop chrome and acrylic." });
        Model.CloseInspector();
    }
}
