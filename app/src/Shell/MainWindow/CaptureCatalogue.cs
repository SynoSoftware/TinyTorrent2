using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TableView;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Search;
using Syno.TinyTorrent.Settings;
using Windows.Foundation;
using Windows.Graphics;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureCatalogue(List<object> outcomes, List<string> completed)
    {
        await Model.Navigation.Navigate(WindowPage.Search);
        WorkspacePicker.Flyout.ShowAt(WorkspacePicker);
        await CapturePage("workspace-menu");
        var menu = (MenuFlyout)WorkspacePicker.Flyout;
        var library = menu.Items.OfType<RadioMenuFlyoutItem>().Single(item => item.Command == Model.ShowLibrary);
        ((IToggleProvider)FrameworkElementAutomationPeer.CreatePeerForElement(library)
            .GetPattern(PatternInterface.Toggle)).Toggle();
        menu.Hide();
        await CaptureReady(Model.Navigation, () => Model.Navigation.Current == WindowPage.Library);
        await CaptureLayout();
        WorkspacePicker.Flyout.ShowAt(WorkspacePicker);
        await CaptureLayout();
        var search = menu.Items.OfType<RadioMenuFlyoutItem>().Single(item => item.Command == Model.ShowSearch);
        ((IToggleProvider)FrameworkElementAutomationPeer.CreatePeerForElement(search)
            .GetPattern(PatternInterface.Toggle)).Toggle();
        menu.Hide();
        await CaptureReady(Model.Navigation, () => Model.Navigation.Current == WindowPage.Search);
        Model.IsFilterOpen = false;
        foreach (var scene in new[] { ("en", "dark", 1040, 680), ("es", "light", 720, 560) })
        {
            await SetScene(scene);
            await CaptureReady(Model.Search, () => !Model.Search.IsBusy && !Model.Search.HasDatabase);
            await CapturePage($"catalogue-{scene.Item1}-{scene.Item2}-first-use");
        }
        await CaptureIndex();
        Model.Search.Query = "Chapter";
        await CaptureReady(Model.Search, () => !Model.Search.IsBusy && Model.Search.Rows.Count == 3);

        Model.Search.Torrent = "field atlas";
        await CaptureReady(Model.Search, () => !Model.Search.IsBusy && Model.Search.Rows.Count == 1);
        if (Model.Search.Rows[0].Source.Name != "Open field atlas")
            throw new InvalidOperationException("The parent torrent filter selected another source.");
        Model.Search.Torrent = string.Empty;
        await CaptureReady(Model.Search, () => !Model.Search.IsBusy && Model.Search.Rows.Count == 3);
        var row = Model.Search.Rows[0];
        Model.Search.Select(new Selection([row], row));
        Model.OpenInspector();
        Model.IsFilterOpen = true;

        foreach (var scene in new[] { ("en", "dark", 1040, 680), ("es", "light", 720, 560) })
        {
            await SetScene(scene);
            row = Model.Search.Rows[0];
            Model.Search.Select(new Selection([row], row));
            var prefix = $"catalogue-{scene.Item1}-{scene.Item2}";
            await CapturePage(prefix + "-files");
            var results = CaptureElements(_catalogueView!).OfType<Table>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "CatalogueResults");
            row.IsExpanded = true;
            results.RefreshView();
            await CapturePage(prefix + "-expanded");
            var sections = CaptureElements(_catalogueInspector!)
                .OfType<SelectorBar>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "CatalogueInspectorSections");
            sections.SelectedItem = sections.Items[1];
            await CapturePage(prefix + "-aliases");
            sections.SelectedItem = sections.Items[0];
            await Model.Search.Inspect(row.Source);
            await CaptureReady(Model.Search, () => Model.Search.FileChoices.Roots.Count > 0);
            await CapturePage(prefix + "-torrent");
            sections.SelectedItem = sections.Items[2];
            Model.Search.FileChoices.SelectMatching(true);
            if (!Model.Search.DownloadFiles.CanExecute(null))
                throw new InvalidOperationException("Selecting the parent torrent's files did not enable Download.");
            await CapturePage(prefix + "-selection");
            sections.SelectedItem = sections.Items[0];
            await ShowIndexing();
            var settings = _settingsPage ?? throw new InvalidOperationException("The indexing Settings tab did not open.");
            await settings.RefreshIndexing();
            await CapturePage(prefix + "-settings", settings);
            var task = new IndexingTask(Model, IndexingPage.Edit);
            var closed = ShowIndexingTask(task);
            await CaptureLayout();
            await CaptureReady(task, () => !task.IsPending);
            try
            {
                await CapturePage(prefix + "-rule-editor", task);
                var title = CaptureElements(task).OfType<TextBox>()
                    .Single(control => AutomationProperties.GetAutomationId(control) == "RuleTitle");
                title.Text = "*.tmp";
                await CaptureReady(task, () => task.CanSubmit);
                await task.Submit();
                await CapturePage(prefix + "-rule-preview", task);
            }
            finally
            {
                task.Dialog.Hide();
                await closed;
            }
            foreach (var clear in new[] { false, true })
            {
                var preview = new IndexingTask(Model, IndexingPage.Removal, clear: clear);
                var previewClosed = ShowIndexingTask(preview);
                await CaptureLayout();
                await CaptureReady(preview, () => !preview.IsPending);
                try { await CapturePage(prefix + (clear ? "-removal-populated" : "-removal-empty"), preview); }
                finally
                {
                    preview.Dialog.Hide();
                    await previewClosed;
                }
            }
            await Model.Navigation.Navigate(WindowPage.Search);
            Model.Search.Select(new Selection([row], row));
            completed.Add(prefix);
        }

        Model.Search.Query = "absent-catalogue-file";
        await CaptureReady(Model.Search, () => !Model.Search.IsBusy && Model.Search.Rows.Count == 0);
        await CapturePage("catalogue-no-matches");
        Model.Search.ClearSearch();
        Model.Search.Target = CatalogueTarget.Torrents;
        Model.Search.Query = string.Empty;
        await CaptureReady(Model.Search, () => !Model.Search.IsBusy && Model.Search.Rows.Count == 2);
        await CapturePage("catalogue-torrents");
        outcomes.Add(new
        {
            journey = "catalogue while collection is off",
            torrents = Model.Search.State.Torrents,
            matchedFiles = 3,
            parentFilteredFiles = 1,
            selectedFilesDownloadAvailable = true,
            scope = "Private paused fixture, no peer discovery or payload transfer; native Search, Inspector and settings surfaces.",
        });

        async Task SetScene((string Language, string Theme, int Width, int Height) scene)
        {
            Model.SelectLanguage(scene.Language);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == scene.Language);
            await Model.Settings.SelectTheme(scene.Theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == scene.Theme);
            var scale = Root.XamlRoot.RasterizationScale;
            var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
            AppWindow.Resize(new SizeInt32(Math.Max((int)(scene.Width * scale), minimum), (int)(scene.Height * scale)));
        }
    }

    // Indexes the two fixture torrents, then turns collection off again so no
    // peer discovery runs.
    private async Task CaptureIndex()
    {
        await Model.Settings.Indexing.Toggle(true);
        await Model.Search.RefreshStatus();
        await CaptureReady(Model.Search, () => Model.Search.State.Torrents == 2 && !Model.Search.State.Projecting);
        await Model.Settings.Indexing.Toggle(false);
        await Model.Search.RefreshStatus();
    }

    // Arranges the inspector on one page and checks that the next page shows
    // the same arrangement and toolbar, that each pane button acts once, and
    // that Close returns focus to the page. The window is narrow and Filters
    // is open, so the toolbar has little room for each page's commands.
    private async Task CaptureWorkspace(List<object> outcomes, List<string> completed)
    {
        Require(await Model.Navigation.Navigate(WindowPage.Search), "Search did not open.");
        await CaptureIndex();
        Model.Search.Query = "Chapter";
        await CaptureReady(Model.Search, () => !Model.Search.IsBusy && Model.Search.Rows.Count == 3);
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new SizeInt32((int)(720 * scale), (int)(560 * scale)));
        Model.IsFilterOpen = true;
        Model.IsToolbarOpen = true;
        Model.IsInspectorMaximized = false;
        Model.Search.Select(new Selection([]));
        Model.SetInspector(open: true, compact: false);
        await CaptureLayout();
        var search = _catalogueInspector ?? throw new InvalidOperationException("Search did not create its inspector.");
        Require(InspectorSurface.Visibility == Visibility.Visible
            && ReferenceEquals(InspectorFrame, search.Surface)
            && ((TextBlock)search.FindName("NoSelection")).Visibility == Visibility.Visible,
            "Search without a selection did not show its empty message in the open inspector.");
        var overflow = new Dictionary<string, int> { ["search"] = CheckToolbar() };
        await CapturePage("workspace-search-empty");

        await Press(search.Surface, "CatalogueInspectorMaximize");
        Require(Model.IsInspectorMaximized && search.Surface.IsMaximized
            && WorkspaceContent.Visibility == Visibility.Collapsed && ToolbarRow.Visibility == Visibility.Visible,
            "One Maximize press on Search did not maximize below the toolbar.");
        await Press(search.Surface, "CatalogueInspectorMinimize");
        Require(Model.IsInspectorCompact && Model.IsInspectorMaximized,
            "Pin on Search lost the maximized choice.");

        Require(await Model.Navigation.Navigate(WindowPage.Library), "Library did not open.");
        await CaptureLayout();
        var library = (InspectorPane)InspectorContent.Content;
        Require(ReferenceEquals(InspectorFrame, library.Surface) && library.Surface.IsCompact
            && InspectorRow.Height.Value == 10,
            "Library did not show the inspector pinned on Search.");
        await Press(library.Surface, "InspectorToggle");
        Require(!Model.IsInspectorCompact && library.Surface.IsMaximized
            && WorkspaceContent.Visibility == Visibility.Collapsed,
            "Expanding on Library did not restore the maximized inspector.");
        await Press(library.Surface, "InspectorMaximize");
        Require(!Model.IsInspectorMaximized && Split.Visibility == Visibility.Visible,
            "One Restore press on Library did not restore the inspector.");
        Split.SetValue(Split.Value - 40);
        await CaptureLayout();
        var height = InspectorRow.Height.Value;
        Require(height > 10 && Math.Abs(height - Split.Value) < 1, "The Library split did not resize the inspector.");
        overflow["library"] = CheckToolbar();
        await CapturePage("workspace-library");

        Require(await Model.Navigation.Navigate(WindowPage.Torrents), "Torrents did not open.");
        await CaptureLayout();
        Require(Math.Abs(InspectorRow.Height.Value - height) < 1 && Split.Visibility == Visibility.Visible
            && !((InspectorPane)InspectorContent.Content).Surface.IsMaximized,
            "Torrents did not keep the inspector height and presentation set on Library.");
        overflow["torrents"] = CheckToolbar();
        await CapturePage("workspace-torrents");

        Require(await Model.Navigation.Navigate(WindowPage.Search), "Search did not open again.");
        await CaptureLayout();
        Require(ReferenceEquals(InspectorFrame, search.Surface) && !search.Surface.IsCompact
            && !search.Surface.IsMaximized && Math.Abs(InspectorRow.Height.Value - height) < 1,
            "Search did not show the inspector as it was left on Torrents.");
        await Press(search.Surface, "CatalogueInspectorClose");
        var focus = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (focus is not null && !ReferenceEquals(focus, _catalogueView))
            focus = VisualTreeHelper.GetParent(focus);
        Require(!Model.HasInspector && InspectorSurface.Visibility == Visibility.Collapsed && focus is not null,
            "Close on Search did not hide the inspector and return focus to the results.");
        Model.OpenInspector();
        await CaptureLayout();
        Require(ReferenceEquals(_catalogueInspector, search) && Math.Abs(InspectorRow.Height.Value - height) < 1,
            "Reopening replaced the Search inspector or lost its height.");
        await Press(search.Surface, "CatalogueInspectorMaximize");
        Require(Model.IsInspectorMaximized, "One Maximize press after reopening did not maximize once.");
        await Press(search.Surface, "CatalogueInspectorMaximize");

        completed.Add("workspace");
        outcomes.Add(new
        {
            journey = "workspace continuity across Search, Library and Torrents",
            inspectorHeight = height,
            overflowedCommands = overflow,
        });

        async Task Press(Pane pane, string automationId)
        {
            CaptureInvoke(CaptureElements(pane).OfType<Button>()
                .Single(button => AutomationProperties.GetAutomationId(button) == automationId));
            await CaptureLayout();
        }

        // Every visible command sits inside the toolbar's column or in its
        // More menu. Returns how many moved to the More menu.
        int CheckToolbar()
        {
            var width = ToolbarRow.ColumnDefinitions[0].ActualWidth;
            var commands = Toolbar.PrimaryCommands.OfType<AppBarButton>()
                .Where(button => button.Visibility == Visibility.Visible)
                .ToArray();
            foreach (var button in commands.Where(button => !button.IsInOverflow))
                Require(button.TransformToVisual(ToolbarRow).TransformPoint(new Point(button.ActualWidth, 0)).X <= width + 0.5,
                    $"The toolbar clipped {button.Label} on {Model.Page}.");
            return commands.Count(button => button.IsInOverflow);
        }

        static void Require(bool condition, string failure)
        {
            if (!condition)
                throw new InvalidOperationException(failure);
        }
    }
}
