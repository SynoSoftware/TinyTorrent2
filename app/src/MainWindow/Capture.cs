using System.Diagnostics;
using System.ComponentModel;
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
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using Windows.Graphics;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private string? _captureDirectory;
    private Task? _capture;
    private static readonly CaptureMode ReviewMode = Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") switch
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
        "preferences-layout" => CaptureMode.PreferencesLayout,
        _ => CaptureMode.None
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
        if (string.IsNullOrWhiteSpace(directory)) return;
        if (!Path.IsPathFullyQualified(directory))
        {
            Debug.WriteLine("TINYTORRENT_CAPTURE_DIRECTORY requires an absolute path.");
            return;
        }
        _captureDirectory = directory;
        if (IsCaptureReview)
        {
            Root.Loaded += (_, _) => { if (_capture is null) _capture = CaptureReview(); };
            return;
        }
        var shortcut = new KeyboardAccelerator { Key = VirtualKey.F12, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
        shortcut.Invoked += (_, args) =>
        {
            if (_capture is not { IsCompleted: false }) _capture = CaptureUi();
            args.Handled = true;
        };
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Root.KeyboardAccelerators.Add(shortcut);
    }

    private async Task CaptureUi(string? name = null)
    {
        if (_captureDirectory is null || Root.XamlRoot is null) return;
        var clock = Stopwatch.StartNew();
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
            var directory = Path.Combine(_captureDirectory, name is null ? stamp : name + "-" + stamp);
            Directory.CreateDirectory(directory);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var scale = Root.XamlRoot.RasterizationScale;
            var scene = new List<FrameworkElement> { Root };
            scene.AddRange(VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                .Select(popup => popup.Child).OfType<FrameworkElement>());
            var frames = new List<object>();
            var controls = new List<object>();
            foreach (var element in scene)
            {
                if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
                var bitmap = new RenderTargetBitmap();
                byte[]? previous = null;
                byte[] pixels;
                var stable = false;
                var rendering = Stopwatch.StartNew();
                do
                {
                    await bitmap.RenderAsync(element, (int)Math.Ceiling(element.ActualWidth * scale),
                        (int)Math.Ceiling(element.ActualHeight * scale));
                    pixels = (await bitmap.GetPixelsAsync()).ToArray();
                    stable = previous is not null && pixels.AsSpan().SequenceEqual(previous);
                    previous = pixels;
                } while (!stable && rendering.ElapsedMilliseconds < 3000);
                var frame = frames.Count == 0 ? "window.png" : $"popup-{frames.Count}.png";
                var file = await folder.CreateFileAsync(frame, CreationCollisionOption.FailIfExists);
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels);
                await encoder.FlushAsync();
                frames.Add(new { file = frame, width = bitmap.PixelWidth, height = bitmap.PixelHeight, stable });
                foreach (var control in CaptureElements(element).Where(control => control.IsLoaded && control.ActualWidth > 0 && control.ActualHeight > 0))
                {
                    var point = control.TransformToVisual(element).TransformPoint(new Windows.Foundation.Point());
                    controls.Add(new { frame, type = control.GetType().Name, name = control.Name,
                        accessibleName = AutomationProperties.GetName(control),
                        text = control is TextBlock label ? label.Text : control is TextBox input ? input.Text : null,
                        x = point.X, y = point.Y, width = control.ActualWidth, height = control.ActualHeight,
                        enabled = control is not Control command || command.IsEnabled });
                }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "capture.json"), JsonSerializer.Serialize(new
            {
                scope = "XAML visuals; native chrome, system dialogs and desktop acrylic are not captured",
                page = Model.Page.ToString(), language = Model.Text.Language, theme = Root.ActualTheme.ToString(),
                scale, milliseconds = clock.ElapsedMilliseconds, frames, controls
            }));
            Debug.WriteLine($"UI capture saved to {directory} in {clock.ElapsedMilliseconds} ms.");
        }
        catch (Exception error)
        {
            Debug.WriteLine($"UI capture did not complete: {error.Message}");
            if (IsCaptureReview) throw;
        }
    }

    private static IEnumerable<FrameworkElement> CaptureElements(DependencyObject parent)
    {
        if (parent is UIElement { Visibility: Visibility.Collapsed }) yield break;
        if (parent is FrameworkElement element) yield return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in CaptureElements(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private async Task CaptureLayout()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { Root.UpdateLayout(); ready.TrySetResult(); }))
            throw new InvalidOperationException("The capture dispatcher is closed.");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task CaptureReady(INotifyPropertyChanged owner, Func<bool> ready)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args) { if (ready()) completion.TrySetResult(); }
        owner.PropertyChanged += Changed;
        try
        {
            if (ready()) completion.TrySetResult();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { owner.PropertyChanged -= Changed; }
    }

    private async Task CapturePage(string name, FrameworkElement? content = null)
    {
        await CaptureLayout();
        var scroll = content is null ? null : CaptureElements(content).OfType<ScrollViewer>()
            .Where(view => view.ScrollableHeight > 0).MaxBy(view => view.ScrollableHeight);
        scroll?.ChangeView(null, 0, null, true);
        await CaptureLayout();
        await CaptureUi(name);
        if (scroll is not { ScrollableHeight: > 0 }) return;
        scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
        await CaptureLayout();
        await CaptureUi(name + "-bottom");
    }

    private async Task CaptureDialog(string name, Func<Task> open, Func<ContentDialog?> current)
    {
        var closed = open();
        await CaptureLayout();
        var dialog = current() ?? throw new InvalidOperationException("The review dialog did not open.");
        try
        {
            if (_filesForm is not null) await CaptureReady(Model.Files, () => !Model.Files.IsPending);
            await CapturePage(name, dialog.Content as FrameworkElement);
        }
        finally { dialog.Hide(); await closed; }
    }

    private static void CaptureInvoke(ContentDialog dialog)
    {
        var button = CaptureElements(dialog).OfType<Button>().First(control => control.Name == "PrimaryButton");
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
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.Pieces is not null);
        await CapturePage("pieces", InspectorContent.Content as FrameworkElement);
        Model.CloseInspector();
        Run(Model.Limits);
        await CaptureLayout();
        await CapturePage("speed-limits-settings", _preferencesForm);
        outcomes.Add(new { journey = "speed limits settings", page = Model.Page.ToString(),
            scope = "Canonical Transfers settings; the obsolete modal Apply/invalid-input scenario is removed." });
        await ShowTorrents();

        Model.Draft.EditingMagnet = true;
        var closed = ShowAdd();
        await CaptureLayout();
        var dialog = _interaction?.Dialog ?? throw new InvalidOperationException("The Add dialog did not open.");
        try
        {
            Model.Draft.Magnet = "invalid magnet";
            await Model.Draft.PrepareMagnet();
            if (!Model.Draft.HasMagnetError || Model.Draft.Magnet != "invalid magnet")
                throw new InvalidOperationException("The magnet editor lost the rejected input.");
            outcomes.Add(new { journey = "invalid magnet", rejectedInputRetained = true });
            await CapturePage("invalid-magnet", dialog.Content as FrameworkElement);
        }
        finally { dialog.Hide(); await closed; }

        await Model.Select([target], target);
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.Trackers);
        await CaptureLayout();
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.EditTrackers.CanExecute(null));
        Run(Model.Inspector.EditTrackers);
        Model.Inspector.TrackerInput = "https://example.invalid/announce";
        closed = ConfirmRemove([target]);
        await CaptureLayout();
        dialog = _interaction?.Dialog ?? throw new InvalidOperationException("The removal dialog did not open.");
        CaptureInvoke(dialog);
        await closed;
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsAvailable);
        if (!Model.Inspector.HasDraft || !Model.Inspector.HasError || Model.Inspector.SaveTrackers.CanExecute(null) ||
                !Model.Inspector.CancelTrackers.CanExecute(null))
            throw new InvalidOperationException("The unavailable properties draft has no safe recovery.");
        outcomes.Add(new { journey = "removed torrent with draft", draftKept = true, saveDisabled = true, cancelEnabled = true });
        await CapturePage("removed-properties", InspectorContent.Content as FrameworkElement);
        Run(Model.Inspector.CancelTrackers);
        Model.CloseInspector();
        await CapturePage("empty-torrents");
    }

    private async Task CaptureShell(Torrent target, List<object> outcomes)
    {
        foreach (var theme in new[] { "light", "dark" })
        {
            await Model.Preferences.SelectTheme(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            foreach (var width in new[] { 1040, 720 })
            {
                var scale = Root.XamlRoot.RasterizationScale;
                var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                AppWindow.Resize(new SizeInt32(Math.Max((int)(width * scale), minimum), (int)(680 * scale)));
                await CaptureLayout();
                CaptureHitRegions(outcomes);
                await CapturePage(theme + "-" + width + "-shell");
                foreach (var menu in new[] { FileMenu, TorrentMenu, ViewMenu, HelpMenu })
                {
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(menu);
                    if (peer.GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
                        throw new InvalidOperationException("The menu does not expose native expansion.");
                    expand.Expand();
                    await CapturePage(theme + "-" + width + "-" + menu.Name);
                    expand.Collapse();
                }
            }
        }
        Torrents.Selection = new Syno.TableView.Selection([], null);
        await SelectTorrent();
        await CaptureLayout();
        outcomes.Add(new { command = "pause-empty", enabled = TorrentMenu.Items.OfType<MenuFlyoutItem>()
            .Single(item => item.Command == Model.Pause).IsEnabled });
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        await CaptureLayout();
        foreach (var command in new[] { Model.Resume, Model.Pause })
        {
            var menuPeer = FrameworkElementAutomationPeer.CreatePeerForElement(TorrentMenu);
            var expand = (IExpandCollapseProvider)menuPeer.GetPattern(PatternInterface.ExpandCollapse);
            expand.Expand();
            await CaptureLayout();
            var item = TorrentMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Command == command);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                throw new InvalidOperationException("The torrent command cannot be invoked.");
            var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = Model.Text.Format("outcomes", "accepted", Model.Text.Get("commands", command == Model.Pause ? "pause" : "resume"));
            void Announced(object? sender, string message)
            {
                if (message == accepted) acknowledged.TrySetResult();
            }
            Model.AnnouncementRequested += Announced;
            try
            {
                invoke.Invoke();
                await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally { Model.AnnouncementRequested -= Announced; }
            if (Model.HasCommandError) throw new InvalidOperationException("The native torrent command failed.");
            var directory = Model.DataDirectory ?? throw new InvalidOperationException("The shell capture has no store.");
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")));
            var paused = saved.RootElement.GetProperty("torrents").EnumerateArray()
                .Single(torrent => torrent.GetProperty("torrent_id").GetString() == target.TorrentId).GetProperty("paused").GetBoolean();
            var allPaused = saved.RootElement.GetProperty("settings").GetProperty("all_paused").GetBoolean();
            outcomes.Add(new { command = item.Text, enabled = item.IsEnabled, persistedPaused = paused, status = target.StatusCode, globalPaused = allPaused });
            if (paused != (command == Model.Pause) || !allPaused || !Model.AllPaused)
                throw new InvalidOperationException("The native torrent command did not save its pause choice while retaining global pause.");
            expand.Collapse();
        }
        Run(Model.ShowPreferences);
        await CaptureLayout();
        outcomes.Add(new { command = "settings", page = Model.Page.ToString(), torrentMenu = TorrentMenu.IsEnabled });
        await CapturePage("shell-settings", _preferencesForm);
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
        outcomes.Add(new { command = "close-filters", open = Workspace.IsPaneOpen, filter = Model.Filter.ToString() });
        Search.Text = target.Name;
        await CaptureLayout();
        outcomes.Add(new { command = "search", matches = Model.FindSuggestions(Search.Text).Count });
        await CapturePage("shell-search");
        Search.Text = string.Empty;
        Model.SelectLanguage("es");
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "es");
        await CaptureLayout();
        var narrow = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 720;
        AppWindow.Resize(new SizeInt32(narrow, 560));
        await CaptureLayout();
        CaptureHitRegions(outcomes);
        await CapturePage("shell-spanish");
        await CaptureDialog("shell-add", () => { Run(Model.AddMagnet); return _interaction?.Completion.Task ?? Task.CompletedTask; }, () => _interaction?.Dialog);
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
            var point = new PointInt32((int)Math.Round(position.X * scale), (int)Math.Round(position.Y * scale));
            if (!ClientToScreen(hwnd, ref point))
                throw new InvalidOperationException("The review window's client coordinates could not be converted to screen coordinates.");
            var coordinates = unchecked((int)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16)));
            var actual = (int)SendMessageW(hwnd, WM_NCHITTEST, 0, coordinates);
            hits.Add(new { name, screenX = point.X, screenY = point.Y, actual, expected });
            failed |= actual != expected;
        }

        foreach (var control in new FrameworkElement[] { AppIcon, FileMenu, TorrentMenu, ViewMenu, HelpMenu, Search, AddButton, MagnetButton, ThemeButton })
        {
            var center = control.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(control.ActualWidth / 2, control.ActualHeight / 2));
            Hit(control.Name, center, control == AppIcon ? HTSYSMENU : HTCLIENT);
        }
        var menu = Menus.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, Menus.ActualWidth, Menus.ActualHeight));
        var search = Search.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, Search.ActualWidth, Search.ActualHeight));
        if (search.Left <= menu.Right) throw new InvalidOperationException("The review caption has no unused gap between its menu and search.");
        Hit("gap", new Windows.Foundation.Point((menu.Right + search.Left) / 2, (menu.Top + menu.Bottom) / 2), HTCAPTION);
        outcomes.Add(new { journey = "caption hit regions", language = Model.Text.Language, theme = Root.ActualTheme.ToString(), width = AppWindow.ClientSize.Width, hits });
        if (failed) throw new InvalidOperationException("The review window returned an unexpected native caption hit classification.");
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref PointInt32 point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    private async Task CaptureSchedule(List<object> outcomes, List<string> completed)
    {
        var preferences = Model.Preferences.Schedule;
        var existing = preferences.Periods.ToArray();
        SchedulePeriod? created = null;
        await ShowPreferences(new(PreferenceSection.Schedule));
        await CaptureLayout();
        var form = _preferencesForm ?? throw new InvalidOperationException("The review preferences did not open.");
        Button FindButton(string id) => CaptureElements(form).OfType<Button>()
            .Single(control => AutomationProperties.GetAutomationId(control) == id);
        TimePicker FindTime(string id) => CaptureElements(form).OfType<TimePicker>()
            .Single(control => AutomationProperties.GetAutomationId(control) == id);
        try
        {
            CaptureInvoke(FindButton("AddPeriod"));
            await CaptureLayout();
            var draft = preferences.Draft ?? throw new InvalidOperationException("Add period did not open the editor.");
            FindTime("PeriodStart").SelectedTime = TimeSpan.FromMinutes(1337);
            FindTime("PeriodEnd").SelectedTime = TimeSpan.FromMinutes(103);
            foreach (var day in CaptureElements(form).OfType<CheckBox>())
                if (AutomationProperties.GetAutomationId(day).StartsWith("PeriodDay", StringComparison.Ordinal)) day.IsChecked = false;
            await CaptureLayout();
            if (draft.Start?.TotalMinutes != 1337 || draft.End?.TotalMinutes != 103 || draft.Days.Any(day => day.IsChecked))
                throw new InvalidOperationException("The native period fields did not update their draft.");
            CaptureInvoke(FindButton("SavePeriod"));
            await CaptureReady(preferences, () => preferences.HasScheduleError);
            if (preferences.Draft != draft || draft.Start?.TotalMinutes != 1337 || draft.End?.TotalMinutes != 103 || preferences.Periods.Count != existing.Length)
                throw new InvalidOperationException("An empty-day save changed the saved schedule or lost its draft.");
            outcomes.Add(new { journey = "empty schedule days", rejected = true, draftRetained = true, exactMinutesRetained = true });
            await CapturePage("schedule-invalid-period", form);
            CaptureElements(form).OfType<CheckBox>().Single(control => AutomationProperties.GetAutomationId(control) == "PeriodDay0").IsChecked = true;
            await CaptureLayout();
            CaptureInvoke(FindButton("SavePeriod"));
            await CaptureReady(preferences, () => !preferences.IsPending && !preferences.IsEditing);
            created = preferences.Periods.Single(period => period.Days.SequenceEqual(new[] { 0 }) && period.Start == 1337 && period.End == 103 && period.Mode == ScheduleMode.Alternative);
            if (created.Span.Duration != 206 || created.Occurrences(0).Single().End != 1543 || created.Occurrences(1).Single().End != 103 ||
                preferences.Ranges(1).First(range => range.Start <= 60 && range.End > 60).Mode != ScheduleMode.Paused)
                throw new InvalidOperationException("The overnight period lost exact minutes or pause precedence.");
            outcomes.Add(new { journey = "save overnight period", start = created.Start, end = created.End, duration = created.Span.Duration, nextDayOccurrence = true, pausePrecedence = true });

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
                        var scale = Root.XamlRoot.RasterizationScale;
                        var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                        AppWindow.Resize(new SizeInt32(Math.Max((int)(size.Width * scale), minimum), (int)(size.Height * scale)));
                        var prefix = "schedule-" + language + "-" + theme + "-" + size.Width + "x" + size.Height;
                        preferences.Select(null);
                        await CapturePage(prefix + "-overview", form);
                        preferences.Select(created);
                        await CapturePage(prefix + "-selected", form);
                        CaptureInvoke(FindButton("EditPeriod"));
                        await CaptureLayout();
                        await CapturePage(prefix + "-editor", form);
                        foreach (var day in CaptureElements(form).OfType<CheckBox>())
                            if (AutomationProperties.GetAutomationId(day).StartsWith("PeriodDay", StringComparison.Ordinal)) day.IsChecked = false;
                        await CaptureLayout();
                        CaptureInvoke(FindButton("SavePeriod"));
                        await CaptureReady(preferences, () => preferences.HasScheduleError);
                        await CaptureLayout();
                        await CaptureUi(prefix + "-validation");
                        CaptureInvoke(FindButton("CancelPeriod"));
                        await CaptureReady(preferences, () => !preferences.IsEditing);
                        completed.Add(prefix);
                    }
                }
            }

            CaptureInvoke(FindButton("EditPeriod"));
            await CaptureLayout();
            FindTime("PeriodStart").SelectedTime = TimeSpan.FromMinutes(1273);
            await CaptureLayout();
            if (preferences.Draft?.Start?.TotalMinutes != 1273)
                throw new InvalidOperationException("The edited native time did not reach the draft.");
            CaptureInvoke(FindButton("CancelPeriod"));
            await CaptureReady(preferences, () => !preferences.IsEditing);
            if (!preferences.Periods.Contains(created) || created.Start != 1337 || created.End != 103)
                throw new InvalidOperationException("Cancel changed the saved period.");
            outcomes.Add(new { journey = "cancel period edit", savedPeriodRetained = true });

            await preferences.Reschedule(created, created.Span.Adjust(PeriodAction.Move, 15));
            created = preferences.Selection ?? throw new InvalidOperationException("The moved period was not selected.");
            if (preferences.HasScheduleError || created.Start != 1350 || created.End != 116 || created.Span.Duration != 206)
                throw new InvalidOperationException("Moving the period did not preserve its duration and snap its start.");
            outcomes.Add(new { journey = "move period", start = created.Start, end = created.End, duration = created.Span.Duration });
            await CapturePage("schedule-moved-period", form);
            CaptureInvoke(FindButton("RemovePeriod"));
            await CaptureReady(preferences, () => !preferences.IsPending && preferences.Periods.Count == existing.Length);
            if (!existing.All(period => preferences.Periods.Any(candidate => candidate.Matches(period))))
                throw new InvalidOperationException("Removing the review period changed an existing period.");
            created = null;
            outcomes.Add(new { journey = "remove review period", existingPeriodsRetained = true });
        }
        finally
        {
            if (preferences.IsEditing) Run(preferences.CancelPeriod);
            if (created is { } saved && preferences.Periods.FirstOrDefault(period => period.Matches(saved)) is { } remaining)
                await preferences.Remove(remaining);
        }
    }

    private async Task CaptureDesktop(Torrent target, List<object> outcomes, List<string> completed)
    {
        const string magnet = "magnet:?xt=urn:btih:";
        await ShowTorrents();
        Run(Model.AddMagnet);
        await CaptureLayout();
        var dialog = _interaction?.Dialog ?? throw new InvalidOperationException("The desktop review Add dialog did not open.");
        var input = CaptureElements(dialog).OfType<TextBox>().Single(control => control.Name == "MagnetInput");
        input.Focus(FocusState.Programmatic);
        input.Text = magnet;
        await CaptureLayout();
        if (Model.Draft.Magnet != magnet || !Model.Draft.HasChanges)
            throw new InvalidOperationException("The unfinished native magnet input did not reach its draft.");
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
                    var scale = Root.XamlRoot.RasterizationScale;
                    var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                    AppWindow.Resize(new SizeInt32(Math.Max((int)(size.Width * scale), minimum), (int)(size.Height * scale)));
                    var prefix = "desktop-" + language + "-" + theme + "-" + size.Width + "x" + size.Height;
                    Run(Model.Exit);
                    await CaptureReady(Model, () => _interaction is { IsDraftDecision: true, Dialog: not null });
                    await CapturePage(prefix + "-exit-prompt");
                    var prompt = _interaction?.Dialog ?? throw new InvalidOperationException("Exit did not retain its draft prompt.");
                    CaptureInvoke(CaptureElements(prompt).OfType<Button>().Single(control => control.Name == "CloseButton"));
                    await CaptureReady(Model, () => !Model.IsClosing && Model.CanEdit && Model.IsAddOpen &&
                        _interaction is { IsDraftDecision: false, Dialog: not null });
                    await CaptureLayout();
                    dialog = _interaction?.Dialog ?? throw new InvalidOperationException("Keep input did not recover the Add form.");
                    input = CaptureElements(dialog).OfType<TextBox>().Single(control => control.Name == "MagnetInput");
                    if (input.Text != magnet || Model.Draft.Magnet != magnet || !Model.Draft.HasChanges)
                        throw new InvalidOperationException("Keep input lost the unfinished magnet.");
                    completed.Add(prefix);
                }
            }
        }
        outcomes.Add(new { journey = "Exit with unfinished magnet", prompts = 12, inputRetained = true, addFormRecovered = true, engineConnected = Model.IsConnected });
        await CapturePage("desktop-kept-magnet", dialog.Content as FrameworkElement);
        input.Focus(FocusState.Programmatic);
        Run(Model.Exit);
        await CaptureReady(Model, () => _interaction is { IsDraftDecision: true, Dialog: not null });
        await CapturePage("desktop-exit-save-invalid");
        CaptureInvoke(_interaction?.Dialog ?? throw new InvalidOperationException("Exit did not retain its Save prompt."));
        await CaptureReady(Model, () => !Model.IsClosing && Model.CanEdit && Model.IsAddOpen &&
            _interaction is { IsDraftDecision: false, Dialog: not null } && Model.Draft.HasMagnetError);
        await CaptureLayout();
        dialog = _interaction?.Dialog ?? throw new InvalidOperationException("Failed Save did not recover the Add form.");
        input = CaptureElements(dialog).OfType<TextBox>().Single(control => control.Name == "MagnetInput");
        if (input.Text != magnet || Model.Draft.Magnet != magnet || !Model.Draft.HasChanges ||
            !ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), input))
            throw new InvalidOperationException("Failed Save lost the unfinished magnet or its editor focus.");
        await CapturePage("desktop-exit-save-failed", dialog.Content as FrameworkElement);
        outcomes.Add(new { journey = "Exit Save with invalid magnet", errorRetained = true, inputRetained = true,
            addFormRecovered = true, focusRetained = true, engineConnected = Model.IsConnected });
        var closed = _interaction?.Completion.Task ?? throw new InvalidOperationException("The recovered Add dialog has no close completion.");
        CaptureInvoke(CaptureElements(dialog).OfType<Button>().Single(control => control.Name == "CloseButton"));
        await closed;
        await CaptureReady(Model.Draft, () => !Model.Draft.HasChanges && !Model.Draft.EditingMagnet);
        outcomes.Add(new { journey = "cancel recovered Add", draftCleared = true });

        Model.SelectLanguage("es");
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "es");
        await Model.Preferences.SelectTheme("light");
        await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
        var raster = Root.XamlRoot.RasterizationScale;
        var minimumWidth = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
        AppWindow.Resize(new SizeInt32(Math.Max((int)(1040 * raster), minimumWidth), (int)(680 * raster)));
        var membership = Model.Torrents.Select(torrent => torrent.TorrentId).Order().ToArray();
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.Trackers);
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.EditTrackers.CanExecute(null));
        await CaptureLayout();
        CaptureInvoke(CaptureElements(InspectorContent).OfType<Button>().Single(control => control.Name == "EditTrackers"));
        await CaptureLayout();
        var editor = CaptureElements(InspectorContent).OfType<TextBox>().Single(control => control.Name == "TrackerInput");
        const string trackerInput = "https://example.invalid/unfinished";
        editor.Text = trackerInput;
        await CaptureLayout();
        if (!Model.Inspector.HasDraft || Model.Inspector.TrackerInput != trackerInput)
            throw new InvalidOperationException("The native tracker editor did not retain its unfinished input.");
        await CapturePage("desktop-before-restart", InspectorContent.Content as FrameworkElement);
        var signal = Path.Combine(_captureDirectory ?? throw new InvalidOperationException("The desktop review has no capture directory."), "restart.txt");
        await File.WriteAllTextAsync(signal, "restart");
        await CaptureReady(Model, () => !Model.IsConnected);
        if (!Model.Inspector.HasDraft || Model.Inspector.TrackerInput != trackerInput || Model.Inspector.SaveTrackers.CanExecute(null) || !Model.Inspector.CancelTrackers.CanExecute(null))
            throw new InvalidOperationException("Disconnect did not preserve a safely cancellable tracker draft.");
        var requestedTheme = Root.RequestedTheme;
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                foreach (var size in new[] { new SizeInt32(720, 560), new SizeInt32(1040, 680), new SizeInt32(1280, 800) })
                {
                    AppWindow.Resize(new SizeInt32(Math.Max((int)(size.Width * raster), minimumWidth), (int)(size.Height * raster)));
                    Root.RequestedTheme = theme;
                    await CapturePage("desktop-disconnected-es-" + theme.ToString().ToLowerInvariant() + "-" + size.Width + "x" + size.Height,
                        InspectorContent.Content as FrameworkElement);
                }
        }
        finally
        {
            Root.RequestedTheme = requestedTheme;
            AppWindow.Resize(new SizeInt32(Math.Max((int)(1040 * raster), minimumWidth), (int)(680 * raster)));
        }
        await File.WriteAllTextAsync(signal, "disconnected");
        await CaptureReady(Model, () => Model.IsConnected && !Model.IsLoading && Model.CanEdit);
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.IsAvailable);
        if (!membership.SequenceEqual(Model.Torrents.Select(torrent => torrent.TorrentId).Order()) || Model.Inspector.Target?.TorrentId != target.TorrentId ||
            !Model.Inspector.HasDraft || Model.Inspector.TrackerInput != trackerInput || editor.Text != trackerInput)
            throw new InvalidOperationException("Reconnect lost torrent membership or the unfinished tracker draft.");
        outcomes.Add(new { journey = "engine restart with tracker draft", reconnected = true, membershipRetained = true, targetRetained = true, draftRetained = true });
        await CapturePage("desktop-reconnected", InspectorContent.Content as FrameworkElement);
        CaptureInvoke(CaptureElements(InspectorContent).OfType<Button>().Single(control => control.Name == "CancelTrackers"));
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsEditingTrackers && !Model.Inspector.HasDraft);
        Model.CloseInspector();
    }

    private async Task CaptureDetails(Torrent target, List<object> outcomes)
    {
        var directory = Model.DataDirectory ?? throw new InvalidOperationException("The detail capture has no store.");
        var preview = Path.Combine(directory, "preview.torrent");
        var metadata = System.Text.Encoding.ASCII.GetBytes("d4:infod6:lengthi1e4:name11:preview.bin12:piece lengthi16384e6:pieces20:");
        await File.WriteAllBytesAsync(preview, metadata.Concat(Convert.FromHexString("11f6ad8ec52a2984abaafd7c3b516503785c2072"))
            .Concat(System.Text.Encoding.ASCII.GetBytes("ee")).ToArray());
        await Model.Draft.Cancel();
        Model.Draft.EditingMagnet = true;
        var closed = ShowAdd();
        await CaptureLayout();
        var dialog = _interaction?.Dialog ?? throw new InvalidOperationException("The preview review Add dialog did not open.");
        try
        {
            Model.Draft.Own([preview]);
            await Model.Draft.PrepareAll().WaitAsync(TimeSpan.FromSeconds(20));
            await CaptureReady(Model.Draft, () => Model.Draft.HasFiles && !Model.Draft.IsPending);
            await CapturePage("details-add-file-preview", dialog.Content as FrameworkElement);
            var table = CaptureElements(dialog).OfType<Syno.TableView.Table>().Single(control => control.Name == "Files");
            var sourceRetained = ReferenceEquals(table.ItemsSource, Model.Draft.Files.Roots);
            var fileDisplayed = CaptureElements(table).OfType<TextBlock>().Any(control => control.Text == "preview.bin");
            outcomes.Add(new { journey = "native Add file preview", sourceRetained, fileDisplayed, files = Model.Draft.Files.Roots.Count });
            if (!sourceRetained || !fileDisplayed)
                throw new InvalidOperationException("The native file browser did not follow the metadata-ready Add draft.");
        }
        finally { dialog.Hide(); await closed; }
        await ShowTorrents();
        Torrents.Selection = new Syno.TableView.Selection([target], target);
        await SelectTorrent();
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.Files);
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.HasFiles);
        await CaptureLayout();
        var file = Model.Inspector.Files.Roots.SelectMany(root => root.Nodes()).First(node => node.Index >= 0 && !node.IsPadding);
        var priority = CaptureElements(InspectorContent).OfType<ComboBox>().First();
        var originalPriority = priority.SelectedIndex;
        priority.SelectedIndex = 4;
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsPending && !Model.Inspector.HasFileDraft && !Model.Inspector.IsLoading);
        if (file.Priority != 7 || !Model.Inspector.Files.HasWanted)
            throw new InvalidOperationException("The native priority choice was not saved.");
        outcomes.Add(new { journey = "native file priority", priority = file.Priority, appliedImmediately = true });
        priority.SelectedIndex = originalPriority;
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsPending && !Model.Inspector.HasFileDraft && !Model.Inspector.IsLoading);
        if (file.PriorityIndex != originalPriority)
            throw new InvalidOperationException("The native priority choice could not be restored.");

        Model.Inspector.Select(InspectorSection.Trackers);
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.EditTrackers.CanExecute(null));
        await CaptureLayout();
        Button FindButton(string name) => CaptureElements(InspectorContent).OfType<Button>().Single(control => control.Name == name);
        var trackers = Model.Inspector.Trackers.Select(tracker => (tracker.Url, tracker.Tier)).ToHashSet();
        CaptureInvoke(FindButton("EditTrackers"));
        await CaptureLayout();
        var editor = CaptureElements(InspectorContent).OfType<TextBox>().Single(control => control.Name == "TrackerInput");
        var originalInput = editor.Text;
        var populated = new (string Url, int Tier)[]
        {
            ("https://example.invalid/announce", 0),
            ("https://example.invalid/private/trackers/announce?passkey=0123456789abcdef0123456789abcdef&source=tinytorrent&region=western-europe", 0),
            ("udp://example.invalid:6969/announce", 1),
            ("https://example.invalid/backup/announce", 1)
        }.ToHashSet();
        var trackerInput = string.Join(Environment.NewLine + Environment.NewLine, populated.GroupBy(tracker => tracker.Tier)
            .OrderBy(tier => tier.Key).Select(tier => string.Join(Environment.NewLine, tier.Select(tracker => tracker.Url))));
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
        outcomes.Add(new { journey = "language switch with tracker draft", inputRetained, modelRetained, focusRetained,
            draftRetained = Model.Inspector.HasDraft, inputCanonicalized = nativeInput != trackerInput,
            carriageReturns = nativeInput.Count(character => character == '\r'), lineFeeds = nativeInput.Count(character => character == '\n'),
            focusSource = "programmatic native focus", language });
        if (!inputRetained || !modelRetained || !Model.Inspector.HasDraft || !focusRetained)
            throw new InvalidOperationException("The live language switch lost tracker input or focus.");
        await CapturePage("details-live-tracker-draft", InspectorContent.Content as FrameworkElement);
        if (!Model.AllPaused) throw new InvalidOperationException("The tracker capture requires a globally paused fixture.");
        CaptureInvoke(FindButton("SaveTrackers"));
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsEditingTrackers && !Model.Inspector.IsPending && !Model.Inspector.IsLoading &&
            populated.SetEquals(Model.Inspector.Trackers.Select(tracker => (tracker.Url, tracker.Tier))));
        outcomes.Add(new { journey = "native tracker save", confirmed = true, count = populated.Count, tiers = new[] { 0, 1 }, globalPaused = Model.AllPaused });
        foreach (var captureLanguage in new[] { "en", "es" })
        foreach (var theme in new[] { "light", "dark" })
        {
            await Model.Preferences.SelectTheme(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            Model.SelectLanguage(captureLanguage);
            await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == captureLanguage);
            await CaptureLayout();
            foreach (var size in new[] { new SizeInt32(720, 560), new SizeInt32(1040, 680), new SizeInt32(1280, 800) })
            {
                if (!Model.AllPaused) throw new InvalidOperationException("The populated tracker capture lost global pause.");
                var scale = Root.XamlRoot.RasterizationScale;
                var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                AppWindow.Resize(new SizeInt32(Math.Max((int)Math.Round(size.Width * scale), minimum), (int)Math.Round(size.Height * scale)));
                await CapturePage($"details-{captureLanguage}-{theme}-{size.Width}x{size.Height}-trackers", InspectorContent.Content as FrameworkElement);
                outcomes.Add(new { journey = "populated tracker capture", language = captureLanguage, theme, requestedWidth = size.Width,
                    requestedHeight = size.Height, clientWidth = AppWindow.ClientSize.Width, count = Model.Inspector.Trackers.Count, globalPaused = Model.AllPaused });
            }
        }
        CaptureInvoke(FindButton("EditTrackers"));
        await CaptureLayout();
        if (Model.Inspector.HasDraft) throw new InvalidOperationException("Reopening the saved tracker list created an untouched draft.");
        outcomes.Add(new { journey = "untouched populated tracker editor", draftCreated = false, count = Model.Inspector.Trackers.Count });
        editor = CaptureElements(InspectorContent).OfType<TextBox>().Single(control => control.Name == "TrackerInput");
        editor.Text = originalInput;
        CaptureInvoke(FindButton("SaveTrackers"));
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsEditingTrackers && !Model.Inspector.IsPending && !Model.Inspector.IsLoading &&
            trackers.SetEquals(Model.Inspector.Trackers.Select(tracker => (tracker.Url, tracker.Tier))));
        Model.CloseInspector();

        var preferences = Model.Preferences;
        await ShowPreferences(new(PreferenceSection.General));
        await CaptureLayout();
        var form = _preferencesForm ?? throw new InvalidOperationException("The detail review preferences did not open.");
        var toggle = CaptureElements(form).OfType<ToggleSwitch>().Single(control => ReferenceEquals(control.Tag, preferences.ShowSplash));
        var originalSplash = toggle.IsOn;
        toggle.IsOn = !originalSplash;
        await CaptureReady(preferences, () => !preferences.ShowSplash.IsPending && !preferences.ShowSplash.HasDraft);
        if (preferences.ShowSplash.IsOn == originalSplash)
            throw new InvalidOperationException("The native preference switch did not apply immediately.");
        toggle.IsOn = originalSplash;
        await CaptureReady(preferences, () => !preferences.ShowSplash.IsPending && !preferences.ShowSplash.HasDraft);
        if (preferences.ShowSplash.IsOn != originalSplash)
            throw new InvalidOperationException("The native preference switch could not be restored.");
        outcomes.Add(new { journey = "native immediate preference", applied = true, restored = true });

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
                throw new InvalidOperationException("Capture review requires an absolute disposable store path.");
            await CaptureReady(Model, () => Model.IsConnected && !Model.IsLoading);
            await Model.LanguageLoad;
            if (!string.Equals(Path.GetFullPath(store).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(Model.DataDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The connected engine does not own the capture store.");
            if (ReviewMode == CaptureMode.PreferencesLayout)
            {
                foreach (var language in new[] { "en", "es" })
                foreach (var theme in new[] { "light", "dark" })
                {
                    Model.SelectLanguage(language);
                    await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
                    await Model.Preferences.SelectTheme(theme);
                    await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                    foreach (var size in new[] { new SizeInt32(720, 560), new SizeInt32(1040, 680), new SizeInt32(1280, 800) })
                    {
                        var scale = Root.XamlRoot.RasterizationScale;
                        var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                        AppWindow.Resize(new SizeInt32(Math.Max((int)(size.Width * scale), minimum), (int)(size.Height * scale)));
                        await ShowPreferences(new(PreferenceSection.General));
                        var form = _preferencesForm ?? throw new InvalidOperationException("The preferences form did not open.");
                        await CaptureReady(Model.Preferences, () => Model.Preferences.HasRegistration && !Model.Preferences.IsPending);
                        var name = "preferences-" + language + "-" + theme + "-" + size.Width + "x" + size.Height;
                        await CapturePage(name, form);
                        if (language == "en" && theme == "light" && size.Width == 720)
                        {
                            var startup = (ToggleSwitch)form.FindName("Startup");
                            var handlers = (ToggleSwitch)form.FindName("Handlers");
                            var matches = startup.IsOn == Model.Preferences.Startup && handlers.IsOn == Model.Preferences.HandlersRegistered &&
                                startup.IsEnabled == Model.Preferences.CanRegister && handlers.IsEnabled == Model.Preferences.CanRegister;
                            outcomes.Add(new { journey = "observed native registration", startup = startup.IsOn, handlers = handlers.IsOn,
                                observedStartup = Model.Preferences.Startup, observedHandlers = Model.Preferences.HandlersRegistered,
                                startupEnabled = startup.IsEnabled, handlersEnabled = handlers.IsEnabled,
                                canRegister = Model.Preferences.CanRegister, matches });
                            if (!matches) throw new InvalidOperationException("Native registration switches do not match their settled observed state.");
                        }
                        foreach (var section in new[] { "StartupSection", "DefaultsSection" })
                        {
                            var element = form.FindName(section) as FrameworkElement ?? throw new InvalidOperationException("The registration section did not load.");
                            element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                            await CaptureLayout();
                            await CaptureUi(name + "-" + section.Replace("Section", string.Empty).ToLowerInvariant());
                        }
                        if (form.FindName("NotificationsSection") is FrameworkElement notifications)
                        {
                            notifications.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
                            await CaptureLayout();
                            await CaptureUi(name + "-notifications");
                        }
                        if (language == "en" && theme == "light" && size.Width == 1040)
                        {
                            var preference = Model.Preferences.AddedNotifications;
                            var toggle = CaptureElements(form).OfType<ToggleSwitch>().Single(control => ReferenceEquals(control.Tag, preference));
                            var original = preference.ConfirmedOn;
                            toggle.IsOn = !original;
                            await CaptureReady(preference, () => !preference.IsPending && !preference.HasDraft && preference.ConfirmedOn == !original);
                            toggle.IsOn = original;
                            await CaptureReady(preference, () => !preference.IsPending && !preference.HasDraft && preference.ConfirmedOn == original);
                            outcomes.Add(new { journey = "notification preference", nativeToggleCommitted = true, originalRestored = true });
                        }
                        await ShowTorrents();
                        var torrent = Model.Torrents.First();
                        Model.ReceiveNotice(JsonSerializer.SerializeToElement(new { type = "notice", kind = "completed", torrent_id = torrent.TorrentId, name = torrent.Name, detail = string.Empty, count = 1 }));
                        await CaptureLayout();
                        if (!Model.HasCompletion || !Model.OpenCompletion.CanExecute(null))
                            throw new InvalidOperationException("The completion notice has no available folder action.");
                        await CapturePage(name + "-completion");
                        if (language == "en" && theme == "light" && size.Width == 1040)
                        {
                            Search.Focus(FocusState.Programmatic);
                            await CaptureLayout();
                            if (!HasEditorFocus() || CompletionNotice.Visibility != Visibility.Collapsed || !Model.HasCompletion)
                                throw new InvalidOperationException("Editing search did not postpone completion feedback.");
                            Torrents.Focus(FocusState.Programmatic);
                            await CaptureLayout();
                            if (CompletionNotice.Visibility != Visibility.Visible)
                                throw new InvalidOperationException("Completion feedback did not return after editing.");
                            await CaptureReady(Model, () => !Model.HasCompletion);
                            outcomes.Add(new { journey = "completion lifetime", postponedWhileEditing = true, resumedAfterEditing = true, dismissedAfterTimeout = true });
                        }
                        Model.DismissCompletion();
                        completed.Add(name);
                    }
                }
                outcomes.Add(new { journey = "completion presentation", scope = "Simulated engine notice through the production UI handler; no download or Explorer launch" });
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
            var target = Model.Torrents.FirstOrDefault() ?? throw new InvalidOperationException("The review store has no torrent.");
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
            if (addOnly) await Model.Draft.Cancel();
            var preferences = Model.Preferences.Fields.Select(field => (field.Name, field.Input, field.IsOn)).ToArray();
            (int Index, int Priority)[]? priorities = null;
            string[] languages = filesOnly || addOnly ? ["en", "es"] : [Model.Text.Language];
            var themes = ReviewMode == CaptureMode.Smoke ? Array.Empty<string>() : ["light", "dark"];
            foreach (var language in languages)
            foreach (var theme in themes)
            {
                await Model.Preferences.SelectTheme(theme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                Model.SelectLanguage(language);
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
                foreach (var size in new[] { new SizeInt32(720, 560), new SizeInt32(1040, 680), new SizeInt32(1280, 800) })
                {
                    var prefix = (addOnly ? "add-layout-" + language + "-" : filesOnly ? "details-" + language + "-" : string.Empty) + theme + "-" + size.Width + "x" + size.Height + "-";
                    var scale = Root.XamlRoot.RasterizationScale;
                    var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                    AppWindow.Resize(new SizeInt32(Math.Max((int)(size.Width * scale), minimum), (int)(size.Height * scale)));
                    await ShowTorrents();
                    if (!filesOnly) Model.CloseInspector();
                    if (addOnly)
                    {
                        await CapturePage(prefix + "header");
                        if (language == "en" && size.Width == 1040)
                        {
                            var states = new[] { (AddButton, "PointerOver"), (MagnetButton, "Pressed"), (ThemeButton, "Disabled") };
                            try
                            {
                                foreach (var (button, state) in states)
                                    if (!VisualStateManager.GoToState(button, state, false))
                                        throw new InvalidOperationException("The caption state is unavailable: " + state);
                                await CaptureUi(prefix + "caption-states");
                            }
                            finally
                            {
                                foreach (var (button, _) in states)
                                    VisualStateManager.GoToState(button, button.IsEnabled ? "Normal" : "Disabled", false);
                            }
                        }
                        Torrents.Selection = new Syno.TableView.Selection([target], target);
                        await SelectTorrent();
                        Run(Model.Properties);
                        Model.Inspector.Select(InspectorSection.General);
                        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading);
                        await CapturePage(prefix + "headers", InspectorContent.Content as FrameworkElement);
                        var message = Feedback.Message;
                        var severity = Feedback.Severity;
                        var visibility = Feedback.Visibility;
                        var open = Feedback.IsOpen;
                        var action = Feedback.ActionButton.Visibility;
                        var workspace = new { width = Torrents.ActualWidth, height = Torrents.ActualHeight, footer = StatusBar.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point()).Y };
                        try
                        {
                            Feedback.Message = Model.Text.Get("window", "connecting");
                            Feedback.Severity = InfoBarSeverity.Warning;
                            Feedback.Visibility = Visibility.Visible;
                            Feedback.IsOpen = true;
                            Feedback.ActionButton.Visibility = Visibility.Visible;
                            await CapturePage(prefix + "connection-overlay");
                            outcomes.Add(new { journey = prefix + "overlay layout", scope = "Presentation only; the engine remains connected",
                                before = workspace, after = new { width = Torrents.ActualWidth, height = Torrents.ActualHeight, footer = StatusBar.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point()).Y },
                                caption = new { add = AddButton.ActualWidth, magnet = MagnetButton.ActualWidth, theme = ThemeButton.ActualWidth, inset = AppWindow.TitleBar.RightInset } });
                        }
                        finally
                        {
                            Feedback.Message = message;
                            Feedback.Severity = severity;
                            Feedback.Visibility = visibility;
                            Feedback.IsOpen = open;
                            Feedback.ActionButton.Visibility = action;
                        }
                        Model.CloseInspector();
                        Model.Draft.EditingMagnet = true;
                        var closed = ShowAdd();
                        await CaptureLayout();
                        var dialog = _interaction?.Dialog ?? throw new InvalidOperationException("The Add dialog did not open: " + Model.CommandError);
                        try
                        {
                            await CapturePage(prefix + "add", dialog.Content as FrameworkElement);
                            var editor = CaptureElements(dialog).OfType<TextBox>().Single(control => control.Name == "MagnetInput");
                            var magnet = "magnet:?xt=urn:btih:" + target.Hashes[0] + "&dn=Example%20download" +
                                string.Concat(Enumerable.Range(1, 12).Select(index => "&tr=https%3A%2F%2Ftracker" + index + ".example.invalid%2Fannounce"));
                            editor.Text = magnet;
                            await CapturePage(prefix + "long-magnet", dialog.Content as FrameworkElement);
                            if (Model.Draft.Magnet != magnet)
                                throw new InvalidOperationException("The wrapped magnet editor changed its input.");
                            if (language == "es" && theme == "dark" && size.Width == 720)
                            {
                                editor.Text = "invalid magnet";
                                var preview = CaptureElements(dialog).OfType<Button>().Single(control => control.Name == "Preview");
                                CaptureInvoke(preview);
                                await CaptureReady(Model.Draft, () => Model.Draft.HasMagnetError && !Model.Draft.IsPending);
                                if (editor.Text != "invalid magnet" || Model.Draft.Magnet != "invalid magnet")
                                    throw new InvalidOperationException("The magnet editor lost its rejected input.");
                                await CapturePage(prefix + "magnet-error", dialog.Content as FrameworkElement);
                                editor.Text = "magnet:?xt=urn:btih:" + target.Hashes[0];
                                CaptureInvoke(preview);
                                await CaptureReady(Model.Draft, () => Model.Draft.HasSources && !Model.Draft.IsPending);
                                await CapturePage(prefix + "magnet-preview", dialog.Content as FrameworkElement);
                                outcomes.Add(new { journey = "magnet preview", rejectedInputRetained = true, previewVisible = Model.Draft.HasSources });
                            }
                        }
                        finally { dialog.Hide(); await closed; }
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
                    foreach (var section in filesOnly ? new[] { InspectorSection.Files } : Enum.GetValues<InspectorSection>())
                    {
                        Model.Inspector.Select(section);
                        await CaptureLayout();
                        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && (!filesOnly || Model.Inspector.HasFiles));
                        if (filesOnly)
                        {
                            priorities ??= Model.Inspector.Files.Roots.SelectMany(root => root.Nodes())
                                .Where(file => file.Index >= 0).Select(file => (file.Index, file.Priority)).OrderBy(file => file.Index).ToArray();
                        }
                        await CapturePage(prefix + "properties-" + section, InspectorContent.Content as FrameworkElement);
                        if (filesOnly && section == InspectorSection.Files && size.Width == 720)
                        {
                            var priority = CaptureElements(InspectorContent).OfType<ComboBox>().First();
                            priority.IsDropDownOpen = true;
                            try { await CapturePage(prefix + "priority-choices"); }
                            finally { priority.IsDropDownOpen = false; }
                        }
                    }
                    if (!filesOnly) Model.CloseInspector();
                    foreach (var section in filesOnly ? new[] { PreferenceSection.Appearance, PreferenceSection.Network } : Enum.GetValues<PreferenceSection>())
                    {
                        await ShowPreferences(new(section));
                        await CapturePage(prefix + "settings-" + section, _preferencesForm);
                        if (!filesOnly && section == PreferenceSection.Schedule && Model.Preferences.Schedule.Periods.FirstOrDefault() is { } period)
                        {
                            Model.Preferences.Schedule.Edit(period);
                            await CaptureLayout();
                            await CaptureUi(prefix + "period-editor");
                            Run(Model.Preferences.Schedule.CancelPeriod);
                        }
                    }
                    if (filesOnly) { completed.Add(prefix); continue; }
                    await ShowAbout();
                    await CapturePage(prefix + "about");
                    await ShowTorrents();
                    await CaptureDialog(prefix + "remove", () => ConfirmRemove([target]), () => _interaction?.Dialog);
                    await CaptureDialog(prefix + "move", () => ShowFiles([target], FileAction.Move), () => _interaction?.Dialog);
                    await CaptureDialog(prefix + "delete", () => ShowFiles([target], FileAction.Delete), () => _interaction?.Dialog);
                    Model.Draft.EditingMagnet = true;
                    await CaptureDialog(prefix + "add", ShowAdd, () => _interaction?.Dialog);
                    completed.Add(prefix);
                }
            }
            if (addOnly) return;
            if (filesOnly)
            {
                await Model.Preferences.SelectTheme("light");
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
                Model.SelectLanguage("en");
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "en");
                var scale = Root.XamlRoot.RasterizationScale;
                var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                AppWindow.Resize(new SizeInt32(Math.Max((int)(720 * scale), minimum), (int)(560 * scale)));
                await ShowTorrents();
                await CapturePage("details-en-light-720x560-reverse-properties-Files", InspectorContent.Content as FrameworkElement);
                foreach (var section in new[] { PreferenceSection.Appearance, PreferenceSection.Network })
                {
                    await ShowPreferences(new(section));
                    await CapturePage("details-en-light-720x560-reverse-settings-" + section, _preferencesForm);
                }
                var originalTheme = preferences.Single(field => field.Name == Model.Preferences.Theme.Name).Input;
                var originalLanguage = preferences.Single(field => field.Name == Model.Preferences.Language.Name).Input;
                await Model.Preferences.SelectTheme(originalTheme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == originalTheme);
                Model.SelectLanguage(originalLanguage);
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == originalLanguage);
                var prioritiesRetained = priorities is not null &&
                    priorities.SequenceEqual(Model.Inspector.Files.Roots.SelectMany(root => root.Nodes())
                        .Where(file => file.Index >= 0).Select(file => (file.Index, file.Priority)).OrderBy(file => file.Index));
                var preferencesRetained = preferences.SequenceEqual(Model.Preferences.Fields.Select(field => (field.Name, field.Input, field.IsOn)));
                var fileDraft = Model.Inspector.HasFileDraft;
                var preferenceDraft = Model.Preferences.HasDraft;
                var pending = Model.Preferences.IsPending;
                outcomes.Add(new { journey = "live language selected choices", languages = new[] { "en", "es", "en" },
                    prioritiesRetained, preferencesRetained, fileDraft, preferenceDraft, pending });
                if (!prioritiesRetained || !preferencesRetained || fileDraft || preferenceDraft || pending)
                    throw new InvalidOperationException("Changing the capture language altered file priorities or preferences.");
                return;
            }
            if (completed.Count > 0)
            {
                Model.SelectLanguage("es");
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "es");
                await ShowPreferences(new(PreferenceSection.Schedule));
                await CapturePage("spanish-schedule", _preferencesForm);
                Model.SelectLanguage("en");
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "en");
            }
            AppWindow.Resize(new SizeInt32(1040, 680));
            await ShowTorrents();
            await CaptureSmoke(target, outcomes);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (_captureDirectory is not null)
            {
                Directory.CreateDirectory(_captureDirectory);
                await File.WriteAllTextAsync(Path.Combine(_captureDirectory, "review.json"), JsonSerializer.Serialize(new
                {
                    completed, outcomes, milliseconds = clock.ElapsedMilliseconds, failure = failure?.ToString(),
                    scope = "Real XAML views and bounded review journeys in a disposable store; no desktop input or capture"
                }));
            }
            await Model.CancelDraft();
            await CloseWindow(engineExit: false);
        }
    }
}
