using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
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
    internal static bool IsCaptureReview => Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") is "1" or "smoke" or "shell" or "schedule" or "desktop" or "details" or "details-files" or "files" or "files-layout" or "search" or "library" or "traffic";

    internal void ShowCaptureReview()
    {
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Move(new PointInt32(-10000, -10000));
        AppWindow.Show(false);
    }

    private void ConfigureCapture()
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
            if (ReferenceEquals(dialog, _filesDialog)) await CaptureReady(Model.Files, () => !Model.Files.IsPending);
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
        var closed = ShowLimits();
        await CaptureLayout();
        var dialog = _limitsDialog ?? throw new InvalidOperationException("The limits dialog did not open.");
        try
        {
            var number = CaptureElements(dialog).OfType<NumberBox>().First();
            var input = CaptureElements(number).OfType<TextBox>().First();
            input.Focus(FocusState.Programmatic);
            input.Text = "abc";
            await CaptureLayout();
            if (Model.Speed.Choices[0].Input != "abc")
                throw new InvalidOperationException($"The limits owner did not retain typed input: {Model.Speed.Choices[0].Input}; editor: {input.Text}.");
            CaptureInvoke(dialog);
            try { await CaptureReady(Model.Speed, () => Model.Speed.HasError || _limitsDialog is null); }
            catch (TimeoutException)
            {
                throw new InvalidOperationException($"Apply did not report the input outcome: owner={Model.Speed.Choices[0].Input}, editor={input.Text}, enabled={dialog.IsPrimaryButtonEnabled}.");
            }
            if (_limitsDialog is null) throw new InvalidOperationException("Apply closed the limits dialog with invalid input.");
            if (Model.Speed.Choices[0].Input != "abc" || input.Text != "abc")
                throw new InvalidOperationException("The limits editor lost the rejected input.");
            outcomes.Add(new { journey = "invalid speed limit", rejectedInputRetained = true, dialogStayedOpen = true });
            await CapturePage("invalid-limit", dialog.Content as FrameworkElement);
        }
        finally { dialog.Hide(); await closed; }

        Model.Draft.EditingMagnet = true;
        closed = ShowAdd();
        await CaptureLayout();
        dialog = _addDialog ?? throw new InvalidOperationException("The Add dialog did not open.");
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

        Model.Select([target], target);
        Run(Model.Properties);
        Model.Inspector.Select(InspectorSection.Trackers);
        await CaptureLayout();
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading && Model.Inspector.EditTrackers.CanExecute(null));
        Run(Model.Inspector.EditTrackers);
        Model.Inspector.TrackerInput = "https://example.invalid/announce";
        closed = ConfirmRemove([target]);
        await CaptureLayout();
        dialog = _removeDialog ?? throw new InvalidOperationException("The removal dialog did not open.");
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
            await Model.SelectTheme(theme);
            await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
            foreach (var width in new[] { 1040, 720 })
            {
                var scale = Root.XamlRoot.RasterizationScale;
                var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                AppWindow.Resize(new SizeInt32(Math.Max((int)(width * scale), minimum), (int)(680 * scale)));
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
            invoke.Invoke();
            await CaptureReady(Model, () => Model.CanEdit && (command == Model.Pause ? target.IsPaused : !target.IsPaused));
            outcomes.Add(new { command = item.Text, enabled = item.IsEnabled, status = target.StatusCode });
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
        FiltersItem.IsChecked = true;
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
        await CapturePage("shell-spanish");
        await CaptureDialog("shell-add", () => { Run(Model.AddMagnet); return _dialogClosed?.Task ?? Task.CompletedTask; }, () => _addDialog);
    }

    private async Task CaptureSchedule(List<object> outcomes, List<string> completed)
    {
        var preferences = Model.Preferences;
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
                    await Model.SelectTheme(theme);
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
        var dialog = _addDialog ?? throw new InvalidOperationException("The desktop review Add dialog did not open.");
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
                await Model.SelectTheme(theme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                foreach (var size in new[] { new SizeInt32(720, 560), new SizeInt32(1040, 680), new SizeInt32(1280, 800) })
                {
                    var scale = Root.XamlRoot.RasterizationScale;
                    var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                    AppWindow.Resize(new SizeInt32(Math.Max((int)(size.Width * scale), minimum), (int)(size.Height * scale)));
                    var prefix = "desktop-" + language + "-" + theme + "-" + size.Width + "x" + size.Height;
                    Run(Model.Exit);
                    await CaptureReady(Model, () => _closePrompt is not null);
                    await CapturePage(prefix + "-exit-prompt");
                    var prompt = _closePrompt ?? throw new InvalidOperationException("Exit did not retain its draft prompt.");
                    CaptureInvoke(CaptureElements(prompt).OfType<Button>().Single(control => control.Name == "CloseButton"));
                    await CaptureReady(Model, () => !Model.IsClosing && Model.CanEdit && _addDialog is not null);
                    await CaptureLayout();
                    dialog = _addDialog ?? throw new InvalidOperationException("Keep input did not recover the Add form.");
                    input = CaptureElements(dialog).OfType<TextBox>().Single(control => control.Name == "MagnetInput");
                    if (input.Text != magnet || Model.Draft.Magnet != magnet || !Model.Draft.HasChanges)
                        throw new InvalidOperationException("Keep input lost the unfinished magnet.");
                    completed.Add(prefix);
                }
            }
        }
        outcomes.Add(new { journey = "Exit with unfinished magnet", prompts = 12, inputRetained = true, addFormRecovered = true, engineConnected = Model.IsConnected });
        await CapturePage("desktop-kept-magnet", dialog.Content as FrameworkElement);
        var closed = _dialogClosed?.Task ?? throw new InvalidOperationException("The recovered Add dialog has no close completion.");
        CaptureInvoke(CaptureElements(dialog).OfType<Button>().Single(control => control.Name == "CloseButton"));
        await closed;
        await CaptureReady(Model.Draft, () => !Model.Draft.HasChanges && !Model.Draft.EditingMagnet);
        outcomes.Add(new { journey = "cancel recovered Add", draftCleared = true });

        Model.SelectLanguage("es");
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "es");
        await Model.SelectTheme("light");
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
        const string trackerInput = "https://example.invalid/details";
        editor.Focus(FocusState.Programmatic);
        editor.Text = trackerInput;
        var language = Model.Text.Language == "en" ? "es" : "en";
        Model.SelectLanguage(language);
        await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
        await CaptureLayout();
        if (editor.Text != trackerInput || Model.Inspector.TrackerInput != trackerInput || !Model.Inspector.HasDraft ||
            !ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), editor))
            throw new InvalidOperationException("The live language switch lost tracker input or focus.");
        outcomes.Add(new { journey = "language switch with tracker draft", inputRetained = true, focusRetained = true, focusSource = "programmatic native focus", language });
        await CapturePage("details-live-tracker-draft", InspectorContent.Content as FrameworkElement);
        CaptureInvoke(FindButton("SaveTrackers"));
        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsEditingTrackers && !Model.Inspector.IsPending && !Model.Inspector.IsLoading &&
            Model.Inspector.Trackers.Any(tracker => tracker.Url == trackerInput));
        outcomes.Add(new { journey = "native tracker save", confirmed = true });
        CaptureInvoke(FindButton("EditTrackers"));
        await CaptureLayout();
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

        await ShowPreferences(new(PreferenceSection.Network));
        await CaptureLayout();
        var number = CaptureElements(form).OfType<NumberBox>().Single(control => ReferenceEquals(control.Tag, preferences.Port));
        var input = CaptureElements(number).OfType<TextBox>().First();
        var next = CaptureElements(form).OfType<NumberBox>().Single(control => ReferenceEquals(control.Tag, preferences.Connections));
        var departure = CaptureElements(next).OfType<TextBox>().First();
        var originalPort = preferences.Port.Input;
        input.Focus(FocusState.Programmatic);
        input.Text = "70000";
        await CaptureLayout();
        departure.Focus(FocusState.Programmatic);
        await CaptureReady(preferences.Port, () => preferences.Port.Message.Length > 0);
        if (preferences.Port.Input != "70000" || input.Text != "70000" || !preferences.Port.HasDraft || preferences.Port.IsPending)
            throw new InvalidOperationException("The invalid native port edit was not retained and rejected.");
        outcomes.Add(new { journey = "invalid native port", rejectedOnDeparture = true, inputRetained = true });
        await CapturePage("details-invalid-port", form);
        input.Focus(FocusState.Programmatic);
        input.Text = originalPort;
        await CaptureLayout();
        departure.Focus(FocusState.Programmatic);
        await CaptureLayout();
        if (preferences.Port.HasDraft || preferences.Port.Message.Length > 0)
            throw new InvalidOperationException("Correcting the native port edit did not recover the field.");
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
            if (Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "schedule")
            {
                await CaptureSchedule(outcomes, completed);
                return;
            }
            if (Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") is "files" or "files-layout")
            {
                await CaptureFiles(outcomes, completed);
                return;
            }
            if (Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "library")
            {
                await CaptureLibrary(outcomes, completed);
                return;
            }
            var target = Model.Torrents.FirstOrDefault() ?? throw new InvalidOperationException("The review store has no torrent.");
            if (Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "traffic")
            {
                await CaptureTraffic(target, outcomes, completed);
                return;
            }
            if (Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "search")
            {
                await CaptureSearch(target, outcomes, completed);
                return;
            }
            if (Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "desktop")
            {
                await CaptureDesktop(target, outcomes, completed);
                return;
            }
            if (Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "shell")
            {
                await CaptureShell(target, outcomes);
                return;
            }
            var filesOnly = Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "details-files";
            var details = filesOnly || Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "details";
            var preferences = Model.Preferences.Fields.Select(field => (field.Name, field.Input, field.IsOn)).ToArray();
            (int Index, int Priority)[]? priorities = null;
            FrameworkElement? inspector = null;
            PreferencesForm? settings = null;
            string[] languages = filesOnly ? ["en", "es"] : [Model.Text.Language];
            var themes = Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "smoke" ? Array.Empty<string>() : ["light", "dark"];
            foreach (var language in languages)
            foreach (var theme in themes)
            {
                await Model.SelectTheme(theme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                Model.SelectLanguage(language);
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == language);
                foreach (var size in new[] { new SizeInt32(720, 560), new SizeInt32(1040, 680), new SizeInt32(1280, 800) })
                {
                    var prefix = (details ? "details-" + language + "-" : string.Empty) + theme + "-" + size.Width + "x" + size.Height + "-";
                    var scale = Root.XamlRoot.RasterizationScale;
                    var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                    AppWindow.Resize(new SizeInt32(Math.Max((int)(size.Width * scale), minimum), (int)(size.Height * scale)));
                    await ShowTorrents();
                    if (!filesOnly) Model.CloseInspector();
                    if (!details)
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
                            inspector ??= InspectorContent.Content as FrameworkElement;
                            if (!ReferenceEquals(inspector, InspectorContent.Content))
                                throw new InvalidOperationException("The language capture recreated the file form.");
                        }
                        await CapturePage(prefix + "properties-" + section, InspectorContent.Content as FrameworkElement);
                        if (details && section == InspectorSection.Files && size.Width == 720)
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
                        if (filesOnly)
                        {
                            settings ??= _preferencesForm;
                            if (!ReferenceEquals(settings, _preferencesForm))
                                throw new InvalidOperationException("The language capture recreated the preferences form.");
                        }
                        await CapturePage(prefix + "settings-" + section, _preferencesForm);
                        if (!details && section == PreferenceSection.Schedule && Model.Preferences.Periods.FirstOrDefault() is { } period)
                        {
                            Model.Preferences.Edit(period);
                            await CaptureLayout();
                            await CaptureUi(prefix + "period-editor");
                            Run(Model.Preferences.CancelPeriod);
                        }
                    }
                    if (details) { completed.Add(prefix); continue; }
                    await ShowAbout();
                    await CapturePage(prefix + "about");
                    await ShowTorrents();
                    await CaptureDialog(prefix + "limits", () => ShowLimits(), () => _limitsDialog);
                    await CaptureDialog(prefix + "remove", () => ConfirmRemove([target]), () => _removeDialog);
                    await CaptureDialog(prefix + "move", () => ShowFiles([target], FileAction.Move), () => _filesDialog);
                    await CaptureDialog(prefix + "delete", () => ShowFiles([target], FileAction.Delete), () => _filesDialog);
                    Model.Draft.EditingMagnet = true;
                    await CaptureDialog(prefix + "add", ShowAdd, () => _addDialog);
                    completed.Add(prefix);
                }
            }
            if (filesOnly)
            {
                await Model.SelectTheme("light");
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == "light");
                Model.SelectLanguage("en");
                await CaptureReady(Model, () => Model.CanClose && Model.Text.Language == "en");
                var scale = Root.XamlRoot.RasterizationScale;
                var minimum = ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).PreferredMinimumWidth ?? 0;
                AppWindow.Resize(new SizeInt32(Math.Max((int)(720 * scale), minimum), (int)(560 * scale)));
                await ShowTorrents();
                await CapturePage("details-en-light-720x560-reverse-properties-Files", inspector);
                foreach (var section in new[] { PreferenceSection.Appearance, PreferenceSection.Network })
                {
                    await ShowPreferences(new(section));
                    await CapturePage("details-en-light-720x560-reverse-settings-" + section, _preferencesForm);
                }
                if (!ReferenceEquals(inspector, InspectorContent.Content) || !ReferenceEquals(settings, _preferencesForm) || priorities is null ||
                    !priorities.SequenceEqual(Model.Inspector.Files.Roots.SelectMany(root => root.Nodes())
                        .Where(file => file.Index >= 0).Select(file => (file.Index, file.Priority)).OrderBy(file => file.Index)) ||
                    !preferences.SequenceEqual(Model.Preferences.Fields.Select(field => (field.Name, field.Input, field.IsOn))) ||
                    Model.Inspector.HasFileDraft || Model.Preferences.HasDraft || Model.Preferences.IsPending)
                    throw new InvalidOperationException("Changing the capture language altered file priorities or preferences.");
                outcomes.Add(new { journey = "live language selected choices", languages = new[] { "en", "es", "en" },
                    fileFormRetained = true, preferencesFormRetained = true, prioritiesRetained = true, preferencesRetained = true });
                return;
            }
            if (details)
            {
                await CaptureDetails(target, outcomes);
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
            if (themes.Length == 0)
                foreach (var section in Enum.GetValues<PreferenceSection>())
                {
                    await ShowPreferences(new(section));
                    await CapturePage("settings-" + section, _preferencesForm);
                }
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
