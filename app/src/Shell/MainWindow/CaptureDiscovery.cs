using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Settings;
using Syno.TinyTorrent.Shared;
using Windows.Graphics;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    // Disk discovery's surfaces with seeded states. The journey never runs
    // gozler: the disk source keeps its option off in memory, so no refresh
    // starts, and the states it shows are set on it directly. The install
    // prompt is closed without its primary button, so nothing downloads.
    private async Task CaptureDiscovery(List<object> outcomes, List<string> completed)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Model.Features.CanStart = true;
        Model.RequestSnapshot();
        await CaptureReady(Model, () => Model.Subtitles is not null && Model.Disk is not null);
        // A new Library file must not start a subtitle search on a website.
        await Model.Subtitles!.SetEnabled(false);
        await CaptureReady(Model.Library, () => Model.Library.IsAvailable && Model.Library.CanDiscover);
        var disk = Model.Disk!;
        var setup = Model.GozlerSetup;
        var stages = new List<string>();
        setup.PropertyChanged += (_, _) => stages.Add(setup.Stage.GetType().Name);
        var gozler = Gozler.Locate();
        var combinations = (
            from language in new[] { "en", "es" }
            from theme in new[] { "light", "dark" }
            select (language, theme)
        ).ToArray();

        foreach (var (language, theme) in combinations)
        {
            var suffix = await Prepare(language, theme);

            await Model.Navigation.Navigate(WindowPage.Library);
            var showing = System.Diagnostics.Stopwatch.StartNew();
            while (
                !CaptureElements(Root)
                    .Any(element => AutomationProperties.GetAutomationId(element) == "LibraryDiscoverMedia")
            )
            {
                if (showing.Elapsed > TimeSpan.FromSeconds(20) || Model.Library.Rows.Count > 0)
                {
                    throw new InvalidOperationException("Library's empty state did not offer Discover.");
                }
                await Task.Delay(20);
                await CaptureLayout();
            }
            await Keep("discovery-empty-" + suffix);

            var page = await ShowMedia();
            await Keep("discovery-settings-off-" + suffix, page);

            var toasts = Model.Notifications.Toasts.Count;
            var installing = InstallGozler();
            await CaptureLayout();
            var dialog =
                _interaction?.Dialog ?? throw new InvalidOperationException("The install prompt did not open.");
            try
            {
                if (setup.Stage is not InstallStage.Failed { Reason: InstallFailure.Unavailable })
                {
                    throw new InvalidOperationException(
                        $"The install prompt opened in {setup.Stage}; the capture build must have no gozler.json."
                    );
                }
                await Keep("discovery-install-unavailable-" + suffix, dialog.Content as FrameworkElement);
                // The first state, as a release build with its pinned record
                // shows it. The address cannot resolve, so even a press of
                // Install could download nothing.
                var pinned = typeof(GozlerSetup).GetNestedType("Pinned", BindingFlags.NonPublic)!;
                typeof(GozlerSetup)
                    .GetField("_pinned", flags)!
                    .SetValue(
                        setup,
                        Activator.CreateInstance(
                            pinned,
                            new Uri("https://capture.invalid/gozler-x64-Setup.exe"),
                            new string('0', 64),
                            4_718_592L
                        )
                    );
                typeof(GozlerSetup)
                    .GetProperty(nameof(GozlerSetup.Stage), flags)!
                    .SetValue(setup, new InstallStage.Ready());
                _interaction!.RefreshText!();
                if (!dialog.IsPrimaryButtonEnabled)
                {
                    throw new InvalidOperationException("The install prompt did not offer Install.");
                }
                await Keep("discovery-install-" + suffix, dialog.Content as FrameworkElement);
            }
            finally
            {
                dialog.Hide();
                await installing;
            }
            await CaptureLayout();
            if (
                _interaction is not null
                || setup.Stage is not InstallStage.Ready
                || Model.Disk?.State.Status != DiskStatus.Off
                || Model.Notifications.Toasts.Count != toasts
            )
            {
                throw new InvalidOperationException("Cancel left the prompt, a stage, the option or a message behind.");
            }
            await Keep("discovery-install-cancelled-" + suffix, page);
        }

        // The option and the files are saved as a refresh saves them, but
        // the source's own option stays off, so it never asks gozler.
        var sources = (FileSources)typeof(DiskSource).GetField("_sources", flags)!.GetValue(disk)!;
        await sources.EnableDiscovery(CancellationToken.None);
        string[] hidden = [@"D:\Games\Old installers", @"E:\Backups"];
        foreach (var folder in hidden)
        {
            await sources.Hide(folder, CancellationToken.None);
        }
        var file = new DiskFile(
            @"D:\Media\Films\Night Train (1998)\Night.Train.1998.1080p.mkv",
            @"\\?\Volume{6f1c2a3e-0000-4000-8000-100000000000}\",
            "0x00000000000000000005000000001f2a"
        )
        {
            Size = 2_147_483_648,
            Modified = new DateTime(2024, 3, 9, 20, 15, 0, DateTimeKind.Utc).ToFileTimeUtc(),
        };
        if (
            !await sources.AcceptDisk(
                new DiskChanges { Added = [file], Confirms = true },
                CancellationToken.None
            )
        )
        {
            throw new InvalidOperationException("The disk file was not saved.");
        }
        await ShowState(new(DiskStatus.Current));
        ((EventHandler?)typeof(DiskSource).GetField("Published", flags)!.GetValue(disk))?.Invoke(
            disk,
            EventArgs.Empty
        );
        // Library searches only while it is shown.
        await Model.Navigation.Navigate(WindowPage.Library);
        await CaptureReady(Model.Library, () => Model.Library.Rows.Count == 1);

        foreach (var (language, theme) in combinations)
        {
            var suffix = await Prepare(language, theme);

            var page = await ShowMedia();
            await CaptureReady(Model.Settings, () => Model.Settings.HiddenFolders.Count == hidden.Length);
            await CaptureLayout();
            if (!Model.Settings.DiscoversMedia)
            {
                throw new InvalidOperationException("Settings did not show the option on.");
            }
            await Keep("discovery-settings-on-" + suffix, page);

            await Model.Navigation.Navigate(WindowPage.Library);
            Model.Library.Select(Model.Library.Rows[0]);
            await CaptureReady(
                Model.Library,
                () => Model.Library.Selected is { } selected && Model.Library.HasOrigins(selected.Entry.EntryId)
            );
            var hide = await LibraryMenuReady();
            var library = Expand(FrameworkElementAutomationPeer.CreatePeerForElement(TorrentMenu));
            try
            {
                await CaptureLayout();
                var submenu = Expand(FrameworkElementAutomationPeer.CreatePeerForElement(hide));
                try
                {
                    await Keep("discovery-library-menu-" + suffix);
                }
                finally
                {
                    submenu.Collapse();
                }
            }
            finally
            {
                library.Collapse();
            }
            await CaptureLayout();

            await ShowState(new(DiskStatus.Stopped));
            await Keep("discovery-bar-stopped-" + suffix);
            await ShowState(new(DiskStatus.Indexing) { Drives = [@"D:\"] });
            await Keep("discovery-bar-indexing-" + suffix);
            await ShowState(new(DiskStatus.Current));
        }

        outcomes.Add(
            new
            {
                journey = "disk discovery",
                gozler = gozler.ToString(),
                hidden,
                file = file.Path,
                installStages = stages.Distinct().ToArray(),
                downloaded = stages.Any(stage => stage is nameof(InstallStage.Downloading) or nameof(InstallStage.Running)),
            }
        );
        if (stages.Any(stage => stage is nameof(InstallStage.Downloading) or nameof(InstallStage.Running)))
        {
            throw new InvalidOperationException("The install prompt started a download or gozler's setup.");
        }

        async Task<string> Prepare(string language, string theme)
        {
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.Text.Language == language);
            await Model.Settings.Theme.Select(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            var scale = Root.XamlRoot.RasterizationScale;
            var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
            AppWindow.Resize(new SizeInt32(Math.Max((int)(1040 * scale), minimum), (int)(720 * scale)));
            await CaptureLayout();
            return language + "-" + theme;
        }

        async Task Keep(string name, FrameworkElement? content = null)
        {
            await CapturePage(name, content);
            completed.Add(name);
        }

        async Task<SettingsPage> ShowMedia()
        {
            await ShowSettings(new(SettingsCategory.Plugins));
            var page = _settingsPage ?? throw new InvalidOperationException("Settings did not open.");
            var section = (SettingsSection)page.FindName("MediaSection");
            section.IsExpanded = true;
            await CaptureLayout();
            section.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            await CaptureLayout();
            return page;
        }

        // Sets the state the source reports and raises its change, as a
        // finished refresh does.
        async Task ShowState(DiskState state)
        {
            typeof(DiskSource).GetProperty(nameof(DiskSource.State), flags)!.SetValue(disk, state);
            ((EventHandler?)typeof(DiskSource).GetField("Changed", flags)!.GetValue(disk))?.Invoke(
                disk,
                EventArgs.Empty
            );
            var body = state.Describe(Model.Text);
            await CaptureReady(
                Model,
                () =>
                    Model.Disk?.State == state
                    && (
                        state.Status == DiskStatus.Current
                        || Model.Page != WindowPage.Library
                        || Model.CurrentStatus?.Body == body
                    )
            );
            await CaptureLayout();
        }

        // The Library menu fills after the row's origins are read; Hide folder
        // is enabled only then. Its text also proves the fill follows the
        // current language.
        async Task<MenuFlyoutSubItem> LibraryMenuReady()
        {
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                await CaptureLayout();
                var refresh = TorrentMenu.Items.OfType<MenuFlyoutItem>()
                    .Any(item => item.Command == Model.RefreshDiscovery);
                var text = Model.Text.Get("discovery", "hide");
                if (
                    refresh
                    && TorrentMenu.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault(item => item.Text == text)
                        is { IsEnabled: true, Items.Count: 4 } hide
                )
                {
                    return hide;
                }
                if (waiting.Elapsed > TimeSpan.FromSeconds(20))
                {
                    throw new InvalidOperationException(
                        "The Library menu did not offer Refresh files and Hide folder for the disk file."
                    );
                }
                await Task.Delay(20);
            }
        }

        static IExpandCollapseProvider Expand(AutomationPeer peer)
        {
            var expand =
                peer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
                ?? throw new InvalidOperationException("The menu cannot be expanded.");
            expand.Expand();
            return expand;
        }
    }
}
