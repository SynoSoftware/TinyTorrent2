using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private string? _captureDirectory;
    private Task? _capture;

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
        var shortcut = new KeyboardAccelerator { Key = VirtualKey.F12, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
        shortcut.Invoked += (_, args) =>
        {
            if (_capture is not { IsCompleted: false }) _capture = CaptureUi();
            args.Handled = true;
        };
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Root.KeyboardAccelerators.Add(shortcut);
    }

    private async Task CaptureUi()
    {
        if (_captureDirectory is null || Root.XamlRoot is null) return;
        var clock = Stopwatch.StartNew();
        try
        {
            var directory = Path.Combine(_captureDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var scale = Root.XamlRoot.RasterizationScale;
            var scene = new List<FrameworkElement> { Root };
            scene.AddRange(VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                .Select(popup => popup.Child).OfType<FrameworkElement>());
            var frames = new List<object>();
            foreach (var element in scene)
            {
                if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(element, (int)Math.Ceiling(element.ActualWidth * scale),
                    (int)Math.Ceiling(element.ActualHeight * scale));
                var pixels = await bitmap.GetPixelsAsync();
                var name = frames.Count == 0 ? "window.png" : $"popup-{frames.Count}.png";
                var file = await folder.CreateFileAsync(name, CreationCollisionOption.FailIfExists);
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels.ToArray());
                await encoder.FlushAsync();
                frames.Add(new { file = name, width = bitmap.PixelWidth, height = bitmap.PixelHeight });
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "capture.json"), JsonSerializer.Serialize(new
            {
                scope = "XAML visuals; native chrome, system dialogs and desktop acrylic are not captured",
                page = Model.Page.ToString(), language = Model.Text.Language, theme = Root.ActualTheme.ToString(),
                scale, milliseconds = clock.ElapsedMilliseconds, frames
            }));
            Debug.WriteLine($"UI capture saved to {directory} in {clock.ElapsedMilliseconds} ms.");
        }
        catch (Exception error) { Debug.WriteLine($"UI capture did not complete: {error.Message}"); }
    }
}
