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

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private string? _captureDirectory;
    private Task? _capture;
    internal static bool IsCaptureReview => Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") is "1" or "smoke";

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
        button.Focus(FocusState.Programmatic);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("The dialog primary button cannot be invoked.");
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
            if (!string.Equals(Path.GetFullPath(store).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(Model.DataDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The connected engine does not own the capture store.");
            var target = Model.Torrents.FirstOrDefault() ?? throw new InvalidOperationException("The review store has no torrent.");
            var themes = Environment.GetEnvironmentVariable("TINYTORRENT_CAPTURE_REVIEW") == "smoke" ? Array.Empty<string>() : ["light", "dark"];
            foreach (var theme in themes)
            {
                await Model.SelectTheme(theme);
                await CaptureReady(Model, () => Model.CanClose && Model.Theme == theme);
                foreach (var size in new[] { new SizeInt32(1040, 680), new SizeInt32(1280, 800), new SizeInt32(720, 560) })
                {
                    var prefix = theme + "-" + size.Width + "x" + size.Height + "-";
                    AppWindow.Resize(size);
                    await ShowTorrents();
                    Model.CloseInspector();
                    await CapturePage(prefix + "torrents");
                    Model.IsFilterOpen = true;
                    await CapturePage(prefix + "filters");
                    Model.IsFilterOpen = false;
                    Torrents.Selection = new Syno.TableView.Selection([target], target);
                    await SelectTorrent();
                    Run(Model.Properties);
                    foreach (var section in Enum.GetValues<InspectorSection>())
                    {
                        Model.Inspector.Select(section);
                        await CaptureLayout();
                        await CaptureReady(Model.Inspector, () => !Model.Inspector.IsLoading);
                        await CapturePage(prefix + "properties-" + section, InspectorContent.Content as FrameworkElement);
                    }
                    Model.CloseInspector();
                    foreach (var section in Enum.GetValues<PreferenceSection>())
                    {
                        await ShowPreferences(new(section));
                        await CapturePage(prefix + "settings-" + section, _preferencesForm);
                        if (section == PreferenceSection.Schedule && Model.Preferences.Periods.FirstOrDefault() is { } period)
                        {
                            Model.Preferences.Edit(period);
                            await CaptureLayout();
                            await CaptureUi(prefix + "period-editor");
                            Run(Model.Preferences.CancelPeriod);
                        }
                    }
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
                    scope = "Real XAML views, cancelled dialogs and recovery smoke cases in a disposable store; no desktop input or capture"
                }));
            }
            await Model.CancelDraft();
            await CloseWindow(engineExit: false);
        }
    }
}
