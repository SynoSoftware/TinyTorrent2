using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Models;
using Windows.Graphics;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private async Task CaptureLibrary(List<object> outcomes, List<string> completed)
    {
        var store = Model.DataDirectory ?? throw new InvalidOperationException("The library capture has no store.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(store, "library-capture.json")));
        var fixture = document.RootElement;
        var identities = fixture.GetProperty("torrents").EnumerateArray().Select(value => value.GetString()).ToHashSet();
        var target = Model.Torrents.Single(torrent => torrent.TorrentId == fixture.GetProperty("target").GetString());
        var bundle = Model.Torrents.Single(torrent => torrent.TorrentId == fixture.GetProperty("bundle").GetString());
        var root = Path.GetDirectoryName(store) ?? throw new InvalidOperationException("The library capture has no fixture root.");
        if (identities.Count != 300 || !identities.SetEquals(Model.Torrents.Select(torrent => torrent.TorrentId)))
            throw new InvalidOperationException("The library capture membership does not match its disposable fixture.");
        await CaptureReady(Model, () => Model.Torrents.All(torrent => torrent.Size > 0));
        foreach (var torrent in Model.Torrents)
        {
            var relative = Path.GetRelativePath(root, torrent.SavePath);
            if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !torrent.IsPaused || torrent.Size <= 0 || torrent.Peers != 0 || torrent.DownloadRate != 0 || torrent.UploadRate != 0)
                throw new InvalidOperationException("The library capture requires private fixture paths and idle paused payloads.");
        }
        Model.SelectLanguage("en");
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "en");
        await Model.Preferences.SelectTheme("light");
        await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
        var scale = Root.XamlRoot.RasterizationScale;
        var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
        AppWindow.Resize(new SizeInt32(Math.Max((int)(1040 * scale), minimum), (int)(680 * scale)));
        await ShowTorrents();
        Model.CloseInspector();
        Model.Filter = TorrentFilter.All;
        Model.IsFilterOpen = true;
        await CapturePage("library-en-light-1040x680-populated-before");

        async Task SelectFilter(TorrentFilter value)
        {
            var choice = Model.Filters.Single(filter => filter.Filter == value);
            Filters.ScrollIntoView(choice);
            await CaptureLayout();
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Filters);
            var data = peer.GetChildren().OfType<SelectorItemAutomationPeer>().Single(child => ReferenceEquals(child.Item, choice));
            if (data.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider selection)
                throw new InvalidOperationException("The native filter data item does not expose selection.");
            selection.Select();
            await CaptureLayout();
        }

        await SelectFilter(TorrentFilter.Errors);
        if (Model.Filter != TorrentFilter.Errors || Model.VisibleTorrents.Count != 0 || Model.Torrents.Count != 300)
            throw new InvalidOperationException("The native Errors filter did not hide rows while retaining the library.");
        outcomes.Add(new { journey = "native status filter", filter = Model.Filter.ToString(), visible = Model.VisibleTorrents.Count,
            retained = Model.Torrents.Count });

        ThemeButton.Focus(FocusState.Programmatic);
        Search.Text = target.Name;
        Search.Focus(FocusState.Keyboard);
        await CaptureLayout();
        var suggestions = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
            .SelectMany(popup => CaptureElements(popup.Child)).OfType<ListView>().Single(list => list.Name == "SuggestionsList");
        var suggestion = suggestions.Items.OfType<Suggestion>().Single(value => value.Scope == SuggestionScope.Torrent && value.Label == target.Name);
        suggestions.ScrollIntoView(suggestion);
        await CaptureLayout();
        var item = suggestions.ContainerFromItem(suggestion) as ListViewItem ??
            throw new InvalidOperationException("The named torrent result has no realized container.");
        var submitted = false;
        void Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => submitted = true;
        Search.QuerySubmitted += Submitted;
        try
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("The named torrent result does not expose native Invoke.");
            invoke.Invoke();
            await CaptureLayout();
        }
        finally { Search.QuerySubmitted -= Submitted; }
        var tableOrigin = Torrents.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point());
        var revealed = CaptureElements(Torrents).OfType<TextBlock>().Any(text =>
        {
            var origin = text.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point());
            return text.Text == target.Name && text.ActualWidth > 0 && text.ActualHeight > 0 &&
                origin.Y >= tableOrigin.Y && origin.Y + text.ActualHeight <= tableOrigin.Y + Torrents.ActualHeight;
        });
        if (!submitted || Model.Page != WindowPage.Torrents || Model.Filter != TorrentFilter.All || Model.VisibleTorrents.Count != 300 ||
            Torrents.Selection.Current is not Torrent selected || selected.TorrentId != target.TorrentId || !revealed)
            throw new InvalidOperationException("The named torrent result did not reveal the correct selection and clear the filter.");
        outcomes.Add(new { journey = "named torrent from filtered library", submitted, revealed, target = target.TorrentId, name = target.Name,
            ordinal = Model.VisibleTorrents.ToList().IndexOf(target), retained = Model.Torrents.Count, filterCleared = true });
        Search.IsSuggestionListOpen = false;
        Search.Text = string.Empty;
        Model.IsFilterOpen = false;

        Task Matrix(string name, Torrent anchor) => CaptureMatrix(async (suffix, _) =>
        {
            Torrents.ScrollIntoView(anchor);
            var prefix = "library-" + suffix + "-" + name;
            await CapturePage(prefix);
            completed.Add(prefix);
        });

        await Matrix("populated-search-landing", target);
        Model.IsFilterOpen = true;
        await SelectFilter(TorrentFilter.Paused);
        if (Model.Filter != TorrentFilter.Paused || Model.VisibleTorrents.Count != 300)
            throw new InvalidOperationException("The native Paused filter did not restore the populated library.");
        outcomes.Add(new { journey = "native populated filter", filter = Model.Filter.ToString(), visible = Model.VisibleTorrents.Count,
            scope = "All fixtures are paused, so Paused includes every member; Errors was the distinct hiding outcome." });
        Torrents.Selection = new Syno.TableView.Selection([], null);
        await SelectTorrent();
        await Matrix("paused-filter", Model.VisibleTorrents[0]);

        Model.IsFilterOpen = false;
        Model.Filter = TorrentFilter.All;
        Torrents.Selection = new Syno.TableView.Selection([bundle], bundle);
        await SelectTorrent();
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.Files);
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.HasFiles);
        var files = Model.Inspector.Files.Roots.SelectMany(node => node.Nodes()).Where(node => node.Index >= 0 && !node.IsPadding).ToArray();
        if (Model.Inspector.Target?.TorrentId != bundle.TorrentId || files.Length != fixture.GetProperty("files").GetInt32() || files.Any(file => file.Size <= 0))
            throw new InvalidOperationException("The multi-file inspector did not load the fixture's nonzero files.");
        outcomes.Add(new { journey = "multi-file inspector", target = bundle.TorrentId, files = files.Length,
            scope = "Engine-backed library and native filter/search providers; file priorities remain unchanged, using existing priority evidence." });
        await CaptureLayout();
        var table = CaptureElements(InspectorContent).OfType<Syno.TableView.Table>().Single(control => control.Name == "Files");
        var list = CaptureElements(table).OfType<Syno.TableView.Body.Surface>().Single();
        var folder = Model.Inspector.Files.Roots.SelectMany(node => node.Nodes()).First(node =>
            node.IsFolder && node.IsExpanded && node.Children.Count > 0 && node.Children.All(child => !child.IsFolder));
        var descendants = folder.Children.ToArray();
        var child = descendants.First(file => list.Items.Contains(file));
        var outside = files.First(file => !descendants.Contains(file) && list.Items.Contains(file));
        var priorities = Model.Inspector.Files.Priorities();
        var expanded = folder.IsExpanded;
        var parent = (ItemsControlAutomationPeer)FrameworkElementAutomationPeer.CreatePeerForElement(list);
        var branch = (IExpandCollapseProvider)parent.CreateItemAutomationPeer(folder).GetPattern(PatternInterface.ExpandCollapse);
        try
        {
            ((ISelectionItemProvider)parent.CreateItemAutomationPeer(outside).GetPattern(PatternInterface.SelectionItem)).Select();
            ((ISelectionItemProvider)parent.CreateItemAutomationPeer(child).GetPattern(PatternInterface.SelectionItem)).AddToSelection();
            if (table.Selection.Items.Count != 2 || !table.Selection.Items.Contains(child) || !table.Selection.Items.Contains(outside) ||
                !ReferenceEquals(table.Selection.Current, child))
                throw new InvalidOperationException("Native hierarchy selection did not establish the child and outside file.");
            branch.Collapse();
            await CaptureLayout();
            var hidden = descendants.All(file => !list.Items.Contains(file));
            var reconciled = table.Selection.Items.Count == 1 && ReferenceEquals(table.Selection.Items.Single(), outside) &&
                ReferenceEquals(table.Selection.Current, folder);
            outcomes.Add(new { journey = "native folder collapse", descendantsHidden = hidden, selectionReconciled = reconciled });
            if (!hidden || !reconciled)
                throw new InvalidOperationException("Native folder collapse did not reconcile the selected child and retain the outside file.");
            branch.Expand();
            await CaptureLayout();
            var visible = descendants.All(file => list.Items.Contains(file));
            var retained = table.Selection.Items.Count == 1 && ReferenceEquals(table.Selection.Items.Single(), outside);
            var unchanged = priorities.SequenceEqual(Model.Inspector.Files.Priorities());
            outcomes.Add(new { journey = "native folder expand", descendantsVisible = visible, selectionRetained = retained, prioritiesUnchanged = unchanged });
            if (!visible || !retained || !unchanged)
                throw new InvalidOperationException("Native folder expansion did not restore children without changing selection or priorities.");
        }
        finally
        {
            table.Selection = new Syno.TableView.Selection([], null);
            if (folder.IsExpanded != expanded) branch.Expand();
            await CaptureLayout();
        }
        async Task<int[]> ReadPriorities()
        {
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(store, "settings.json")));
            return saved.RootElement.GetProperty("torrents").EnumerateArray()
                .Single(torrent => torrent.GetProperty("torrent_id").GetString() == bundle.TorrentId)
                .GetProperty("priorities").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        }
        async Task ChoosePriority(string key)
        {
            CaptureInvoke(CaptureElements(InspectorContent).OfType<Button>().Single(button => button.Name == "Priority"));
            await CaptureLayout();
            var item = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                .SelectMany(popup => CaptureElements(popup.Child)).OfType<MenuFlyoutItem>()
                .Single(item => item.Text == Model.Text.Get("files", key));
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(item).GetPattern(PatternInterface.Invoke)).Invoke();
            await CaptureLayout();
            await CaptureReady(Model.Inspector, () => !Model.Inspector.IsPending && !Model.Inspector.HasFileDraft && !Model.Inspector.IsLoading);
        }
        var baseline = await ReadPriorities();
        if (!priorities.SequenceEqual(baseline) || priorities.Any(priority => priority != 4))
            throw new InvalidOperationException("The bulk-priority fixture does not have its original Normal priorities.");
        var maps = files.Single(file => file.Index == 1).Parent ?? throw new InvalidOperationException("The Maps fixture folder is missing.");
        try
        {
            ((ISelectionItemProvider)parent.CreateItemAutomationPeer(files.Single(file => file.Index == 0)).GetPattern(PatternInterface.SelectionItem)).Select();
            ((ISelectionItemProvider)parent.CreateItemAutomationPeer(maps).GetPattern(PatternInterface.SelectionItem)).AddToSelection();
            ((ISelectionItemProvider)parent.CreateItemAutomationPeer(files.Single(file => file.Index == 1)).GetPattern(PatternInterface.SelectionItem)).AddToSelection();
            if (table.Selection.Items.Count != 3) throw new InvalidOperationException("The native overlapping priority selection did not select three rows.");
            await ChoosePriority("high");
            var saved = await ReadPriorities();
            var exact = saved.SequenceEqual(new[] { 7, 7, 7, 4, 4, 4, 4, 4 });
            outcomes.Add(new { journey = "native overlapping bulk priority", changedIndexes = new[] { 0, 1, 2 }, persistedPriorities = saved, exactTargets = exact });
            if (!exact) throw new InvalidOperationException("Bulk High priority did not persist exactly the folder, overlapping child and outside file indexes.");
        }
        finally
        {
            await ChoosePriority("normal");
            var saved = await ReadPriorities();
            var restored = priorities.SequenceEqual(saved) && priorities.SequenceEqual(Model.Inspector.Files.Priorities());
            outcomes.Add(new { journey = "native bulk priority restore", originalPrioritiesRestored = restored });
            table.Selection = new Syno.TableView.Selection([], null);
            if (!restored) throw new InvalidOperationException("The native priority menu did not restore the original priorities.");
        }
        await Matrix("multi-file-inspector", bundle);
        Model.Inspector.Select(InspectorSection.Pieces);
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.Pieces is not null);
        await Matrix("pieces", bundle);
        await CapturePieces(outcomes, completed);
        Model.CloseInspector();
    }

    private async Task CaptureMatrix(Func<string, SizeInt32, Task> capture)
    {
        foreach (var language in new[] { "en", "es" })
        {
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
            foreach (var theme in new[] { "light", "dark" })
            {
                await Model.Preferences.SelectTheme(theme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                foreach (var size in new[] { new SizeInt32(720, 560), new SizeInt32(1040, 680), new SizeInt32(1280, 800) })
                {
                    await CaptureLayout();
                    var scale = Root.XamlRoot.RasterizationScale;
                    var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                    AppWindow.Resize(new SizeInt32(Math.Max((int)(size.Width * scale), minimum), (int)(size.Height * scale)));
                    await CaptureLayout();
                    await capture(language + "-" + theme + "-" + size.Width + "x" + size.Height, size);
                }
            }
        }
    }
}
