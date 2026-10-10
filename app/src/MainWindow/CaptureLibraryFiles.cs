using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Library;
using Syno.TinyTorrent.Services;
using Windows.Graphics;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task CaptureLibraryFiles(List<object> outcomes, List<string> completed)
    {
        var fixture = Path.Combine(Model.DataDirectory!, "library-timing.json");
        if (File.Exists(fixture))
        {
            await CaptureLibraryTiming(fixture, outcomes, completed);
            return;
        }
        await Navigate(WindowPage.Library);
        await CaptureReady(Model.Library, () => Model.Library.IsAvailable && Model.Library.Rows.Count == 2);
        Model.Library.Select(Model.Library.Rows[0]);
        Model.Library.Close();
        Model.Library.Properties.Execute(null);
        await CaptureReady(Model.Library, () => Model.Library.Origins.Count == 1);
        foreach (var language in new[] { "en", "es" })
        foreach (var theme in new[] { "light", "dark" })
        {
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.Text.Language == language);
            await Model.Settings.SelectTheme(theme);
            var scale = Root.XamlRoot.RasterizationScale;
            var minimum = ((OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
            AppWindow.Resize(new SizeInt32(Math.Max((int)((theme == "light" ? 1120 : 800) * scale), minimum), (int)(760 * scale)));
            var name = $"library-files-{language}-{theme}";
            await CapturePage(name);
            completed.Add(name);
            Model.Library.Query = "readme";
            await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 0 &&
                Model.Library.Counts.GetValueOrDefault(LibraryConfiguration.Files) == 1);
            await CapturePage(name + "-empty");
            completed.Add(name + "-empty");
            Model.Library.Query = string.Empty;
            await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 2);
            Model.Library.Select(Model.Library.Rows[0]);
            await CaptureReady(Model.Library, () => Model.Library.Origins.Count == 1);
        }
        Model.Library.Configuration = LibraryConfiguration.Music;
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 1);
        Model.Library.Configuration = LibraryConfiguration.Files;
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 4);
        Model.Library.Query = "readme";
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 1);
        Model.Library.Query = string.Empty;
        Model.Library.Filter("kind", ((int)FileKind.Document).ToString());
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 1);
        Model.Library.Configuration = LibraryConfiguration.Videos;
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 2);
        Model.Library.Configuration = LibraryConfiguration.Files;
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 1);
        Model.Library.Select(Model.Library.Rows[0]);
        await CapturePage("library-file-inspector");
        completed.Add("library-file-inspector");
        await ShowSettings(new(SettingsCategory.Subtitles));
        await CapturePage("subtitle-settings");
        completed.Add("subtitle-settings");
        var supplier = ShowSupplier();
        try
        {
            await CaptureLayout();
            if (_interaction?.Dialog is null)
                throw new InvalidOperationException("The supplier editor bypassed the window's dialog owner.");
            foreach (var language in new[] { "en", "es" })
            {
                Model.SelectLanguage(language);
                await CaptureReady(Model, () => Model.Text.Language == language);
                await Model.Settings.SelectTheme(language == "en" ? "light" : "dark");
                await CapturePage("subtitle-supplier-" + language);
                completed.Add("subtitle-supplier-" + language);
            }
        }
        finally
        {
            _interaction?.Dialog?.Hide();
            await supplier;
        }
        await GoBack();
        if (Model.Page != WindowPage.Library || Model.Library.Rows.Count != 1)
            throw new InvalidOperationException("Settings did not return to the retained Library view.");
        var consent = EnableVideoInformation();
        try
        {
            await CapturePage("library-consent");
            completed.Add("library-consent");
        }
        finally
        {
            _interaction?.Dialog?.Hide();
            await consent;
        }
        outcomes.Add(new { journey = "Library local files", videos = 2, music = 1, files = 4,
            filtersRetained = true, source = "existing pipe, verified fixture files, production SQLite" });
        if (Model.Library.Providers.FirstOrDefault(entry => entry.ProviderId == "public") is not { } option)
            return;
        await Model.Library.SelectProvider(option.ProviderId);
        // Without a delay the website provider asks only on request, so the review sends no requests.
        if (Model.Library.Provider is { } provider)
            await Model.Library.Configure(provider, new Dictionary<string, string> { ["delay"] = "0" });
        await Model.Library.SetEnabled(true);
        Model.Library.Configuration = LibraryConfiguration.Videos;
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 2 && Model.Library.IsEnrichmentEnabled);
        await Navigate(WindowPage.Torrents);
        await Navigate(WindowPage.Library);
        Model.Library.Select(Model.Library.Rows.First(row => row.Name.Contains("Coastal", StringComparison.Ordinal)));
        await CaptureReady(Model.Library, () => Model.Library.Origins.Count == 1);
        foreach (var language in new[] { "en", "es" })
        foreach (var theme in new[] { "light", "dark" })
        {
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.Text.Language == language);
            await Model.Settings.SelectTheme(theme);
            var name = $"information-details-{language}-{theme}";
            var sections = CaptureElements(InspectorContent).OfType<SelectorBar>()
                .Single(section => AutomationProperties.GetAutomationId(section) == "InspectorSections");
            var information = sections.Items.Single(item => AutomationProperties.GetAutomationId(item) == "LibraryInformation");
            sections.SelectedItem = information;
            await CaptureLayout();
            var fetch = CaptureElements(InspectorContent).OfType<ActionButton>()
                .SingleOrDefault(button => ReferenceEquals(button.Command, Model.Library.FetchDetails));
            if (!ReferenceEquals(sections.SelectedItem, information) || sections.ActualWidth <= 0 ||
                fetch is not { ActualWidth: > 0, ActualHeight: > 0 })
                throw new InvalidOperationException("Library information did not show its sections and Fetch action.");
            await CapturePage(name);
            completed.Add(name);
            var configure = ConfigureVideo();
            try
            {
                await CaptureLayout();
                if (_interaction?.Dialog is null)
                    throw new InvalidOperationException("The information editor bypassed the window's dialog owner.");
                name = $"information-configure-{language}-{theme}";
                await CapturePage(name);
                completed.Add(name);
            }
            finally
            {
                _interaction?.Dialog?.Hide();
                await configure;
            }
        }
        outcomes.Add(new { journey = "Information source controls", source = option.ProviderId, delay = 0,
            networkRequests = "none; capture reviews presentation only" });
        await Model.Library.PrepareTiming();
        Model.SelectLanguage("en");
        await CaptureReady(Model, () => Model.Text.Language == "en");
        await Model.Settings.SelectTheme("light");
        AppWindow.Resize(new SizeInt32((int)(1120 * Root.XamlRoot.RasterizationScale), (int)(760 * Root.XamlRoot.RasterizationScale)));
        Model.Library.Refresh();
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 2 &&
            Model.Library.Rows.All(row => row.Entry.Type == "movie"));
        Model.Library.Select(Model.Library.Rows[0]);
        await CaptureReady(Model.Library, () => Model.Library.Cast.Length > 0 && Model.Library.Synopsis.Length > 0);
        foreach (var language in new[] { "en", "es" })
        foreach (var theme in new[] { "light", "dark" })
        foreach (var width in new[] { 1280, 960, 800 })
        {
            Model.IsFilterOpen = width == 800;
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.Text.Language == language);
            await Model.Settings.SelectTheme(theme);
            var scale = Root.XamlRoot.RasterizationScale;
            var minimum = ((OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
            AppWindow.Resize(new SizeInt32(Math.Max((int)(width * scale), minimum), (int)(760 * scale)));
            var name = $"library-identified-{language}-{theme}-{width}";
            await CapturePage(name);
            completed.Add(name);
        }
        var heading = (Controls.Section)CaptureElements(InspectorContent).OfType<Details>().Single().Content;
        heading.Header = "Coastal mystery: the disappearance of the fishing boat and the secrets of the old harbour";
        await CapturePage("library-long-heading");
        completed.Add("library-long-heading");
        Model.Library.Configuration = LibraryConfiguration.Music;
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 1 && Model.Library.Rows[0].Artist.Length > 0);
        Model.Library.Select(Model.Library.Rows[0]);
        await CapturePage("library-music");
        completed.Add("library-music");
        outcomes.Add(new { journey = "Populated information layout", source = "local diagnostic database facts",
            networkRequests = "none; fixture data, not supplier or Windows metadata evidence" });
        await Navigate(WindowPage.Torrents);
        Model.IsFilterOpen = false;
        AppWindow.Resize(new SizeInt32((int)(1280 * Root.XamlRoot.RasterizationScale), (int)(760 * Root.XamlRoot.RasterizationScale)));
        var target = Model.VisibleTorrents[0];
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.General);
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading);
        foreach (var theme in new[] { "light", "dark" })
        {
            await Model.Settings.SelectTheme(theme);
            var name = $"library-shared-inspector-{theme}";
            await CapturePage(name, InspectorContent.Content as Microsoft.UI.Xaml.FrameworkElement);
            completed.Add(name);
        }
        Model.Inspector.Select(InspectorSection.Pieces);
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.Pieces is { Count: > 0 });
        await CapturePage("pieces-switch-before");
        completed.Add("pieces-switch-before");
        var map = CaptureElements(InspectorContent).OfType<PiecesMap>().Single();
        var bitmap = (Image)map.FindName("Bitmap");
        var drawing = (FrameworkElement)map.FindName("Drawing");
        var alternate = Model.VisibleTorrents.First(torrent => torrent != target);
        await Switch(false);
        target = alternate;
        alternate = Model.VisibleTorrents.First(torrent => torrent != target);
        await Switch(true);

        async Task Switch(bool rapid)
        {
            var previous = bitmap.Source ?? throw new InvalidOperationException("The initial Pieces map has no bitmap.");
            var expected = rapid ? target : alternate;
            var clock = Stopwatch.StartNew();
            var samples = new List<object>();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stage = "before";
            void Sample()
            {
                var retained = ReferenceEquals(bitmap.Source, previous);
                if (Model.Inspector.Target != target && retained && drawing.IsHitTestVisible)
                    ready.TrySetException(new InvalidOperationException("The retained Pieces map still accepts input for another torrent."));
                if (samples.Count < 240)
                    samples.Add(new { stage, elapsedMs = clock.Elapsed.TotalMilliseconds,
                        torrentId = Model.Inspector.Target?.TorrentId, loading = Model.Inspector.IsLoading,
                        retainedBitmap = retained, hasBitmap = bitmap.Source is not null,
                        opacity = bitmap.Opacity, interactive = drawing.IsHitTestVisible });
                if (stage == "waiting" && Model.Inspector.Target == expected &&
                    !Model.Inspector.IsLoading && Model.Inspector.Pieces is { Count: > 0 } &&
                    bitmap.Source is not null && !retained && drawing.IsHitTestVisible)
                    ready.TrySetResult();
            }
            void Frame(object? sender, object args) => Sample();
            CompositionTarget.Rendering += Frame;
            try
            {
                Sample();
                stage = "alternate";
                if (!await SelectTorrent(new Syno.TableView.Selection([alternate], alternate)))
                    throw new InvalidOperationException("The Pieces switch did not accept the alternate torrent.");
                Sample();
                if (rapid)
                {
                    stage = "return";
                    if (!await SelectTorrent(new Syno.TableView.Selection([target], target)))
                        throw new InvalidOperationException("The rapid Pieces switch did not accept the latest torrent.");
                    Sample();
                }
                stage = "waiting";
                Sample();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var name = rapid ? "pieces-switch-rapid" : "pieces-switch";
                await CapturePage(name);
                Sample();
                if (Model.Inspector.Target != expected || Model.Inspector.IsLoading ||
                    Model.Inspector.Pieces is null || !drawing.IsHitTestVisible)
                    throw new InvalidOperationException("The Pieces map did not settle on the latest torrent.");
                completed.Add(name);
                outcomes.Add(new { journey = name, initialTorrentId = target.TorrentId,
                    alternateTorrentId = alternate.TorrentId, expectedTorrentId = expected.TorrentId,
                    samples, scope = "Real torrent selection and engine detail; rendering callbacks record bitmap retention, opacity and hit testing. No physical input or presentation assertions." });
            }
            finally
            {
                CompositionTarget.Rendering -= Frame;
            }
        }
    }

    private async Task CaptureLibraryTiming(string fixture, List<object> outcomes, List<string> completed)
    {
        var source = Stopwatch.StartNew();
        using var document = JsonDocument.Parse(File.ReadAllText(fixture));
        var data = document.RootElement;
        var files = data.GetProperty("files").GetInt32();
        using var engine = Process.GetProcessById(data.GetProperty("enginePid").GetInt32());
        using var window = Process.GetCurrentProcess();
        long PrivateBytes()
        {
            engine.Refresh();
            window.Refresh();
            return engine.PrivateMemorySize64 + window.PrivateMemorySize64;
        }
        var baseline = PrivateBytes();
        var sourceReadyAtBaseline = Model.Library.IsAvailable;
        await CaptureReady(Model.Library, () => Model.Library.IsAvailable, TimeSpan.FromMinutes(3));
        source.Stop();
        var titles = await Model.Library.PrepareTiming();
        if (titles != 1000)
            throw new InvalidOperationException("The timing fixture does not contain 1,000 saved titles.");
        Model.Library.Configuration = LibraryConfiguration.Files;
        var navigation = Stopwatch.StartNew();
        await Navigate(WindowPage.Library);
        await Model.Library.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(20));
        await CaptureLayout();
        await Rendered().WaitAsync(TimeSpan.FromSeconds(20));
        navigation.Stop();
        if (Model.Library.Rows.Count != files)
            throw new InvalidOperationException("The native timing table does not contain the fixture files.");
        VerifyLibrary();
        outcomes.Add(new { journey = "Library first navigation", files, titles,
            configuration = data.GetProperty("configuration").GetString(), machine = Environment.MachineName,
            processors = Environment.ProcessorCount, operatingSystem = Environment.OSVersion.ToString(),
            sourceReadyMs = source.Elapsed.TotalMilliseconds, visibleMs = navigation.Elapsed.TotalMilliseconds,
            combinedPrivateBytes = PrivateBytes(), baselinePrivateBytes = baseline,
            sourceReadyAtBaseline,
            source = "Cold window database; existing engine pipe; source ingestion measured before navigation; saved video and file facts prepared locally" });
        foreach (var configuration in Enum.GetValues<LibraryConfiguration>())
        {
            Model.Library.Configuration = configuration;
            await Model.Library.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(20));
            await CaptureLayout();
            var samples = new List<object>();
            foreach (var query in new[] { "", "e", "example", "mystery" })
            for (var sample = 0; sample < 20; sample++)
            {
                Search.Text = "timing-no-match";
                await Model.Library.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(20));
                await CaptureLayout();
                VerifyLibrary();
                var allocated = GC.GetTotalAllocatedBytes(false);
                var clock = Stopwatch.StartNew();
                Search.Text = query;
                if (Model.Query != query)
                    throw new InvalidOperationException("The native search input did not reach Library.");
                await Model.Library.SearchCompletion.WaitAsync(TimeSpan.FromSeconds(20));
                var bound = clock.Elapsed.TotalMilliseconds;
                await CaptureLayout();
                var layout = clock.Elapsed.TotalMilliseconds;
                await Rendered().WaitAsync(TimeSpan.FromSeconds(20));
                clock.Stop();
                var allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocated;
                if (Model.Library.HasFailure)
                    throw new InvalidOperationException(Model.Library.Failure);
                VerifyLibrary();
                samples.Add(new { query, sample, rows = Model.Library.Rows.Count,
                    boundMs = bound, layoutMs = layout, visibleMs = clock.Elapsed.TotalMilliseconds,
                    allocatedBytes,
                    combinedPrivateBytes = PrivateBytes() });
            }
            outcomes.Add(new { journey = "Library warm search", files, configuration = configuration.ToString(), samples });
        }
        outcomes.Add(new { journey = "Library timing scope", additionalCombinedPrivateBytes = PrivateBytes() - baseline,
            scope = "Input text through current search, native table layout and next rendering callback; includes frame scheduling",
            memory = "Engine and window private bytes against capture-entry baseline; source initialization may already be in progress; no forced GC",
            excluded = "Cold engine startup, before-feature binary baseline, closed-window baseline, real provider work and transfer throughput",
            targets = new { warmSearchP95Ms = 100, firstNavigationP95Ms = 500, additionalCombinedMiB = 40 } });
        completed.Add("library-native-timing");
    }

    private void VerifyLibrary()
    {
        if (_libraryTable.Table is not { ActualWidth: > 0, ActualHeight: > 0 } table ||
            !ReferenceEquals(table.ItemsSource, Model.Library.Rows))
            throw new InvalidOperationException("The native search table did not receive the current results.");
        var surface = CaptureElements(table).OfType<ListView>().Single();
        if (surface.Items.Count != Model.Library.Rows.Count)
            throw new InvalidOperationException("The native row surface has an obsolete result count.");
        if (surface.Items.Count > 0 &&
            (surface.ContainerFromIndex(0) is not ListViewItem { Content: LibraryRow first } ||
             !ReferenceEquals(first, surface.Items[0]) || !Model.Library.Rows.Contains(first)))
            throw new InvalidOperationException("The native row surface has an obsolete first row.");
    }
}
