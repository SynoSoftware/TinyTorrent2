using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private string? _captureDirectory;
    private Task? _capture;
    private static readonly CaptureMode ReviewMode = Environment.GetEnvironmentVariable(
        "TINYTORRENT_CAPTURE_REVIEW"
    ) switch
    {
        "1" => CaptureMode.Full,
        "smoke" => CaptureMode.Smoke,
        "shell" => CaptureMode.Shell,
        "schedule" => CaptureMode.Schedule,
        "desktop" => CaptureMode.Desktop,
        "details" => CaptureMode.Details,
        "details-files" => CaptureMode.DetailsFiles,
        "files" => CaptureMode.Files,
        "files-layout" => CaptureMode.FilesLayout,
        "search" => CaptureMode.Search,
        "library" => CaptureMode.Library,
        "traffic" => CaptureMode.Traffic,
        "edits" => CaptureMode.Edits,
        "add-layout" => CaptureMode.AddLayout,
        "settings-layout" => CaptureMode.SettingsLayout,
        "footer" => CaptureMode.Footer,
        _ => CaptureMode.None,
    };

    static MainWindow() => IsCaptureReview = ReviewMode != CaptureMode.None;

    internal void ShowCaptureReview()
    {
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Move(new PointInt32(-10000, -10000));
        AppWindow.Show(false);
    }

    partial void ConfigureCapture()
    {
        var directory = Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        if (!Path.IsPathFullyQualified(directory))
        {
            Debug.WriteLine("TINYTORRENT_CAPTURE_DIRECTORY requires an absolute path.");
            return;
        }
        _captureDirectory = directory;
        if (IsCaptureReview)
        {
            Root.Loaded += (_, _) =>
            {
                if (_capture is null)
                    _capture = CaptureReview();
            };
            return;
        }
        var shortcut = new KeyboardAccelerator
        {
            Key = VirtualKey.F12,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
        };
        shortcut.Invoked += (_, args) =>
        {
            if (_capture is not { IsCompleted: false })
                _capture = CaptureUi();
            args.Handled = true;
        };
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Root.KeyboardAccelerators.Add(shortcut);
    }

    private async Task CaptureUi(string? name = null)
    {
        if (_captureDirectory is null || Root.XamlRoot is null)
            return;
        var clock = Stopwatch.StartNew();
        try
        {
            var stamp = DateTime.UtcNow.ToString(
                "yyyyMMdd-HHmmss-fffffff",
                CultureInfo.InvariantCulture
            );
            var directory = Path.Combine(
                _captureDirectory,
                name is null ? stamp : name + "-" + stamp
            );
            Directory.CreateDirectory(directory);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var scale = Root.XamlRoot.RasterizationScale;
            var scene = new List<FrameworkElement> { Root };
            scene.AddRange(
                VisualTreeHelper
                    .GetOpenPopupsForXamlRoot(Root.XamlRoot)
                    .Select(popup => popup.Child)
                    .OfType<FrameworkElement>()
            );
            var frames = new List<object>();
            var controls = new List<object>();
            foreach (var element in scene)
            {
                if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                    continue;
                var bitmap = new RenderTargetBitmap();
                byte[]? previous = null;
                byte[] pixels;
                var stable = false;
                var rendering = Stopwatch.StartNew();
                do
                {
                    await bitmap.RenderAsync(
                        element,
                        (int)Math.Ceiling(element.ActualWidth * scale),
                        (int)Math.Ceiling(element.ActualHeight * scale)
                    );
                    pixels = (await bitmap.GetPixelsAsync()).ToArray();
                    stable = previous is not null && pixels.AsSpan().SequenceEqual(previous);
                    previous = pixels;
                } while (!stable && rendering.ElapsedMilliseconds < 3000);
                var frame = frames.Count == 0 ? "window.png" : $"popup-{frames.Count}.png";
                var file = await folder.CreateFileAsync(
                    frame,
                    CreationCollisionOption.FailIfExists
                );
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth,
                    (uint)bitmap.PixelHeight,
                    96 * scale,
                    96 * scale,
                    pixels
                );
                await encoder.FlushAsync();
                frames.Add(
                    new
                    {
                        file = frame,
                        width = bitmap.PixelWidth,
                        height = bitmap.PixelHeight,
                        stable,
                    }
                );
                foreach (
                    var control in CaptureElements(element)
                        .Where(control =>
                            control.IsLoaded && control.ActualWidth > 0 && control.ActualHeight > 0
                        )
                )
                {
                    var point = control
                        .TransformToVisual(element)
                        .TransformPoint(new Windows.Foundation.Point());
                    controls.Add(
                        new
                        {
                            frame,
                            type = control.GetType().Name,
                            name = control.Name,
                            accessibleName = AutomationProperties.GetName(control),
                            text = control is TextBlock label ? label.Text
                            : control is TextBox input ? input.Text
                            : null,
                            x = point.X,
                            y = point.Y,
                            width = control.ActualWidth,
                            height = control.ActualHeight,
                            enabled = control is not Control command || command.IsEnabled,
                        }
                    );
                }
            }
            await File.WriteAllTextAsync(
                Path.Combine(directory, "capture.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        scope = "XAML visuals; native chrome, system dialogs and desktop acrylic are not captured",
                        page = Model.Page.ToString(),
                        language = Model.Text.Language,
                        theme = Root.ActualTheme.ToString(),
                        scale,
                        milliseconds = clock.ElapsedMilliseconds,
                        frames,
                        controls,
                    }
                )
            );
            Debug.WriteLine($"UI capture saved to {directory} in {clock.ElapsedMilliseconds} ms.");
        }
        catch (Exception error)
        {
            Debug.WriteLine($"UI capture did not complete: {error.Message}");
            if (IsCaptureReview)
                throw;
        }
    }

    private static IEnumerable<FrameworkElement> CaptureElements(
        DependencyObject parent,
        bool includeCollapsed = false
    )
    {
        if (!includeCollapsed && parent is UIElement { Visibility: Visibility.Collapsed })
            yield break;
        if (parent is FrameworkElement element)
            yield return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (
                var child in CaptureElements(
                    VisualTreeHelper.GetChild(parent, index),
                    includeCollapsed
                )
            )
                yield return child;
    }

    private async Task CaptureLayout()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (
            !DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    Root.UpdateLayout();
                    ready.TrySetResult();
                }
            )
        )
            throw new InvalidOperationException("The capture dispatcher is closed.");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task CaptureReady(INotifyPropertyChanged owner, Func<bool> ready)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (ready())
                completion.TrySetResult();
        }
        owner.PropertyChanged += Changed;
        try
        {
            if (ready())
                completion.TrySetResult();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            owner.PropertyChanged -= Changed;
        }
    }

    private async Task CapturePage(string name, FrameworkElement? content = null)
    {
        await CaptureLayout();
        var scroll = content is null
            ? null
            : CaptureElements(content)
                .OfType<ScrollViewer>()
                .Where(view => view.ScrollableHeight > 0)
                .MaxBy(view => view.ScrollableHeight);
        scroll?.ChangeView(null, 0, null, true);
        await CaptureLayout();
        await CaptureUi(name);
        if (scroll is not { ScrollableHeight: > 0 })
            return;
        scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
        await CaptureLayout();
        await CaptureUi(name + "-bottom");
    }

    private async Task CaptureDialog(string name, Func<Task> open, Func<ContentDialog?> current)
    {
        var closed = open();
        await CaptureLayout();
        var dialog =
            current() ?? throw new InvalidOperationException("The review dialog did not open.");
        try
        {
            if (_fileDialog is not null)
                await CaptureReady(Model.FileDraft, () => !Model.FileDraft.IsPending);
            await CapturePage(name, dialog.Content as FrameworkElement);
        }
        finally
        {
            dialog.Hide();
            await closed;
        }
    }

    private static void CaptureInvoke(ContentDialog dialog)
    {
        var button = CaptureElements(dialog)
            .OfType<Button>()
            .First(control => control.Name == "PrimaryButton");
        CaptureInvoke(button);
    }

    private static void CaptureInvoke(Button button)
    {
        button.Focus(FocusState.Programmatic);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("The review button cannot be invoked.");
        invoke.Invoke();
    }

    private async Task CaptureSmoke(Torrent target, List<object> outcomes)
    {
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.Pieces);
        await CaptureLayout();
        await CaptureReady(
            Model.Inspector,
            () => !Model.Inspector.IsLoading && Model.Inspector.Pieces is not null
        );
        await CapturePage("pieces", InspectorContent.Content as FrameworkElement);
        Model.CloseInspector();
        Run(Model.Limits);
        await CaptureLayout();
        await CapturePage("speed-limits-settings", _settingsPage);
        outcomes.Add(
            new
            {
                journey = "speed limits settings",
                page = Model.Page.ToString(),
                scope = "Canonical Transfers settings; the obsolete modal Apply/invalid-input scenario is removed.",
            }
        );
        await ShowTorrents();

        Model.AddDraft.EditingMagnet = true;
        var closed = ShowAdd();
        await CaptureLayout();
        var dialog =
            _interaction?.Dialog
            ?? throw new InvalidOperationException("The Add dialog did not open.");
        try
        {
            Model.AddDraft.Magnet = "invalid magnet";
            await Model.AddDraft.PrepareMagnet();
            if (!Model.AddDraft.HasMagnetError || Model.AddDraft.Magnet != "invalid magnet")
                throw new InvalidOperationException("The magnet editor lost the rejected input.");
            outcomes.Add(new { journey = "invalid magnet", rejectedInputRetained = true });
            await CapturePage("invalid-magnet", dialog.Content as FrameworkElement);
        }
        finally
        {
            dialog.Hide();
            await closed;
        }

        await Model.Select([target], target);
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.Trackers);
        await CaptureLayout();
        await CaptureReady(
            Model.Inspector,
            () => !Model.Inspector.IsLoading && Model.Inspector.EditTrackers.CanExecute(null)
        );
        Run(Model.Inspector.EditTrackers);
        Model.Inspector.TrackerInput = "https://example.invalid/announce";
        closed = ConfirmRemove([target]);
        await CaptureLayout();
        dialog =
            _interaction?.Dialog
            ?? throw new InvalidOperationException("The removal dialog did not open.");
        CaptureInvoke(dialog);
        await closed;
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsAvailable);
        if (
            !Model.Inspector.HasDraft
            || !Model.Inspector.HasError
            || Model.Inspector.CanSaveTrackers
            || !Model.Inspector.CancelTrackers.CanExecute(null)
        )
            throw new InvalidOperationException(
                "The unavailable properties draft has no safe recovery."
            );
        outcomes.Add(
            new
            {
                journey = "removed torrent with draft",
                draftKept = true,
                saveDisabled = true,
                cancelEnabled = true,
            }
        );
        await CapturePage("removed-properties", InspectorContent.Content as FrameworkElement);
        Run(Model.Inspector.CancelTrackers);
        Model.CloseInspector();
        await CapturePage("empty-torrents");
    }

    private async Task CaptureShell(Torrent target, List<object> outcomes)
    {
        foreach (var theme in new[] { "light", "dark" })
        {
            await Model.Settings.SelectTheme(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            foreach (var width in new[] { 1040, 720 })
            {
                var scale = Root.XamlRoot.RasterizationScale;
                var minimum =
                    (
                        (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter
                    ).PreferredMinimumWidth
                    ?? 0;
                AppWindow.Resize(
                    new SizeInt32(Math.Max((int)(width * scale), minimum), (int)(680 * scale))
                );
                await CaptureLayout();
                CaptureHitRegions(outcomes);
                await CapturePage(theme + "-" + width + "-shell");
                foreach (var menu in new[] { FileMenu, TorrentMenu, ViewMenu, HelpMenu })
                {
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(menu);
                    if (
                        peer.GetPattern(PatternInterface.ExpandCollapse)
                        is not IExpandCollapseProvider expand
                    )
                        throw new InvalidOperationException(
                            "The menu does not expose native expansion."
                        );
                    expand.Expand();
                    await CapturePage(theme + "-" + width + "-" + menu.Name);
                    expand.Collapse();
                }
            }
        }
        Torrents.Selection = new Syno.TableView.Selection([], null);
        await SelectTorrent();
        await CaptureLayout();
        outcomes.Add(
            new
            {
                command = "pause-empty",
                enabled = TorrentMenu
                    .Items.OfType<MenuFlyoutItem>()
                    .Single(item => item.Command == Model.Pause)
                    .IsEnabled,
            }
        );
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        await CaptureLayout();
        foreach (var command in new[] { Model.Resume, Model.Pause })
        {
            var menuPeer = FrameworkElementAutomationPeer.CreatePeerForElement(TorrentMenu);
            var expand = (IExpandCollapseProvider)
                menuPeer.GetPattern(PatternInterface.ExpandCollapse);
            expand.Expand();
            await CaptureLayout();
            var item = TorrentMenu
                .Items.OfType<MenuFlyoutItem>()
                .Single(item => item.Command == command);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("The torrent command cannot be invoked.");
            var acknowledged = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var accepted = Model.Text.Format(
                "outcomes",
                "accepted",
                Model.Text.Get("commands", command == Model.Pause ? "pause" : "resume")
            );
            void Announced(object? sender, string message)
            {
                // A resume under the global pause announces when the torrent starts instead.
                if (message == accepted || command == Model.Resume && message == Model.ResumeNotice)
                    acknowledged.TrySetResult();
            }
            Model.AnnouncementRequested += Announced;
            try
            {
                invoke.Invoke();
                await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally
            {
                Model.AnnouncementRequested -= Announced;
            }
            if (Model.HasCommandError)
                throw new InvalidOperationException("The native torrent command failed.");
            var directory =
                Model.DataDirectory
                ?? throw new InvalidOperationException("The shell capture has no store.");
            using var saved = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"))
            );
            var paused = saved
                .RootElement.GetProperty("torrents")
                .EnumerateArray()
                .Single(torrent =>
                    torrent.GetProperty("torrent_id").GetString() == target.TorrentId
                )
                .GetProperty("paused")
                .GetBoolean();
            var allPaused = saved
                .RootElement.GetProperty("settings")
                .GetProperty("all_paused")
                .GetBoolean();
            outcomes.Add(
                new
                {
                    command = item.Text,
                    enabled = item.IsEnabled,
                    persistedPaused = paused,
                    status = target.StatusCode,
                    globalPaused = allPaused,
                }
            );
            if (paused != (command == Model.Pause) || !allPaused || !Model.IsPaused)
                throw new InvalidOperationException(
                    "The native torrent command did not save its pause choice while retaining global pause."
                );
            expand.Collapse();
        }
        Run(Model.ShowSettings);
        await CaptureLayout();
        outcomes.Add(
            new
            {
                command = "settings",
                page = Model.Page.ToString(),
                torrentMenu = TorrentMenu.IsEnabled,
            }
        );
        await CapturePage("shell-settings", _settingsPage);
        Run(Model.ShowAbout);
        await CaptureLayout();
        outcomes.Add(new { command = "about", page = Model.Page.ToString() });
        await CapturePage("shell-about");
        Run(BackButton.Command);
        await CaptureLayout();
        outcomes.Add(new { command = "back", page = Model.Page.ToString() });
        Run(ThemeButton.Command);
        await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
        await CaptureLayout();
        outcomes.Add(new { command = "theme", theme = Root.ActualTheme.ToString() });
        Run(ThemeButton.Command);
        await CaptureReady(Model, () => Model.CanClose && Model.Theme == "dark");
        Model.IsFilterOpen = true;
        Model.Filter = TorrentFilter.Paused;
        await CapturePage("shell-filters");
        outcomes.Add(new { command = "filters", open = Workspace.IsPaneOpen });
        CloseFilters();
        await CapturePage("shell-filter-closed");
        outcomes.Add(
            new
            {
                command = "close-filters",
                open = Workspace.IsPaneOpen,
                filter = Model.Filter.ToString(),
            }
        );
        Search.Text = target.Name;
        await CaptureLayout();
        outcomes.Add(
            new { command = "search", matches = Model.FindSuggestions(Search.Text).Count }
        );
        await CapturePage("shell-search");
        Search.Text = string.Empty;
        Model.SelectLanguage("es");
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "es");
        await CaptureLayout();
        var narrow =
            ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth
            ?? 720;
        AppWindow.Resize(new SizeInt32(narrow, 560));
        await CaptureLayout();
        CaptureHitRegions(outcomes);
        await CapturePage("shell-spanish");
        await CaptureDialog(
            "shell-add",
            () =>
            {
                Run(Model.AddMagnet);
                return _interaction?.Completion.Task ?? Task.CompletedTask;
            },
            () => _interaction?.Dialog
        );
    }

    private void CaptureHitRegions(List<object> outcomes)
    {
        const uint WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;
        const int HTSYSMENU = 3;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Root.XamlRoot.RasterizationScale;
        var hits = new List<object>();
        var failed = false;

        void Hit(string name, Windows.Foundation.Point position, int expected)
        {
            var point = new PointInt32(
                (int)Math.Round(position.X * scale),
                (int)Math.Round(position.Y * scale)
            );
            if (!ClientToScreen(hwnd, ref point))
                throw new InvalidOperationException(
                    "The review window's client coordinates could not be converted to screen coordinates."
                );
            var coordinates = unchecked(
                (int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16))
            );
            var actual = (int)SendMessageW(hwnd, WM_NCHITTEST, 0, coordinates);
            hits.Add(
                new
                {
                    name,
                    screenX = point.X,
                    screenY = point.Y,
                    actual,
                    expected,
                }
            );
            failed |= actual != expected;
        }

        foreach (
            var control in new FrameworkElement[]
            {
                AppIcon,
                FileMenu,
                TorrentMenu,
                ViewMenu,
                HelpMenu,
                Search,
                AddButton,
                MagnetButton,
                ThemeButton,
            }
        )
        {
            var center = control
                .TransformToVisual(Root)
                .TransformPoint(
                    new Windows.Foundation.Point(control.ActualWidth / 2, control.ActualHeight / 2)
                );
            Hit(control.Name, center, control == AppIcon ? HTSYSMENU : HTCLIENT);
        }
        var menu = Menus
            .TransformToVisual(Root)
            .TransformBounds(
                new Windows.Foundation.Rect(0, 0, Menus.ActualWidth, Menus.ActualHeight)
            );
        var search = Search
            .TransformToVisual(Root)
            .TransformBounds(
                new Windows.Foundation.Rect(0, 0, Search.ActualWidth, Search.ActualHeight)
            );
        if (search.Left <= menu.Right)
            throw new InvalidOperationException(
                "The review caption has no unused gap between its menu and search."
            );
        Hit(
            "gap",
            new Windows.Foundation.Point(
                (menu.Right + search.Left) / 2,
                (menu.Top + menu.Bottom) / 2
            ),
            HTCAPTION
        );
        outcomes.Add(
            new
            {
                journey = "caption hit regions",
                language = Model.Text.Language,
                theme = Root.ActualTheme.ToString(),
                width = AppWindow.ClientSize.Width,
                hits,
            }
        );
        if (failed)
            throw new InvalidOperationException(
                "The review window returned an unexpected native caption hit classification."
            );
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref PointInt32 point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    private async Task CaptureSchedule(List<object> outcomes, List<string> completed)
    {
        var schedule = Model.Settings.Schedule;
        var existing = schedule.Periods.ToArray();
        SchedulePeriod? created = null;
        var limits = Model.LimitsIndex;
        try
        {
            // Periods show and change only under the weekly schedule.
            await Model.ChooseLimits(LimitMode.Schedule);
            if (!Model.FollowsSchedule)
                throw new InvalidOperationException(
                    "The review could not choose the weekly schedule."
                );
            await ShowSettings(new(SettingsCategory.Limits));
            await CaptureLayout();
            var page =
                _settingsPage
                ?? throw new InvalidOperationException("The review settings page did not open.");
            Button FindButton(string id) =>
                CaptureElements(page)
                    .OfType<Button>()
                    .Single(control => AutomationProperties.GetAutomationId(control) == id);
            TimePicker FindTime(string id) =>
                CaptureElements(page)
                    .OfType<TimePicker>()
                    .Single(control => AutomationProperties.GetAutomationId(control) == id);
            void ClearDays()
            {
                foreach (var day in CaptureElements(page).OfType<CheckBox>())
                    if (
                        AutomationProperties
                            .GetAutomationId(day)
                            .StartsWith("PeriodDay", StringComparison.Ordinal)
                    )
                        day.IsChecked = false;
            }
            CaptureInvoke(FindButton("AddPeriod"));
            await CaptureReady(schedule, () => !schedule.IsPending && schedule.IsOpen);
            created =
                schedule.OpenPeriod
                ?? throw new InvalidOperationException("Add period did not open the new period.");
            if (existing.Any(period => period.Matches(created)))
                throw new InvalidOperationException(
                    "The review schedule already holds the period that Add creates."
                );
            var draft =
                schedule.Draft
                ?? throw new InvalidOperationException("The new period has no editor.");
            FindTime("PeriodStart").SelectedTime = TimeSpan.FromMinutes(1337);
            FindTime("PeriodEnd").SelectedTime = TimeSpan.FromMinutes(103);
            await CaptureReady(
                schedule,
                () => !schedule.IsPending && schedule.OpenPeriod is { Start: 1337, End: 103 }
            );
            // Each cleared day saves at once until none is left, so the saved
            // period keeps whichever days remained before the last.
            ClearDays();
            await CaptureReady(schedule, () => schedule.HasScheduleError);
            if (
                schedule.Draft != draft
                || draft.Start?.TotalMinutes != 1337
                || draft.End?.TotalMinutes != 103
                || schedule.Periods.Count != existing.Length + 1
                || schedule.OpenPeriod is not { Days.Count: > 0 }
            )
                throw new InvalidOperationException(
                    "Clearing every day changed the saved schedule or lost the editor input."
                );
            outcomes.Add(
                new
                {
                    journey = "empty schedule days",
                    rejected = true,
                    draftRetained = true,
                    exactMinutesRetained = true,
                }
            );
            await CapturePage("schedule-invalid-period", page);
            CaptureElements(page)
                .OfType<CheckBox>()
                .Single(control => AutomationProperties.GetAutomationId(control) == "PeriodDay0")
                .IsChecked = true;
            await CaptureReady(
                schedule,
                () =>
                    !schedule.IsPending
                    && !schedule.HasScheduleError
                    && schedule.OpenPeriod?.Days.Count == 1
            );
            created = schedule.Periods.Single(period =>
                period.Days.SequenceEqual(new[] { 0 })
                && period.Start == 1337
                && period.End == 103
                && period.Mode == ScheduleMode.Alternative
            );
            if (
                created.Span.Duration != 206
                || created.Occurrences(0).Single().End != 1543
                || created.Occurrences(1).Single().End != 103
                || schedule.Ranges(1).First(range => range.Start <= 60 && range.End > 60).Mode
                    != ScheduleMode.Paused
            )
                throw new InvalidOperationException(
                    "The overnight period lost exact minutes or pause precedence."
                );
            outcomes.Add(
                new
                {
                    journey = "save overnight period",
                    start = created.Start,
                    end = created.End,
                    duration = created.Span.Duration,
                    nextDayOccurrence = true,
                    pausePrecedence = true,
                }
            );

            foreach (var language in new[] { "en", "es" })
            {
                Model.SelectLanguage(language);
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
                foreach (var theme in new[] { "light", "dark" })
                {
                    await Model.Settings.SelectTheme(theme);
                    await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                    foreach (
                        var size in new[]
                        {
                            new SizeInt32(720, 560),
                            new SizeInt32(1040, 680),
                            new SizeInt32(1280, 800),
                        }
                    )
                    {
                        var scale = Root.XamlRoot.RasterizationScale;
                        var minimum =
                            (
                                (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter
                            ).PreferredMinimumWidth
                            ?? 0;
                        AppWindow.Resize(
                            new SizeInt32(
                                Math.Max((int)(size.Width * scale), minimum),
                                (int)(size.Height * scale)
                            )
                        );
                        var prefix =
                            "schedule-"
                            + language
                            + "-"
                            + theme
                            + "-"
                            + size.Width
                            + "x"
                            + size.Height;
                        await schedule.Close();
                        await CapturePage(prefix + "-overview", page);
                        await schedule.Open(created);
                        await CapturePage(prefix + "-open", page);
                        ClearDays();
                        await CaptureReady(schedule, () => schedule.HasScheduleError);
                        await CaptureLayout();
                        await CaptureUi(prefix + "-validation");
                        await schedule.Close();
                        if (!schedule.Periods.Contains(created) || created.Days.Count != 1)
                            throw new InvalidOperationException(
                                "Closing the period changed the saved period."
                            );
                        completed.Add(prefix);
                    }
                }
            }
            outcomes.Add(new { journey = "close invalid period", savedPeriodRetained = true });

            await schedule.Reschedule(created, created.Span.Adjust(PeriodAction.Move, 15));
            await CaptureReady(
                schedule,
                () => !schedule.IsPending && schedule.OpenPeriod?.Start != 1337
            );
            created =
                schedule.OpenPeriod
                ?? throw new InvalidOperationException("The moved period did not stay open.");
            if (
                schedule.HasScheduleError
                || created.Start != 1350
                || created.End != 116
                || created.Span.Duration != 206
            )
                throw new InvalidOperationException(
                    "Moving the period did not preserve its duration and snap its start."
                );
            outcomes.Add(
                new
                {
                    journey = "move period",
                    start = created.Start,
                    end = created.End,
                    duration = created.Span.Duration,
                }
            );
            await CapturePage("schedule-moved-period", page);
            CaptureInvoke(FindButton("RemovePeriod"));
            await CaptureReady(
                schedule,
                () => !schedule.IsPending && schedule.Periods.Count == existing.Length
            );
            if (
                !existing.All(period =>
                    schedule.Periods.Any(candidate => candidate.Matches(period))
                )
            )
                throw new InvalidOperationException(
                    "Removing the review period changed an existing period."
                );
            created = null;
            outcomes.Add(new { journey = "remove review period", existingPeriodsRetained = true });
        }
        finally
        {
            await schedule.Close();
            if (
                created is { } saved
                && schedule.Periods.FirstOrDefault(period => period.Matches(saved)) is { } remaining
            )
                await schedule.Remove(remaining);
            if (limits >= 0)
                await Model.ChooseLimits((LimitMode)limits);
        }
    }

    private async Task CaptureDesktop(Torrent target, List<object> outcomes, List<string> completed)
    {
        const string magnet = "magnet:?xt=urn:btih:";
        await ShowTorrents();
        Run(Model.AddMagnet);
        await CaptureLayout();
        var dialog =
            _interaction?.Dialog
            ?? throw new InvalidOperationException("The desktop review Add dialog did not open.");
        var input = CaptureElements(dialog)
            .OfType<TextBox>()
            .Single(control => control.Name == "MagnetInput");
        input.Focus(FocusState.Programmatic);
        input.Text = magnet;
        await CaptureLayout();
        if (Model.AddDraft.Magnet != magnet || !Model.AddDraft.HasChanges)
            throw new InvalidOperationException(
                "The unfinished native magnet input did not reach its draft."
            );
        foreach (var language in new[] { "en", "es" })
        {
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
            foreach (var theme in new[] { "light", "dark" })
            {
                await Model.Settings.SelectTheme(theme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                foreach (
                    var size in new[]
                    {
                        new SizeInt32(720, 560),
                        new SizeInt32(1040, 680),
                        new SizeInt32(1280, 800),
                    }
                )
                {
                    var scale = Root.XamlRoot.RasterizationScale;
                    var minimum =
                        (
                            (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter
                        ).PreferredMinimumWidth
                        ?? 0;
                    AppWindow.Resize(
                        new SizeInt32(
                            Math.Max((int)(size.Width * scale), minimum),
                            (int)(size.Height * scale)
                        )
                    );
                    var prefix =
                        "desktop-" + language + "-" + theme + "-" + size.Width + "x" + size.Height;
                    Run(Model.Exit);
                    await CaptureReady(
                        Model,
                        () => _interaction is { IsDraftDecision: true, Dialog: not null }
                    );
                    await CapturePage(prefix + "-exit-prompt");
                    var prompt =
                        _interaction?.Dialog
                        ?? throw new InvalidOperationException(
                            "Exit did not retain its draft prompt."
                        );
                    CaptureInvoke(
                        CaptureElements(prompt)
                            .OfType<Button>()
                            .Single(control => control.Name == "CloseButton")
                    );
                    await CaptureReady(
                        Model,
                        () =>
                            !Model.IsClosing
                            && Model.CanEdit
                            && Model.IsAddOpen
                            && _interaction is { IsDraftDecision: false, Dialog: not null }
                    );
                    await CaptureLayout();
                    dialog =
                        _interaction?.Dialog
                        ?? throw new InvalidOperationException(
                            "Keep input did not recover the Add dialog."
                        );
                    input = CaptureElements(dialog)
                        .OfType<TextBox>()
                        .Single(control => control.Name == "MagnetInput");
                    if (
                        input.Text != magnet
                        || Model.AddDraft.Magnet != magnet
                        || !Model.AddDraft.HasChanges
                    )
                        throw new InvalidOperationException(
                            "Keep input lost the unfinished magnet."
                        );
                    completed.Add(prefix);
                }
            }
        }
        outcomes.Add(
            new
            {
                journey = "Exit with unfinished magnet",
                prompts = 12,
                inputRetained = true,
                addDialogRecovered = true,
                engineConnected = Model.IsConnected,
            }
        );
        await CapturePage("desktop-kept-magnet", dialog.Content as FrameworkElement);
        input.Focus(FocusState.Programmatic);
        Run(Model.Exit);
        await CaptureReady(
            Model,
            () => _interaction is { IsDraftDecision: true, Dialog: not null }
        );
        await CapturePage("desktop-exit-save-invalid");
        CaptureInvoke(
            _interaction?.Dialog
                ?? throw new InvalidOperationException("Exit did not retain its Save prompt.")
        );
        await CaptureReady(
            Model,
            () =>
                !Model.IsClosing
                && Model.CanEdit
                && Model.IsAddOpen
                && _interaction is { IsDraftDecision: false, Dialog: not null }
                && Model.AddDraft.HasMagnetError
        );
        await CaptureLayout();
        dialog =
            _interaction?.Dialog
            ?? throw new InvalidOperationException("Failed Save did not recover the Add dialog.");
        input = CaptureElements(dialog)
            .OfType<TextBox>()
            .Single(control => control.Name == "MagnetInput");
        if (
            input.Text != magnet
            || Model.AddDraft.Magnet != magnet
            || !Model.AddDraft.HasChanges
            || !ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), input)
        )
            throw new InvalidOperationException(
                "Failed Save lost the unfinished magnet or its editor focus."
            );
        await CapturePage("desktop-exit-save-failed", dialog.Content as FrameworkElement);
        outcomes.Add(
            new
            {
                journey = "Exit Save with invalid magnet",
                errorRetained = true,
                inputRetained = true,
                addDialogRecovered = true,
                focusRetained = true,
                engineConnected = Model.IsConnected,
            }
        );
        var closed =
            _interaction?.Completion.Task
            ?? throw new InvalidOperationException(
                "The recovered Add dialog has no close completion."
            );
        CaptureInvoke(
            CaptureElements(dialog)
                .OfType<Button>()
                .Single(control => control.Name == "CloseButton")
        );
        await closed;
        await CaptureReady(
            Model.AddDraft,
            () => !Model.AddDraft.HasChanges && !Model.AddDraft.EditingMagnet
        );
        outcomes.Add(new { journey = "cancel recovered Add", draftCleared = true });

        Model.SelectLanguage("es");
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "es");
        await Model.Settings.SelectTheme("light");
        await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
        var raster = Root.XamlRoot.RasterizationScale;
        var minimumWidth =
            ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth
            ?? 0;
        AppWindow.Resize(
            new SizeInt32(Math.Max((int)(1040 * raster), minimumWidth), (int)(680 * raster))
        );
        var membership = Model.Torrents.Select(torrent => torrent.TorrentId).Order().ToArray();
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.Trackers);
        await CaptureReady(
            Model.Inspector,
            () => !Model.Inspector.IsLoading && Model.Inspector.EditTrackers.CanExecute(null)
        );
        await CaptureLayout();
        CaptureInvoke(
            CaptureElements(InspectorContent)
                .OfType<Button>()
                .Single(control => control.Name == "EditTrackers")
        );
        await CaptureLayout();
        var editor = CaptureElements(InspectorContent)
            .OfType<TextBox>()
            .Single(control => control.Name == "TrackerInput");
        const string trackerInput = "https://example.invalid/unfinished";
        editor.Text = trackerInput;
        await CaptureLayout();
        if (!Model.Inspector.HasDraft || Model.Inspector.TrackerInput != trackerInput)
            throw new InvalidOperationException(
                "The native tracker editor did not retain its unfinished input."
            );
        await CapturePage("desktop-before-restart", InspectorContent.Content as FrameworkElement);
        var signal = Path.Combine(
            _captureDirectory
                ?? throw new InvalidOperationException(
                    "The desktop review has no capture directory."
                ),
            "restart.txt"
        );
        await File.WriteAllTextAsync(signal, "restart");
        await CaptureReady(Model, () => !Model.IsConnected);
        if (
            !Model.Inspector.HasDraft
            || Model.Inspector.TrackerInput != trackerInput
            || Model.Inspector.CanSaveTrackers
            || !Model.Inspector.CancelTrackers.CanExecute(null)
        )
            throw new InvalidOperationException(
                "Disconnect did not preserve a safely cancellable tracker draft."
            );
        var requestedTheme = Root.RequestedTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (
                var size in new[]
                {
                    new SizeInt32(720, 560),
                    new SizeInt32(1040, 680),
                    new SizeInt32(1280, 800),
                }
            )
            {
                AppWindow.Resize(
                    new SizeInt32(
                        Math.Max((int)(size.Width * raster), minimumWidth),
                        (int)(size.Height * raster)
                    )
                );
                Root.RequestedTheme = theme;
                await CapturePage(
                    "desktop-disconnected-es-"
                        + theme.ToString().ToLowerInvariant()
                        + "-"
                        + size.Width
                        + "x"
                        + size.Height,
                    InspectorContent.Content as FrameworkElement
                );
            }
        }
        finally
        {
            Root.RequestedTheme = requestedTheme;
            AppWindow.Resize(
                new SizeInt32(Math.Max((int)(1040 * raster), minimumWidth), (int)(680 * raster))
            );
        }
        await File.WriteAllTextAsync(signal, "disconnected");
        await CaptureReady(Model, () => Model.IsConnected && !Model.IsLoading && Model.CanEdit);
        await CaptureReady(
            Model.Inspector,
            () => !Model.Inspector.IsLoading && Model.Inspector.IsAvailable
        );
        if (
            !membership.SequenceEqual(Model.Torrents.Select(torrent => torrent.TorrentId).Order())
            || Model.Inspector.Target?.TorrentId != target.TorrentId
            || !Model.Inspector.HasDraft
            || Model.Inspector.TrackerInput != trackerInput
            || editor.Text != trackerInput
        )
            throw new InvalidOperationException(
                "Reconnect lost torrent membership or the unfinished tracker draft."
            );
        outcomes.Add(
            new
            {
                journey = "engine restart with tracker draft",
                reconnected = true,
                membershipRetained = true,
                targetRetained = true,
                draftRetained = true,
            }
        );
        await CapturePage("desktop-reconnected", InspectorContent.Content as FrameworkElement);
        CaptureInvoke(
            CaptureElements(InspectorContent)
                .OfType<Button>()
                .Single(control => control.Name == "CancelTrackers")
        );
        await CaptureReady(
            Model.Inspector,
            () => !Model.Inspector.IsEditingTrackers && !Model.Inspector.HasDraft
        );
        Model.CloseInspector();
    }

    // A real Resume while every transfer is paused: the row changes to All
    // paused and the notice says when the torrent starts and offers Resume.
    private async Task CaptureResumeNotice(
        Torrent target,
        List<object> outcomes,
        List<string> completed
    )
    {
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        var announced = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void Announced(object? sender, string message)
        {
            if (message == Model.ResumeNotice)
                announced.TrySetResult();
        }
        Model.AnnouncementRequested += Announced;
        try
        {
            Run(Model.Resume);
            await announced.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            Model.AnnouncementRequested -= Announced;
        }
        try
        {
            await CaptureReady(
                Model,
                () => target.StatusCode == "all_paused" && Model.HasResumeNotice
            );
            await CaptureMatrix(
                async (suffix, _) =>
                {
                    var name = "footer-" + suffix + "-resume-notice";
                    await CaptureLayout();
                    await CaptureUi(name);
                    var action = (ResumeNotice.ActionButton as Controls.ActionButton)?.Text;
                    outcomes.Add(
                        new
                        {
                            journey = name,
                            scope = "Real Resume while every transfer is paused; the notice button is not pressed",
                            status = target.StatusCode,
                            message = ResumeNotice.Message,
                            action,
                            severity = ResumeNotice.Severity.ToString(),
                        }
                    );
                    completed.Add(name);
                    if (
                        ResumeNotice.Visibility != Visibility.Visible
                        || !ResumeNotice.IsOpen
                        || ResumeNotice.Severity != InfoBarSeverity.Informational
                        || ResumeNotice.Message != Model.ResumeNotice
                        || !ResumeNotice.Message.Contains(target.Name, StringComparison.Ordinal)
                        || action != Model.Text.Get("commands", "resume")
                        || target.StatusCode != "all_paused"
                    )
                        throw new InvalidOperationException(
                            "The blocked resume did not show All paused and its notice."
                        );
                }
            );
        }
        finally
        {
            Run(Model.Pause);
            await CaptureReady(
                Model,
                () => target.StatusCode == "paused" && !Model.HasResumeNotice
            );
        }
    }

    private async Task CaptureFooter(Torrent target, List<object> outcomes, List<string> completed)
    {
        var filter = Model.Filter;
        var language = Model.Text.Language;
        var theme = Model.Theme;
        try
        {
            if (!target.IsPaused)
                throw new InvalidOperationException("The footer fixture must be paused.");
            Model.Filter = TorrentFilter.All;
            await ShowTorrents();
            Torrents.Selection = new Syno.TableView.Selection([target], target);
            await SelectTorrent();
            Run(Model.Properties);
            Model.Inspector.Select(InspectorSection.General);
            await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading);
            await CaptureLayout();
            var update = UpdateAvailable;
            await CaptureMatrix(
                async (suffix, _) =>
                {
                    var error = Model.Text.Format(
                        "errors",
                        "torrent",
                        target.Name,
                        Model.Text.Get("errors", "move_failed")
                    );
                    object Bounds(FrameworkElement element)
                    {
                        var position = element
                            .TransformToVisual(Root)
                            .TransformPoint(new Windows.Foundation.Point());
                        return new
                        {
                            x = position.X,
                            y = position.Y,
                            width = element.ActualWidth,
                            height = element.ActualHeight,
                        };
                    }
                    async Task Scene(string state, string scope)
                    {
                        var name = "footer-" + suffix + "-" + state;
                        var message = TorrentError.Message;
                        var visibility = TorrentError.Visibility;
                        var open = TorrentError.IsOpen;
                        var updateVisibility = update.Visibility;
                        var errorRequested = state is "torrent-error" or "combined";
                        var updateRequested = state is "update" or "combined";
                        void Present()
                        {
                            if (errorRequested)
                            {
                                TorrentError.Message = error;
                                TorrentError.IsOpen = true;
                                TorrentError.Visibility = Visibility.Visible;
                            }
                            if (updateRequested)
                                update.Visibility = Visibility.Visible;
                        }
                        PropertyChangedEventHandler changed = (_, _) => Present();
                        Model.PropertyChanged += changed;
                        try
                        {
                            Present();
                            await CaptureLayout();
                            await CaptureUi(name);
                            var retained =
                                Model.Torrents.Any(torrent => torrent.TorrentId == target.TorrentId)
                                && Model.Inspector.Target?.TorrentId == target.TorrentId;
                            outcomes.Add(
                                new
                                {
                                    journey = name,
                                    scope,
                                    table = Bounds(Torrents),
                                    inspector = Bounds(InspectorContent),
                                    status = Bounds(StatusBar),
                                    filter = Model.Filter.ToString(),
                                    fixtureRetained = retained,
                                    errorVisible = TorrentError.Visibility == Visibility.Visible,
                                    errorText = TorrentError.Message,
                                    errorOpen = TorrentError.IsOpen,
                                    updateVisible = update.Visibility == Visibility.Visible,
                                    errorRequested,
                                    errorRetained = TorrentError.Message == error
                                        && TorrentError.IsOpen
                                        && TorrentError.Visibility == Visibility.Visible,
                                    updateRequested,
                                }
                            );
                            completed.Add(name);
                            if (!retained)
                                throw new InvalidOperationException(
                                    "The footer capture lost its fixture or inspector target."
                                );
                        }
                        finally
                        {
                            Model.PropertyChanged -= changed;
                            TorrentError.Message = message;
                            TorrentError.Visibility = visibility;
                            TorrentError.IsOpen = open;
                            update.Visibility = updateVisibility;
                            Bindings.Update();
                        }
                    }
                    try
                    {
                        await Scene("baseline", "Real paused fixture; no presentation overrides");
                        Model.Filter = TorrentFilter.Paused;
                        await Scene("filtered", "Real Paused filter setter");
                        Model.Filter = TorrentFilter.All;
                        await Scene("filter-cleared", "Real All filter setter");
                        await Scene(
                            "torrent-error",
                            "Simulated selected-torrent error presentation; no engine command"
                        );
                        await Scene("update", "Simulated update-status visibility");
                        Model.Filter = TorrentFilter.Paused;
                        await Scene(
                            "combined",
                            "Real Paused filter with simulated torrent-error and update presentation; no engine command or launch"
                        );
                    }
                    finally
                    {
                        Model.Filter = TorrentFilter.All;
                        Bindings.Update();
                    }
                }
            );
            await CaptureResumeNotice(target, outcomes, completed);
        }
        finally
        {
            Model.Filter = filter;
            Bindings.Update();
            await Model.Settings.SelectTheme(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            Model.SelectLanguage(language);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
        }
    }

    private async Task CaptureDetails(Torrent target, List<object> outcomes)
    {
        var directory =
            Model.DataDirectory
            ?? throw new InvalidOperationException("The detail capture has no store.");
        var preview = Path.Combine(directory, "preview.torrent");
        var metadata = System.Text.Encoding.ASCII.GetBytes(
            "d4:infod6:lengthi1e4:name11:preview.bin12:piece lengthi16384e6:pieces20:"
        );
        await File.WriteAllBytesAsync(
            preview,
            metadata
                .Concat(Convert.FromHexString("11f6ad8ec52a2984abaafd7c3b516503785c2072"))
                .Concat(System.Text.Encoding.ASCII.GetBytes("ee"))
                .ToArray()
        );
        await Model.AddDraft.Cancel();
        Model.AddDraft.EditingMagnet = true;
        var closed = ShowAdd();
        await CaptureLayout();
        var dialog =
            _interaction?.Dialog
            ?? throw new InvalidOperationException("The preview review Add dialog did not open.");
        try
        {
            Model.AddDraft.Own([preview]);
            await Model.AddDraft.PrepareAll().WaitAsync(TimeSpan.FromSeconds(20));
            await CaptureReady(
                Model.AddDraft,
                () => Model.AddDraft.HasFiles && !Model.AddDraft.IsPending
            );
            await CapturePage("details-add-file-preview", dialog.Content as FrameworkElement);
            var table = CaptureElements(dialog)
                .OfType<Syno.TableView.Table>()
                .Single(control => control.Name == "Files");
            var sourceRetained = ReferenceEquals(table.ItemsSource, Model.AddDraft.Files.Roots);
            var fileDisplayed = CaptureElements(table)
                .OfType<TextBlock>()
                .Any(control => control.Text == "preview.bin");
            outcomes.Add(
                new
                {
                    journey = "native Add file preview",
                    sourceRetained,
                    fileDisplayed,
                    files = Model.AddDraft.Files.Roots.Count,
                }
            );
            if (!sourceRetained || !fileDisplayed)
                throw new InvalidOperationException(
                    "The native file browser did not follow the metadata-ready Add draft."
                );
        }
        finally
        {
            dialog.Hide();
            await closed;
        }
        await ShowTorrents();
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.Files);
        await CaptureReady(
            Model.Inspector,
            () => !Model.Inspector.IsLoading && Model.Inspector.HasFiles
        );
        await CaptureLayout();
        var file = Model
            .Inspector.Files.Roots.SelectMany(root => root.Nodes())
            .First(node => node.Index >= 0 && !node.IsPadding);
        var priority = CaptureElements(InspectorContent).OfType<ComboBox>().First();
        var originalPriority = priority.SelectedIndex;
        priority.SelectedIndex = 4;
        await CaptureReady(
            Model.Inspector,
            () =>
                !Model.Inspector.IsPending
                && !Model.Inspector.HasFileChanges
                && !Model.Inspector.IsLoading
        );
        if (file.Priority != 7 || !Model.Inspector.Files.HasWanted)
            throw new InvalidOperationException("The native priority choice was not saved.");
        outcomes.Add(
            new
            {
                journey = "native file priority",
                priority = file.Priority,
                appliedImmediately = true,
            }
        );
        priority.SelectedIndex = originalPriority;
        await CaptureReady(
            Model.Inspector,
            () =>
                !Model.Inspector.IsPending
                && !Model.Inspector.HasFileChanges
                && !Model.Inspector.IsLoading
        );
        if (file.PriorityIndex != originalPriority)
            throw new InvalidOperationException(
                "The native priority choice could not be restored."
            );

        Model.Inspector.Select(InspectorSection.Trackers);
        await CaptureReady(
            Model.Inspector,
            () => !Model.Inspector.IsLoading && Model.Inspector.EditTrackers.CanExecute(null)
        );
        await CaptureLayout();
        Button FindButton(string name) =>
            CaptureElements(InspectorContent)
                .OfType<Button>()
                .Single(control => control.Name == name);
        var trackers = (Model.Inspector.Trackers ?? [])
            .Select(tracker => (tracker.Url, tracker.Tier))
            .ToHashSet();
        CaptureInvoke(FindButton("EditTrackers"));
        await CaptureLayout();
        var editor = CaptureElements(InspectorContent)
            .OfType<TextBox>()
            .Single(control => control.Name == "TrackerInput");
        var originalInput = editor.Text;
        var populated = new (string Url, int Tier)[]
        {
            ("https://example.invalid/announce", 0),
            (
                "https://example.invalid/private/trackers/announce?passkey=0123456789abcdef0123456789abcdef&source=tinytorrent&region=western-europe",
                0
            ),
            ("udp://example.invalid:6969/announce", 1),
            ("https://example.invalid/backup/announce", 1),
        }.ToHashSet();
        var trackerInput = string.Join(
            Environment.NewLine + Environment.NewLine,
            populated
                .GroupBy(tracker => tracker.Tier)
                .OrderBy(tier => tier.Key)
                .Select(tier =>
                    string.Join(Environment.NewLine, tier.Select(tracker => tracker.Url))
                )
        );
        editor.Focus(FocusState.Programmatic);
        editor.Text = trackerInput;
        await CaptureLayout();
        var nativeInput = editor.Text;
        var modelInput = Model.Inspector.TrackerInput;
        var language = Model.Text.Language == "en" ? "es" : "en";
        Model.SelectLanguage(language);
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
        await CaptureLayout();
        var inputRetained = editor.Text == nativeInput;
        var modelRetained = Model.Inspector.TrackerInput == modelInput;
        var focusRetained = ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), editor);
        outcomes.Add(
            new
            {
                journey = "language switch with tracker draft",
                inputRetained,
                modelRetained,
                focusRetained,
                draftRetained = Model.Inspector.HasDraft,
                inputCanonicalized = nativeInput != trackerInput,
                carriageReturns = nativeInput.Count(character => character == '\r'),
                lineFeeds = nativeInput.Count(character => character == '\n'),
                focusSource = "programmatic native focus",
                language,
            }
        );
        if (!inputRetained || !modelRetained || !Model.Inspector.HasDraft || !focusRetained)
            throw new InvalidOperationException(
                "The live language switch lost tracker input or focus."
            );
        await CapturePage(
            "details-live-tracker-draft",
            InspectorContent.Content as FrameworkElement
        );
        if (!Model.IsPaused)
            throw new InvalidOperationException(
                "The tracker capture requires a globally paused fixture."
            );
        CaptureInvoke(FindButton("SaveTrackers"));
        await CaptureReady(
            Model.Inspector,
            () =>
                !Model.Inspector.IsEditingTrackers
                && !Model.Inspector.IsPending
                && !Model.Inspector.IsLoading
                && populated.SetEquals(
                    (Model.Inspector.Trackers ?? []).Select(tracker => (tracker.Url, tracker.Tier))
                )
        );
        outcomes.Add(
            new
            {
                journey = "native tracker save",
                confirmed = true,
                count = populated.Count,
                tiers = new[] { 0, 1 },
                globalPaused = Model.IsPaused,
            }
        );
        foreach (var captureLanguage in new[] { "en", "es" })
        foreach (var theme in new[] { "light", "dark" })
        {
            await Model.Settings.SelectTheme(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            Model.SelectLanguage(captureLanguage);
            await CaptureReady(
                Model,
                () => Model.CanClose && Model.Text.Language == captureLanguage
            );
            await CaptureLayout();
            foreach (
                var size in new[]
                {
                    new SizeInt32(720, 560),
                    new SizeInt32(1040, 680),
                    new SizeInt32(1280, 800),
                }
            )
            {
                if (!Model.IsPaused)
                    throw new InvalidOperationException(
                        "The populated tracker capture lost global pause."
                    );
                var scale = Root.XamlRoot.RasterizationScale;
                var minimum =
                    (
                        (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter
                    ).PreferredMinimumWidth
                    ?? 0;
                AppWindow.Resize(
                    new SizeInt32(
                        Math.Max((int)Math.Round(size.Width * scale), minimum),
                        (int)Math.Round(size.Height * scale)
                    )
                );
                await CapturePage(
                    $"details-{captureLanguage}-{theme}-{size.Width}x{size.Height}-trackers",
                    InspectorContent.Content as FrameworkElement
                );
                outcomes.Add(
                    new
                    {
                        journey = "populated tracker capture",
                        language = captureLanguage,
                        theme,
                        requestedWidth = size.Width,
                        requestedHeight = size.Height,
                        clientWidth = AppWindow.ClientSize.Width,
                        count = Model.Inspector.Trackers?.Count ?? 0,
                        globalPaused = Model.IsPaused,
                    }
                );
            }
        }
        CaptureInvoke(FindButton("EditTrackers"));
        await CaptureLayout();
        if (Model.Inspector.HasDraft)
            throw new InvalidOperationException(
                "Reopening the saved tracker list created an untouched draft."
            );
        outcomes.Add(
            new
            {
                journey = "untouched populated tracker editor",
                draftCreated = false,
                count = Model.Inspector.Trackers?.Count ?? 0,
            }
        );
        editor = CaptureElements(InspectorContent)
            .OfType<TextBox>()
            .Single(control => control.Name == "TrackerInput");
        editor.Text = originalInput;
        CaptureInvoke(FindButton("SaveTrackers"));
        await CaptureReady(
            Model.Inspector,
            () =>
                !Model.Inspector.IsEditingTrackers
                && !Model.Inspector.IsPending
                && !Model.Inspector.IsLoading
                && trackers.SetEquals(
                    (Model.Inspector.Trackers ?? []).Select(tracker => (tracker.Url, tracker.Tier))
                )
        );
        Model.CloseInspector();

        var settings = Model.Settings;
        await ShowSettings(new(SettingsCategory.General));
        await CaptureLayout();
        var page =
            _settingsPage
            ?? throw new InvalidOperationException("The detail review settings page did not open.");
        var toggle = CaptureElements(page)
            .OfType<ToggleSwitch>()
            .Single(control => ReferenceEquals(control.Tag, settings.ShowSplash));
        var originalSplash = toggle.IsOn;
        toggle.IsOn = !originalSplash;
        await CaptureReady(
            settings,
            () => !settings.ShowSplash.IsPending && !settings.ShowSplash.HasDraft
        );
        if (settings.ShowSplash.IsOn == originalSplash)
            throw new InvalidOperationException(
                "The native setting switch did not apply immediately."
            );
        toggle.IsOn = originalSplash;
        await CaptureReady(
            settings,
            () => !settings.ShowSplash.IsPending && !settings.ShowSplash.HasDraft
        );
        if (settings.ShowSplash.IsOn != originalSplash)
            throw new InvalidOperationException("The native setting switch could not be restored.");
        outcomes.Add(
            new
            {
                journey = "native immediate setting",
                applied = true,
                restored = true,
            }
        );
    }

    private async Task CaptureReview()
    {
        var clock = Stopwatch.StartNew();
        var completed = new List<string>();
        var outcomes = new List<object>();
        Exception? failure = null;
        try
        {
            var store = Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_STORE");
            if (store is null || !Path.IsPathFullyQualified(store))
                throw new InvalidOperationException(
                    "Capture review requires an absolute disposable store path."
                );
            await CaptureReady(Model, () => Model.IsConnected && !Model.IsLoading);
            await Model.LanguageLoad;
            if (
                !string.Equals(
                    Path.GetFullPath(store).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(Model.DataDirectory ?? string.Empty)
                        .TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvalidOperationException(
                    "The connected engine does not own the capture store."
                );
            if (ReviewMode == CaptureMode.SettingsLayout)
            {
                foreach (var language in new[] { "en", "es" })
                foreach (var theme in new[] { "light", "dark" })
                {
                    Model.SelectLanguage(language);
                    await CaptureReady(
                        Model,
                        () => Model.CanClose && Model.Text.Language == language
                    );
                    await Model.Settings.SelectTheme(theme);
                    await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                    foreach (
                        var size in new[]
                        {
                            new SizeInt32(720, 560),
                            new SizeInt32(1040, 680),
                            new SizeInt32(1280, 800),
                        }
                    )
                    {
                        var scale = Root.XamlRoot.RasterizationScale;
                        var minimum =
                            (
                                (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter
                            ).PreferredMinimumWidth
                            ?? 0;
                        AppWindow.Resize(
                            new SizeInt32(
                                Math.Max((int)(size.Width * scale), minimum),
                                (int)(size.Height * scale)
                            )
                        );
                        await ShowSettings(new(SettingsCategory.General));
                        var page =
                            _settingsPage
                            ?? throw new InvalidOperationException(
                                "The settings page did not open."
                            );
                        await CaptureReady(
                            Model.Settings,
                            () => Model.Settings.HasRegistration && !Model.Settings.IsPending
                        );
                        var name =
                            "settings-"
                            + language
                            + "-"
                            + theme
                            + "-"
                            + size.Width
                            + "x"
                            + size.Height;
                        await CapturePage(name, page);
                        if (language == "en" && theme == "light" && size.Width == 720)
                        {
                            var startup = (ToggleSwitch)page.FindName("Startup");
                            var handlers = (ToggleSwitch)page.FindName("Handlers");
                            var matches =
                                startup.IsOn == Model.Settings.Startup
                                && handlers.IsOn == Model.Settings.HandlersRegistered
                                && startup.IsEnabled == Model.Settings.CanRegister
                                && handlers.IsEnabled == Model.Settings.CanRegister;
                            outcomes.Add(
                                new
                                {
                                    journey = "observed native registration",
                                    startup = startup.IsOn,
                                    handlers = handlers.IsOn,
                                    observedStartup = Model.Settings.Startup,
                                    observedHandlers = Model.Settings.HandlersRegistered,
                                    startupEnabled = startup.IsEnabled,
                                    handlersEnabled = handlers.IsEnabled,
                                    canRegister = Model.Settings.CanRegister,
                                    matches,
                                }
                            );
                            if (!matches)
                                throw new InvalidOperationException(
                                    "Native registration switches do not match their settled observed state."
                                );
                        }
                        foreach (var section in new[] { "StartupSection", "DefaultAppSection" })
                        {
                            var element =
                                page.FindName(section) as FrameworkElement
                                ?? throw new InvalidOperationException(
                                    "The registration section did not load."
                                );
                            element.StartBringIntoView(
                                new BringIntoViewOptions
                                {
                                    AnimationDesired = false,
                                    VerticalAlignmentRatio = 0,
                                }
                            );
                            await CaptureLayout();
                            await CaptureUi(
                                name
                                    + "-"
                                    + section.Replace("Section", string.Empty).ToLowerInvariant()
                            );
                        }
                        if (page.FindName("NotificationsSection") is FrameworkElement notifications)
                        {
                            notifications.StartBringIntoView(
                                new BringIntoViewOptions
                                {
                                    AnimationDesired = false,
                                    VerticalAlignmentRatio = 0,
                                }
                            );
                            await CaptureLayout();
                            await CaptureUi(name + "-notifications");
                        }
                        if (language == "en" && theme == "light" && size.Width == 1040)
                        {
                            var setting = Model.Settings.AddedNotifications;
                            var toggle = CaptureElements(page)
                                .OfType<ToggleSwitch>()
                                .Single(control => ReferenceEquals(control.Tag, setting));
                            var original = setting.ConfirmedOn;
                            toggle.IsOn = !original;
                            await CaptureReady(
                                setting,
                                () =>
                                    !setting.IsPending
                                    && !setting.HasDraft
                                    && setting.ConfirmedOn == !original
                            );
                            toggle.IsOn = original;
                            await CaptureReady(
                                setting,
                                () =>
                                    !setting.IsPending
                                    && !setting.HasDraft
                                    && setting.ConfirmedOn == original
                            );
                            outcomes.Add(
                                new
                                {
                                    journey = "notification setting",
                                    nativeToggleCommitted = true,
                                    originalRestored = true,
                                }
                            );
                        }
                        await ShowTorrents();
                        var torrent = Model.Torrents.First();
                        Model.ReceiveNotice(
                            JsonSerializer.SerializeToElement(
                                new
                                {
                                    type = "notice",
                                    kind = "completed",
                                    torrent_id = torrent.TorrentId,
                                    name = torrent.Name,
                                    detail = string.Empty,
                                    count = 1,
                                }
                            )
                        );
                        await CaptureLayout();
                        if (!Model.HasCompletion || !Model.OpenCompletion.CanExecute(null))
                            throw new InvalidOperationException(
                                "The completion notice has no available folder action."
                            );
                        await CapturePage(name + "-completion");
                        if (language == "en" && theme == "light" && size.Width == 1040)
                        {
                            Search.Focus(FocusState.Programmatic);
                            await CaptureLayout();
                            if (
                                !HasEditorFocus()
                                || CompletionNotice.Visibility != Visibility.Collapsed
                                || !Model.HasCompletion
                            )
                                throw new InvalidOperationException(
                                    "Editing search did not postpone completion feedback."
                                );
                            Torrents.Focus(FocusState.Programmatic);
                            await CaptureLayout();
                            if (CompletionNotice.Visibility != Visibility.Visible)
                                throw new InvalidOperationException(
                                    "Completion feedback did not return after editing."
                                );
                            await CaptureReady(Model, () => !Model.HasCompletion);
                            outcomes.Add(
                                new
                                {
                                    journey = "completion lifetime",
                                    postponedWhileEditing = true,
                                    resumedAfterEditing = true,
                                    dismissedAfterTimeout = true,
                                }
                            );
                        }
                        Model.DismissCompletion();
                        completed.Add(name);
                    }
                }
                outcomes.Add(
                    new
                    {
                        journey = "completion presentation",
                        scope = "Simulated engine notice through the production UI handler; no download or Explorer launch",
                    }
                );
                return;
            }
            if (ReviewMode == CaptureMode.Schedule)
            {
                await CaptureSchedule(outcomes, completed);
                return;
            }
            if (ReviewMode is CaptureMode.Files or CaptureMode.FilesLayout)
            {
                await CaptureFiles(outcomes, completed);
                return;
            }
            if (ReviewMode == CaptureMode.Library)
            {
                await CaptureLibrary(outcomes, completed);
                return;
            }
            var target =
                Model.Torrents.FirstOrDefault()
                ?? throw new InvalidOperationException("The review store has no torrent.");
            if (ReviewMode == CaptureMode.Footer)
            {
                await CaptureFooter(target, outcomes, completed);
                return;
            }
            if (ReviewMode == CaptureMode.Details)
            {
                await CaptureDetails(target, outcomes);
                return;
            }
            if (ReviewMode == CaptureMode.Edits)
            {
                await CaptureEdits(target, outcomes, completed);
                return;
            }
            if (ReviewMode == CaptureMode.Traffic)
            {
                await CaptureTraffic(target, outcomes, completed);
                return;
            }
            if (ReviewMode == CaptureMode.Search)
            {
                await CaptureSearch(target, outcomes, completed);
                return;
            }
            if (ReviewMode == CaptureMode.Desktop)
            {
                await CaptureDesktop(target, outcomes, completed);
                return;
            }
            if (ReviewMode == CaptureMode.Shell)
            {
                await CaptureShell(target, outcomes);
                return;
            }
            var filesOnly = ReviewMode == CaptureMode.DetailsFiles;
            var addOnly = ReviewMode == CaptureMode.AddLayout;
            if (addOnly)
                await Model.AddDraft.Cancel();
            var settingValues = Model
                .Settings.All.Select(setting => (setting.Name, setting.Input, setting.IsOn))
                .ToArray();
            (int Index, int Priority)[]? priorities = null;
            string[] languages = filesOnly || addOnly ? ["en", "es"] : [Model.Text.Language];
            var themes =
                ReviewMode == CaptureMode.Smoke ? Array.Empty<string>() : ["light", "dark"];
            foreach (var language in languages)
            foreach (var theme in themes)
            {
                await Model.Settings.SelectTheme(theme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                Model.SelectLanguage(language);
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
                foreach (
                    var size in new[]
                    {
                        new SizeInt32(720, 560),
                        new SizeInt32(1040, 680),
                        new SizeInt32(1280, 800),
                    }
                )
                {
                    var prefix =
                        (
                            addOnly ? "add-layout-" + language + "-"
                            : filesOnly ? "details-" + language + "-"
                            : string.Empty
                        )
                        + theme
                        + "-"
                        + size.Width
                        + "x"
                        + size.Height
                        + "-";
                    var scale = Root.XamlRoot.RasterizationScale;
                    var minimum =
                        (
                            (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter
                        ).PreferredMinimumWidth
                        ?? 0;
                    AppWindow.Resize(
                        new SizeInt32(
                            Math.Max((int)(size.Width * scale), minimum),
                            (int)(size.Height * scale)
                        )
                    );
                    await ShowTorrents();
                    if (!filesOnly)
                        Model.CloseInspector();
                    if (addOnly)
                    {
                        await CapturePage(prefix + "header");
                        if (language == "en" && size.Width == 1040)
                        {
                            var states = new[]
                            {
                                (AddButton, "PointerOver"),
                                (MagnetButton, "Pressed"),
                                (ThemeButton, "Disabled"),
                            };
                            try
                            {
                                foreach (var (button, state) in states)
                                    if (!VisualStateManager.GoToState(button, state, false))
                                        throw new InvalidOperationException(
                                            "The caption state is unavailable: " + state
                                        );
                                await CaptureUi(prefix + "caption-states");
                            }
                            finally
                            {
                                foreach (var (button, _) in states)
                                    VisualStateManager.GoToState(
                                        button,
                                        button.IsEnabled ? "Normal" : "Disabled",
                                        false
                                    );
                            }
                        }
                        Torrents.Selection = new Syno.TableView.Selection([target], target);
                        await SelectTorrent();
                        Run(Model.Properties);
                        Model.Inspector.Select(InspectorSection.General);
                        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading);
                        await CapturePage(
                            prefix + "headers",
                            InspectorContent.Content as FrameworkElement
                        );
                        var before = new
                        {
                            height = Torrents.ActualHeight,
                            inspector = InspectorContent
                                .TransformToVisual(Root)
                                .TransformPoint(new Windows.Foundation.Point())
                                .Y,
                            detail = InspectorContent.ActualHeight,
                            footer = StatusBar
                                .TransformToVisual(Root)
                                .TransformPoint(new Windows.Foundation.Point())
                                .Y,
                        };
                        if (Model.HasCommandError)
                            throw new InvalidOperationException(
                                "The layout fixture already has a command failure."
                            );
                        try
                        {
                            Model.Report(
                                new InvalidOperationException(
                                    Model.Text.Get("errors", "invalid_destination")
                                )
                            );
                            await CapturePage(
                                prefix + "command-error",
                                InspectorContent.Content as FrameworkElement
                            );
                            outcomes.Add(
                                new
                                {
                                    journey = prefix + "command-error layout",
                                    scope = "Simulated command failure; no engine operation",
                                    before,
                                    after = new
                                    {
                                        height = Torrents.ActualHeight,
                                        inspector = InspectorContent
                                            .TransformToVisual(Root)
                                            .TransformPoint(new Windows.Foundation.Point())
                                            .Y,
                                        detail = InspectorContent.ActualHeight,
                                        footer = StatusBar
                                            .TransformToVisual(Root)
                                            .TransformPoint(new Windows.Foundation.Point())
                                            .Y,
                                    },
                                }
                            );
                        }
                        finally
                        {
                            Model.ClearError();
                        }
                        await CaptureLayout();
                        var message = Feedback.Message;
                        var severity = Feedback.Severity;
                        var visibility = Feedback.Visibility;
                        var open = Feedback.IsOpen;
                        var action = Feedback.ActionButton.Visibility;
                        var condition = Model.Message;
                        var workspace = new
                        {
                            width = Torrents.ActualWidth,
                            height = Torrents.ActualHeight,
                            footer = StatusBar
                                .TransformToVisual(Root)
                                .TransformPoint(new Windows.Foundation.Point())
                                .Y,
                        };
                        try
                        {
                            Feedback.Message = Model.Text.Get("window", "connecting");
                            Feedback.Severity = InfoBarSeverity.Warning;
                            Feedback.Visibility = Visibility.Visible;
                            Feedback.IsOpen = true;
                            Feedback.ActionButton.Visibility = Visibility.Visible;
                            await CapturePage(prefix + "connection-overlay");
                            outcomes.Add(
                                new
                                {
                                    journey = prefix + "overlay layout",
                                    scope = "Presentation only; the engine remains connected",
                                    before = workspace,
                                    after = new
                                    {
                                        width = Torrents.ActualWidth,
                                        height = Torrents.ActualHeight,
                                        footer = StatusBar
                                            .TransformToVisual(Root)
                                            .TransformPoint(new Windows.Foundation.Point())
                                            .Y,
                                    },
                                    caption = new
                                    {
                                        add = AddButton.ActualWidth,
                                        magnet = MagnetButton.ActualWidth,
                                        theme = ThemeButton.ActualWidth,
                                        inset = AppWindow.TitleBar.RightInset,
                                    },
                                }
                            );
                            Model.Report(
                                new InvalidOperationException(
                                    Model.Text.Get("errors", "invalid_destination")
                                )
                            );
                            Feedback.Message = Model.Text.Get("window", "connecting");
                            Feedback.Severity = InfoBarSeverity.Warning;
                            Feedback.Visibility = Visibility.Visible;
                            Feedback.IsOpen = true;
                            Feedback.ActionButton.Visibility = Visibility.Visible;
                            await CapturePage(prefix + "combined-errors");
                            var error = CaptureElements(Root)
                                .OfType<InfoBar>()
                                .Single(bar => bar.Message == Model.CommandError);
                            CaptureInvoke(
                                CaptureElements(error)
                                    .OfType<Button>()
                                    .Single(button => button.Name == "CloseButton")
                            );
                            await CaptureLayout();
                            if (Model.HasCommandError || Model.Message != condition)
                                throw new InvalidOperationException(
                                    "Dismissing a command error changed connection state or retained the error."
                                );
                            outcomes.Add(
                                new
                                {
                                    journey = prefix + "error dismissal",
                                    scope = "Simulated messages; the engine remains connected",
                                    commandCleared = true,
                                    connectionStateUnchanged = true,
                                }
                            );
                        }
                        finally
                        {
                            Model.ClearError();
                            Feedback.Message = message;
                            Feedback.Severity = severity;
                            Feedback.Visibility = visibility;
                            Feedback.IsOpen = open;
                            Feedback.ActionButton.Visibility = action;
                        }
                        Model.CloseInspector();
                        Model.AddDraft.EditingMagnet = true;
                        var closed = ShowAdd();
                        await CaptureLayout();
                        var dialog =
                            _interaction?.Dialog
                            ?? throw new InvalidOperationException(
                                "The Add dialog did not open: " + Model.CommandError
                            );
                        try
                        {
                            await CapturePage(prefix + "add", dialog.Content as FrameworkElement);
                            var editor = CaptureElements(dialog)
                                .OfType<TextBox>()
                                .Single(control => control.Name == "MagnetInput");
                            var magnet =
                                "magnet:?xt=urn:btih:"
                                + target.Hashes[0]
                                + "&dn=Example%20download"
                                + string.Concat(
                                    Enumerable
                                        .Range(1, 12)
                                        .Select(index =>
                                            "&tr=https%3A%2F%2Ftracker"
                                            + index
                                            + ".example.invalid%2Fannounce"
                                        )
                                );
                            editor.Text = magnet;
                            await CapturePage(
                                prefix + "long-magnet",
                                dialog.Content as FrameworkElement
                            );
                            if (Model.AddDraft.Magnet != magnet)
                                throw new InvalidOperationException(
                                    "The wrapped magnet editor changed its input."
                                );
                            editor.Text = "invalid magnet";
                            var preview = CaptureElements(dialog)
                                .OfType<Button>()
                                .Single(control => control.Name == "Preview");
                            var previewTop = preview
                                .TransformToVisual(dialog)
                                .TransformPoint(new Windows.Foundation.Point())
                                .Y;
                            CaptureInvoke(preview);
                            await CaptureReady(
                                Model.AddDraft,
                                () => Model.AddDraft.HasMagnetError && !Model.AddDraft.IsPending
                            );
                            if (
                                editor.Text != "invalid magnet"
                                || Model.AddDraft.Magnet != "invalid magnet"
                            )
                                throw new InvalidOperationException(
                                    "The magnet editor lost its rejected input."
                                );
                            await CapturePage(
                                prefix + "magnet-error",
                                dialog.Content as FrameworkElement
                            );
                            outcomes.Add(
                                new
                                {
                                    journey = prefix + "magnet validation",
                                    before = previewTop,
                                    after = preview
                                        .TransformToVisual(dialog)
                                        .TransformPoint(new Windows.Foundation.Point())
                                        .Y,
                                    rejectedInputRetained = true,
                                }
                            );
                            if (language == "es" && theme == "dark" && size.Width == 720)
                            {
                                editor.Text = "magnet:?xt=urn:btih:" + target.Hashes[0];
                                CaptureInvoke(preview);
                                await CaptureReady(
                                    Model.AddDraft,
                                    () => Model.AddDraft.HasSources && !Model.AddDraft.IsPending
                                );
                                await CapturePage(
                                    prefix + "magnet-preview",
                                    dialog.Content as FrameworkElement
                                );
                                outcomes.Add(
                                    new
                                    {
                                        journey = "magnet preview",
                                        rejectedInputRetained = true,
                                        previewVisible = Model.AddDraft.HasSources,
                                    }
                                );
                            }
                            await Model.AddDraft.Cancel();
                            Model.AddDraft.EditingMagnet = true;
                            Model.AddDraft.Paused = true;
                            var hash = Guid.NewGuid().ToString("N") + "01234567";
                            editor.Text = "magnet:?xt=urn:btih:" + hash;
                            await CaptureLayout();
                            var destination = CaptureElements(dialog)
                                .OfType<ComboBox>()
                                .Single(control => control.Name == "Destination");
                            var folder = CaptureElements(destination)
                                .OfType<TextBox>()
                                .Single(control => control.Name == "EditableText");
                            var original = Model.AddDraft.Destination;
                            folder.Focus(FocusState.Programmatic);
                            destination.Text = "relative-download-folder";
                            await CaptureLayout();
                            if (Model.AddDraft.Destination != "relative-download-folder")
                                throw new InvalidOperationException(
                                    $"The folder edit did not reach the draft: editor={folder.Text}, control={destination.Text}, draft={Model.AddDraft.Destination}."
                                );
                            CaptureInvoke(dialog);
                            await CaptureReady(
                                Model.AddDraft,
                                () =>
                                    !Model.AddDraft.IsPending && Model.AddDraft.Failure is not null
                            );
                            if (
                                Model.AddDraft.Failure
                                is not CommandException
                                {
                                    Command: "add",
                                    Code: "invalid_destination"
                                }
                            )
                                throw new InvalidOperationException(
                                    "The destination fixture failed before its Add command."
                                );
                            await CaptureLayout();
                            if (
                                FocusManager.GetFocusedElement(Root.XamlRoot)
                                    is not DependencyObject focused
                                || !CaptureElements(destination).Contains(focused)
                            )
                                throw new InvalidOperationException(
                                    "A destination refusal did not focus the folder editor."
                                );
                            await CapturePage(
                                prefix + "destination-error",
                                dialog.Content as FrameworkElement
                            );
                            outcomes.Add(
                                new
                                {
                                    journey = prefix + "destination refusal",
                                    message = Model.AddDraft.Failure.Message,
                                    focus = (
                                        FocusManager.GetFocusedElement(Root.XamlRoot)
                                        as FrameworkElement
                                    )?.Name,
                                    folderRetained = folder.Text == "relative-download-folder",
                                    sources = Model.AddDraft.Sources.Count,
                                }
                            );
                            destination.Text = original;
                            await CaptureReady(
                                Model.AddDraft,
                                () => Model.AddDraft.CanSubmit && Model.AddDraft.Failure is null
                            );
                            await CapturePage(
                                prefix + "destination-corrected",
                                dialog.Content as FrameworkElement
                            );
                            if (language == "en" && theme == "light" && size.Width == 720)
                            {
                                CaptureInvoke(dialog);
                                await closed.WaitAsync(TimeSpan.FromSeconds(20));
                                await CaptureReady(
                                    Model,
                                    () =>
                                        Model.Torrents.Any(torrent => torrent.Hashes.Contains(hash))
                                );
                                outcomes.Add(
                                    new
                                    {
                                        journey = "destination correction retry",
                                        accepted = true,
                                    }
                                );
                            }
                        }
                        finally
                        {
                            dialog.Hide();
                            await closed;
                        }
                        completed.Add(prefix);
                        continue;
                    }
                    if (!filesOnly)
                    {
                        await CapturePage(prefix + "torrents");
                        Model.IsFilterOpen = true;
                        await CapturePage(prefix + "filters");
                        Model.IsFilterOpen = false;
                    }
                    Torrents.Selection = new Syno.TableView.Selection([target], target);
                    await SelectTorrent();
                    Run(Model.Properties);
                    foreach (
                        var section in filesOnly
                            ? new[] { InspectorSection.Files }
                            : Enum.GetValues<InspectorSection>()
                    )
                    {
                        Model.Inspector.Select(section);
                        await CaptureLayout();
                        await CaptureReady(
                            Model.Inspector,
                            () =>
                                !Model.Inspector.IsLoading
                                && (!filesOnly || Model.Inspector.HasFiles)
                        );
                        if (filesOnly)
                        {
                            priorities ??= Model
                                .Inspector.Files.Roots.SelectMany(root => root.Nodes())
                                .Where(file => file.Index >= 0)
                                .Select(file => (file.Index, file.Priority))
                                .OrderBy(file => file.Index)
                                .ToArray();
                        }
                        await CapturePage(
                            prefix + "properties-" + section,
                            InspectorContent.Content as FrameworkElement
                        );
                        if (filesOnly && section == InspectorSection.Files && size.Width == 720)
                        {
                            var priority = CaptureElements(InspectorContent)
                                .OfType<ComboBox>()
                                .First();
                            priority.IsDropDownOpen = true;
                            try
                            {
                                await CapturePage(prefix + "priority-choices");
                            }
                            finally
                            {
                                priority.IsDropDownOpen = false;
                            }
                        }
                    }
                    if (!filesOnly)
                        Model.CloseInspector();
                    foreach (
                        var category in filesOnly
                            ? new[] { SettingsCategory.Appearance, SettingsCategory.Network }
                            : Enum.GetValues<SettingsCategory>()
                    )
                    {
                        await ShowSettings(new(category));
                        await CapturePage(prefix + "settings-" + category, _settingsPage);
                        if (
                            !filesOnly
                            && category == SettingsCategory.Limits
                            && Model.FollowsSchedule
                            && Model.Settings.Schedule.Periods.FirstOrDefault() is { } period
                        )
                        {
                            await Model.Settings.Schedule.Open(period);
                            await CaptureLayout();
                            await CaptureUi(prefix + "period-editor");
                            await Model.Settings.Schedule.Close();
                        }
                    }
                    if (filesOnly)
                    {
                        completed.Add(prefix);
                        continue;
                    }
                    await ShowAbout();
                    await CapturePage(prefix + "about");
                    await ShowTorrents();
                    await CaptureDialog(
                        prefix + "remove",
                        () => ConfirmRemove([target]),
                        () => _interaction?.Dialog
                    );
                    await CaptureDialog(
                        prefix + "move",
                        () => ShowFiles([target], FileAction.Move),
                        () => _interaction?.Dialog
                    );
                    await CaptureDialog(
                        prefix + "delete",
                        () => ShowFiles([target], FileAction.Delete),
                        () => _interaction?.Dialog
                    );
                    Model.AddDraft.EditingMagnet = true;
                    await CaptureDialog(prefix + "add", ShowAdd, () => _interaction?.Dialog);
                    completed.Add(prefix);
                }
            }
            if (addOnly)
                return;
            if (filesOnly)
            {
                await Model.Settings.SelectTheme("light");
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
                Model.SelectLanguage("en");
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "en");
                var scale = Root.XamlRoot.RasterizationScale;
                var minimum =
                    (
                        (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter
                    ).PreferredMinimumWidth
                    ?? 0;
                AppWindow.Resize(
                    new SizeInt32(Math.Max((int)(720 * scale), minimum), (int)(560 * scale))
                );
                await ShowTorrents();
                await CapturePage(
                    "details-en-light-720x560-reverse-properties-Files",
                    InspectorContent.Content as FrameworkElement
                );
                foreach (
                    var category in new[] { SettingsCategory.Appearance, SettingsCategory.Network }
                )
                {
                    await ShowSettings(new(category));
                    await CapturePage(
                        "details-en-light-720x560-reverse-settings-" + category,
                        _settingsPage
                    );
                }
                var originalTheme = settingValues
                    .Single(setting => setting.Name == Model.Settings.Theme.Name)
                    .Input;
                var originalLanguage = settingValues
                    .Single(setting => setting.Name == Model.Settings.Language.Name)
                    .Input;
                await Model.Settings.SelectTheme(originalTheme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == originalTheme);
                Model.SelectLanguage(originalLanguage);
                await CaptureReady(
                    Model,
                    () => Model.CanClose && Model.Text.Language == originalLanguage
                );
                var prioritiesRetained =
                    priorities is not null
                    && priorities.SequenceEqual(
                        Model
                            .Inspector.Files.Roots.SelectMany(root => root.Nodes())
                            .Where(file => file.Index >= 0)
                            .Select(file => (file.Index, file.Priority))
                            .OrderBy(file => file.Index)
                    );
                var settingsRetained = settingValues.SequenceEqual(
                    Model.Settings.All.Select(setting =>
                        (setting.Name, setting.Input, setting.IsOn)
                    )
                );
                var fileChanges = Model.Inspector.HasFileChanges;
                var settingDraft = Model.Settings.Schedule.HasDraft;
                var pending = Model.Settings.IsPending;
                outcomes.Add(
                    new
                    {
                        journey = "live language selected choices",
                        languages = new[] { "en", "es", "en" },
                        prioritiesRetained,
                        settingsRetained,
                        fileChanges,
                        settingDraft,
                        pending,
                    }
                );
                if (
                    !prioritiesRetained
                    || !settingsRetained
                    || fileChanges
                    || settingDraft
                    || pending
                )
                    throw new InvalidOperationException(
                        "Changing the capture language altered file priorities or settings."
                    );
                return;
            }
            if (completed.Count > 0)
            {
                Model.SelectLanguage("es");
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "es");
                await ShowSettings(new(SettingsCategory.Limits));
                await CapturePage("spanish-limits", _settingsPage);
                Model.SelectLanguage("en");
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "en");
            }
            AppWindow.Resize(new SizeInt32(1040, 680));
            await ShowTorrents();
            await CaptureSmoke(target, outcomes);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            if (_captureDirectory is not null)
            {
                Directory.CreateDirectory(_captureDirectory);
                await File.WriteAllTextAsync(
                    Path.Combine(_captureDirectory, "review.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            completed,
                            outcomes,
                            milliseconds = clock.ElapsedMilliseconds,
                            failure = failure?.ToString(),
                            scope = "Real XAML views and bounded review journeys in a disposable store; no desktop input or capture",
                        }
                    )
                );
            }
            await Model.CancelDraft();
            await CloseWindow(exiting: false);
        }
    }
}
