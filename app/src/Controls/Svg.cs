using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Syno.TinyTorrent.Controls;

/// <summary>
/// Rasterizes an SVG at the size its host displays. WinUI rasterizes an SVG
/// whose host has no arranged bounds when the source is decoded, such as a
/// collapsed image, at window size, and keeps that bitmap for the source's life.
/// </summary>
internal static class Svg
{
    /// <summary>
    /// Keeps the host's SVG source rasterized at the host's Width and Height at
    /// the display scale, through source replacement and scale changes while the
    /// host is loaded. The host sets both dimensions.
    /// </summary>
    internal static void Fit(Image host) => Fit(host, Image.SourceProperty);

    /// <inheritdoc cref="Fit(Image)"/>
    internal static void Fit(ImageIcon host) => Fit(host, ImageIcon.SourceProperty);

    private static void Fit(FrameworkElement host, DependencyProperty source)
    {
        XamlRoot? root = null;
        host.RegisterPropertyChangedCallback(source, (_, _) => Apply());
        host.Loaded += (_, _) =>
        {
            root = host.XamlRoot;
            root.Changed += OnRoot;
            Apply();
        };
        host.Unloaded += (_, _) =>
        {
            if (root is { } loaded)
                loaded.Changed -= OnRoot;
            root = null;
        };
        Apply();

        void OnRoot(XamlRoot sender, XamlRootChangedEventArgs args) => Apply();

        void Apply()
        {
            if (host.GetValue(source) is not SvgImageSource svg)
                return;
            // The runtime reads these as physical pixels, not the logical pixels
            // the documentation describes.
            var scale = host.XamlRoot?.RasterizationScale ?? 1;
            svg.RasterizePixelWidth = host.Width * scale;
            svg.RasterizePixelHeight = host.Height * scale;
        }
    }
}
