using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private string? _placementPath;
    private Task? _placementRead;
    private Placement? _placement;
    private RectInt32 _normalBounds;
    private double _normalScale = 1;

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
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
            if (placement is null || _closing || _allowClose) return;
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

    private async Task SavePlacement()
    {
        if (_placementRead is { } reading) await reading;
        if (_placementPath is null) return;
        RememberBounds();
        var placement = new Placement
        {
            X = _normalBounds.X, Y = _normalBounds.Y, Width = _normalBounds.Width, Height = _normalBounds.Height,
            Scale = _normalScale, Maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized },
            SplitHeight = _splitHeight, Torrents = Torrents.Layout,
            Inspector = InspectorContent.Content is InspectorForm form ? form.Layout : _placement?.Inspector
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
    }
}
