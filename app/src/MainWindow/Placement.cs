using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private string? _placementPath;
    private Task? _placementRead;
    private Placement? _placement;
    private RectInt32 _normalBounds;
    private double _normalScale = 1;
    // The window stays cloaked until its first complete usable frame, in its
    // saved place, theme and language, has rendered; it never appears empty or
    // jumps into position. A failed connection shows it at once, with the
    // failure.
    private bool _cloaked;
    private const int CloakAttribute = 13;
    // The saved view is restored once, at the first snapshot whose storage
    // loaded; a reconnect keeps the view the person has now. Until then the
    // window holds only defaults, so Close saves nothing rather than overwrite
    // the saved file.
    private bool _viewRestored;

    private void SetCloak(bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        var result = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), CloakAttribute, ref value, sizeof(int));
        _cloaked = cloaked && result == 0;
    }

    private async Task ShowWhenReady()
    {
        StartPlacement();
        // A failed restore or language load still shows the window, with the
        // defaults, rather than keeping the splash waiting.
        if (_placementRead is { } reading) await Task.WhenAny(reading);
        await RestoreView();
        await Task.WhenAny(Model.LanguageLoad);
        await Rendered();
        var ready = await Model.Ready();
        Reveal();
        if (!ready) return;
        // The window is on screen, so the engine closes its splash.
        if (!IsCaptureReview) await Model.Activated(true);
        await Model.ReceiveSources();
    }

    private void Reveal()
    {
        if (!_cloaked) return;
        SetCloak(false);
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private static Task Rendered()
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            CompositionTarget.Rendering -= handler;
            rendered.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        return rendered.Task;
    }

    private void StartPlacement()
    {
        if (!IsCaptureReview && _placementPath is null && Model.DataDirectory is { } directory) _placementRead = RestorePlacement(directory);
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_allowClose) return;
        RememberBounds();
        UpdateChrome();
    }

    private void RememberBounds()
    {
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
        _normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _normalScale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
    }

    private async Task RestorePlacement(string directory)
    {
        _placementPath = Path.Combine(directory, "window.json");
        try
        {
            var input = await File.ReadAllTextAsync(_placementPath);
            var placement = JsonSerializer.Deserialize<Placement>(input);
            if (placement is null || Model.IsClosing || _allowClose) return;
            _placement = placement;
            if (placement.Width > 0 && placement.Height > 0 && double.IsFinite(placement.Scale) && placement.Scale > 0)
            {
                var display = DisplayArea.GetFromRect(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height), DisplayAreaFallback.Nearest);
                var area = display.WorkArea;
                AppWindow.Move(new PointInt32(Math.Clamp(placement.X, area.X, area.X + Math.Max(0, area.Width - 720)),
                    Math.Clamp(placement.Y, area.Y, area.Y + Math.Max(0, area.Height - 560))));
                var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
                var width = (int)Math.Clamp(placement.Width / placement.Scale * scale, Math.Min(720 * scale, area.Width), area.Width);
                var height = (int)Math.Clamp(placement.Height / placement.Scale * scale, Math.Min(560 * scale, area.Height), area.Height);
                AppWindow.MoveAndResize(new RectInt32(Math.Clamp(placement.X, area.X, area.X + area.Width - width),
                    Math.Clamp(placement.Y, area.Y, area.Y + area.Height - height), width, height));
                RememberBounds();
                if (placement.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
            }
            if (placement.Torrents is { } torrents) Torrents.Layout = torrents;
            if (placement.Inspector is { } inspector && InspectorContent.Content is InspectorForm form) form.Layout = inspector;
            if (double.IsFinite(placement.SplitHeight) && placement.SplitHeight >= 300) _splitHeight = placement.SplitHeight;
            UpdateInspectorSize();
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Window layout could not be restored: {error.Message}");
        }
    }

    private async Task RestoreView()
    {
        if (_viewRestored || Model.IsClosing || Model.IsStorageFailed) return;
        _viewRestored = true;
        if (_placement is not { } placement) return;
        if (!Enum.IsDefined(placement.Page) || !Enum.IsDefined(placement.Filter) ||
            !Enum.IsDefined(placement.Section) || !Enum.IsDefined(placement.Settings)) return;
        Model.Filter = placement.Filter;
        Model.IsFilterOpen = placement.FiltersOpen;
        var identities = new HashSet<string>(placement.Selected ?? []);
        var selected = Model.VisibleTorrents.Where(torrent => identities.Contains(torrent.TorrentId)).ToArray();
        var current = selected.FirstOrDefault(torrent => torrent.TorrentId == placement.Current) ?? selected.FirstOrDefault();
        Torrents.Selection = new Syno.TableView.Selection(selected, current);
        Model.Inspector.Select(placement.Section);
        if (placement.InspectorOpen) Run(Model.Properties);
        // After the inspector, which sets how many rows fit, and before another
        // page hides the table.
        Torrents.ScrollTo(placement.HorizontalOffset, placement.VerticalOffset);
        if (placement.Page == WindowPage.Preferences) await ShowPreferences(new(placement.Settings));
        else if (placement.Page == WindowPage.About) await ShowAbout();
    }

    private async Task SavePlacement()
    {
        if (_placementRead is { } reading) await reading;
        if (_placementPath is null || !_viewRestored) return;
        RememberBounds();
        var placement = new Placement
        {
            X = _normalBounds.X, Y = _normalBounds.Y, Width = _normalBounds.Width, Height = _normalBounds.Height,
            Scale = _normalScale, Maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized },
            SplitHeight = _splitHeight, Torrents = Torrents.Layout,
            Inspector = InspectorContent.Content is InspectorForm form ? form.Layout : _placement?.Inspector,
            Page = Model.Page, Settings = _preferencesForm?.Section ?? PreferenceSection.General,
            Filter = Model.Filter, FiltersOpen = Model.IsFilterOpen,
            Selected = [.. _selection.Items.Cast<Torrent>().Select(torrent => torrent.TorrentId)],
            Current = (_selection.Current as Torrent)?.TorrentId,
            InspectorOpen = Model.HasInspector, Section = Model.Inspector.Section,
            HorizontalOffset = Torrents.HorizontalOffset, VerticalOffset = Torrents.VerticalOffset
        };
        var temporary = _placementPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(placement));
            File.Move(temporary, _placementPath, true);
            _placement = placement;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Window layout could not be saved: {error.Message}");
        }
    }

    private sealed record Placement
    {
        public int X { get; init; }
        public int Y { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double Scale { get; init; } = 1;
        public bool Maximized { get; init; }
        public double SplitHeight { get; init; } = 360;
        public Syno.TableView.ColumnLayout? Torrents { get; init; }
        public InspectorLayout? Inspector { get; init; }
        public WindowPage Page { get; init; }
        public PreferenceSection Settings { get; init; }
        public TorrentFilter Filter { get; init; }
        public bool FiltersOpen { get; init; }
        public IReadOnlyList<string>? Selected { get; init; }
        public string? Current { get; init; }
        public bool InspectorOpen { get; init; }
        public InspectorSection Section { get; init; }
        public double HorizontalOffset { get; init; }
        public double VerticalOffset { get; init; }
    }
}
