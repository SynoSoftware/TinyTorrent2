using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Torrents;
using Windows.Graphics;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureMemory(List<object> outcomes)
    {
        AppWindow.Resize(new SizeInt32(1040, 680));
        await ShowTorrents();
        Model.CloseInspector();
        await SampleMemory("startup", outcomes);
        await TourSettings();
        await SampleMemory("settings-open", outcomes);
        await ShowTorrents();
        await Task.Delay(10000);
        await SampleMemory("settings-closed", outcomes);
        await TourSettings();
        await SampleMemory("settings-repeated", outcomes);
        for (var pass = 1; pass <= 5; pass++)
        {
            await TourSettings();
            await ShowConnection();
            await SampleMemory($"browse-{pass}-connection", outcomes);
            await Model.Navigation.Navigate(WindowPage.Library);
            await SampleMemory($"browse-{pass}-library", outcomes);
            await ShowTorrents();
            await SampleMemory($"browse-{pass}-torrents", outcomes);
            await ShowConnections(true);
            await CaptureReady(
                Model.Inspector,
                () => !Model.Inspector.IsLoading && Model.Inspector.Peers is not null
            );
            await Task.Delay(1000);
            await SampleMemory($"browse-{pass}-map", outcomes);
            Model.Inspector.Select(InspectorSection.Speed);
            await CaptureReady(
                Model.Inspector,
                () => !Model.Inspector.IsLoading && Model.Inspector.History is not null
            );
            await SampleMemory($"browse-{pass}-speed", outcomes);
            await GoBack();
            await ShowAbout();
            await ShowCredits();
            await SampleMemory($"browse-{pass}-credits", outcomes);
            await ShowAbout();
            await Task.Delay(2200);
            await SampleMemory($"browse-{pass}-about", outcomes);
        }
        await CapturePeers(Model.Torrents.First(), outcomes, []);
        await ShowAbout();
        await Task.Delay(2200);
        await SampleMemory("after-peers", outcomes);
        await CaptureUi("memory-about");
        for (var cycle = 1; cycle <= 8; cycle++)
        {
            await ShowSettings(new(SettingsCategory.Network));
            await ShowConnection();
            await ShowTorrents();
            await ShowConnections(true);
            await CaptureReady(
                Model.Inspector,
                () => !Model.Inspector.IsLoading && Model.Inspector.Peers is not null
            );
            await Task.Delay(1000);
            Model.Inspector.Select(InspectorSection.Speed);
            await CaptureReady(
                Model.Inspector,
                () => !Model.Inspector.IsLoading && Model.Inspector.History is not null
            );
            await GoBack();
        }
        await SampleMemory("cycled-8", outcomes);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await SampleMemory("cycled-8-collected", outcomes);

        async Task TourSettings()
        {
            foreach (var category in Enum.GetValues<SettingsCategory>())
            {
                await ShowSettings(new(category));
                await CaptureLayout();
                await Task.Delay(300);
            }
        }
    }

    private async Task CaptureTableMemory(
        Inspector inspector,
        InspectorPane pane,
        List<object> outcomes
    )
    {
        var original = inspector.Peers!;
        var table = (Syno.TableView.Table)pane.FindName("Peers");
        var apply = typeof(Inspector).GetMethod(
            "ApplyPeers", BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var property = typeof(Inspector).GetProperty(nameof(Inspector.Peers))!;
        pane.Layout = pane.Layout with { View = PeerView.Default with { Layout = PeerLayout.Table } };
        Model.IsInspectorMaximized = true;
        foreach (var count in new[] { 100, 1000, 10000 })
        {
            property.SetValue(inspector, null);
            await (Task<bool>)apply.Invoke(inspector, [
                original[0].TorrentId, PeerRows(count), new Dictionary<string, Peer[]>(),
                typeof(Inspector).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(inspector)!
            ])!;
            for (var index = 0; index < count; index++)
                inspector.Peers![index].Location = original[index % original.Count].Location;
            inspector.Refresh();
            table.ScrollTo(0);
            await SampleMemory($"peers-{count}", outcomes, table);
            var surface = CaptureElements(table).OfType<ListView>().Single();
            var scroll = CaptureElements(surface).OfType<ScrollViewer>().First();
            var allocated = GC.GetTotalAllocatedBytes(false);
            for (var step = 1; step <= 10; step++)
            {
                table.ScrollTo(scroll.ScrollableHeight * step / 10);
                await CaptureLayout();
                await Task.Delay(100);
            }
            outcomes.Add(new
            {
                journey = "peer table scrolling",
                peers = count,
                allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated,
            });
            await SampleMemory($"peers-{count}-scrolled", outcomes, table);
        }
        for (var pass = 1; pass <= 6; pass++)
        {
            InspectorContent.Content = null;
            await SampleMemory($"peers-unloaded-{pass}", outcomes, table);
            InspectorContent.Content = pane;
            await SampleMemory($"peers-reloaded-{pass}", outcomes, table);
        }
    }

    private async Task SampleMemory(
        string stage,
        List<object> outcomes,
        Syno.TableView.Table? table = null
    )
    {
        await CaptureLayout();
        using var process = Process.GetCurrentProcess();
        var sample = new
        {
            stage,
            pid = process.Id,
            time = DateTimeOffset.UtcNow,
            privateBytes = process.PrivateMemorySize64,
            workingSet = process.WorkingSet64,
            managedBytes = GC.GetTotalMemory(false),
            gcCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes,
            gen2Collections = GC.CollectionCount(2),
            aboutUiMemory = Model.IsAbout ? Model.UiMemory : null,
            tableElements = table is null ? null
                : CaptureElements(table)
                    .GroupBy(element => element.GetType().Name)
                    .ToDictionary(group => group.Key, group => group.Count()),
        };
        outcomes.Add(sample);
        var directory = Environment.GetEnvironmentVariable("TINYTORRENT_MEMORY_AUDIT");
        if (directory is null)
            return;
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Memory checkpoints require an absolute directory.");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, stage + ".json"),
            JsonSerializer.Serialize(sample)
        );
        await File.WriteAllTextAsync(Path.Combine(directory, "ready.txt"), stage);
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, stage + ".continue")))
        {
            if (elapsed.Elapsed > TimeSpan.FromMinutes(2))
                throw new TimeoutException("The memory profiler did not continue " + stage);
            await Task.Delay(100);
        }
    }
}
