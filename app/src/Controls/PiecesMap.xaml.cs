using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Controls;

public sealed partial class PiecesMap : UserControl
{
    private const int Square = 16;
    private const int Gap = 4;
    private const int Band = 8;
    private const int Gutter = 6;
    private readonly ToolTip _tooltip = new();
    private Pieces? _data;
    private Raster? _layout;
    private Strings? _text;
    private string _language = string.Empty;
    private string _value = string.Empty;
    private int _selected;
    private int _pointed = -1;
    private int _tipped = -1;
    private int _revision;
    private bool _queued;
    private bool _drawing;
    private XamlRoot? _root;

    public PiecesMap()
    {
        InitializeComponent();
        _tooltip.PlacementTarget = Drawing;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => QueueDraw();
        LostFocus += (_, _) => { Selection.Visibility = Visibility.Collapsed; _tooltip.IsOpen = false; };
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) => QueueDraw());
        foreach (var swatch in Swatches())
            swatch.RegisterPropertyChangedCallback(Border.BackgroundProperty, (_, _) => QueueDraw());
        Ink.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => QueueDraw());
    }

    private Border[] Swatches() => [UnavailableSwatch, RareSwatch, CommonSwatch, MissingSwatch, DownloadingSwatch, VerifiedSwatch];
    private Run[] Labels() => [UnavailableLabel, RareLabel, CommonLabel, MissingLabel, DownloadingLabel, VerifiedLabel];
    private Run[] Totals() => [UnavailableCount, RareCount, CommonCount, MissingCount, DownloadingCount, VerifiedCount];

    internal void Show(Pieces? data, Strings text)
    {
        if (_data is null && data is null && _language == text.Language) return;
        var changed = _data is null || data is null || !_data.SameMap(data);
        var languageChanged = _language != text.Language;
        if (data is null || _data?.Count != data.Count) Clear();
        _data = data;
        _text = text;
        _language = text.Language;
        if (changed || languageChanged)
        {
            Summary.Text = data?.Summary(text) ?? text.Get("pieces", "metadata");
            var counts = data?.Counts(0, data.Count);
            var labels = Labels();
            var totals = Totals();
            foreach (var kind in Enum.GetValues<PieceKind>())
            {
                labels[(int)kind].Text = Pieces.Name(text, kind);
                totals[(int)kind].Text = (counts?[(int)kind] ?? 0).ToString("N0", CultureInfo.CurrentCulture);
            }
            PieceCount.Text = text.FormatCount("pieces", "size", data?.Count ?? 0, text.Bytes(data?.PieceSize ?? 0));
            MeasureLegend();
            AutomationProperties.SetName(this, text.Get("inspector", "pieces"));
            Refresh();
        }
        if (changed) QueueDraw();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        MeasureLegend();
        _root = XamlRoot;
        _tooltip.XamlRoot = _root;
        _root.Changed += OnRoot;
        QueueDraw();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (_root is { } root) root.Changed -= OnRoot;
        _root = null;
        _revision++;
        Clear();
    }
    private void OnRoot(XamlRoot sender, XamlRootChangedEventArgs args) => QueueDraw();
    private void OnSize(object sender, SizeChangedEventArgs args) => QueueDraw();

    private void MeasureLegend()
    {
        if (Legend.ItemsPanelRoot is not WrapGrid panel) return;
        var width = 0.0;
        var height = 0.0;
        foreach (FrameworkElement item in Legend.Items)
        {
            item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = Math.Max(width, item.DesiredSize.Width);
            height = Math.Max(height, item.DesiredSize.Height);
        }
        panel.ItemWidth = Math.Ceiling(width);
        panel.ItemHeight = Math.Ceiling(height);
    }

    private void QueueDraw()
    {
        _revision++;
        if (_queued) return;
        _queued = true;
        DispatcherQueue.TryEnqueue(() => { _queued = false; if (!_drawing) _ = Draw(); });
    }

    private async Task Draw()
    {
        if (!IsLoaded || _data is not { Count: > 0 } data || Viewport.ActualWidth < Square || Viewport.ActualHeight < Square)
        {
            Clear();
            return;
        }
        _drawing = true;
        var revision = _revision;
        try
        {
            var palette = new Palette
            {
                Fills = [.. Swatches().Select(swatch => Read(swatch.Background))],
                Received = Read(Received.Background),
                Hatch = Read(Hatch.Stroke),
                Cross = Read(Cross.Stroke),
                Ink = Read(Ink.Foreground),
                Outline = VerifiedSwatch.BorderThickness.Left > 0 ? Read(VerifiedSwatch.BorderBrush) : default
            };
            var radius = VerifiedSwatch.CornerRadius.TopLeft;
            Hover.CornerRadius = new CornerRadius(radius + 1);
            Selection.CornerRadius = new CornerRadius(radius + 2);
            var space = new Space(new Size(Viewport.ActualWidth, Viewport.ActualHeight),
                XamlRoot.RasterizationScale, FlowDirection == FlowDirection.RightToLeft, radius);
            var layout = await Task.Run(() => Render(data, space, palette));
            if (revision != _revision || !IsLoaded) return;
            var bitmap = new WriteableBitmap(layout.PixelWidth, layout.PixelHeight);
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(layout.Pixels);
            bitmap.Invalidate();
            _layout = layout with { Pixels = [] };
            Drawing.Width = Bitmap.Width = layout.Width;
            Drawing.Height = Bitmap.Height = layout.Height;
            Bitmap.Source = bitmap;
            _selected = Math.Min(_selected, layout.Blocks.Length - 1);
            Refresh();
        }
        finally
        {
            _drawing = false;
            if (revision != _revision) QueueDraw();
        }
    }

    private static int Start(int position) => position * (Square + Gap) + position / Band * Gutter;
    private static int Extent(int count) => count == 0 ? 0 : Start(count - 1) + Square;

    private static Raster Render(Pieces data, Space space, Palette palette)
    {
        var scale = space.Scale;
        var rtl = space.IsRightToLeft;
        var maxColumns = 1;
        while (Extent(maxColumns + 1) <= space.Size.Width) maxColumns++;
        if (maxColumns >= Band) maxColumns = maxColumns / Band * Band;
        var maxRows = 1;
        while (Extent(maxRows + 1) <= space.Size.Height) maxRows++;
        var count = Math.Min(data.Count, maxColumns * maxRows);
        var columns = Math.Min(maxColumns, count);
        if (columns >= Band) columns = Math.Min(maxColumns, (columns + Band - 1) / Band * Band);
        var rows = (count + columns - 1) / columns;
        var mapWidth = Extent(columns);
        var mapHeight = Extent(rows);
        var pixelWidth = (int)Math.Ceiling(mapWidth * scale);
        var pixelHeight = (int)Math.Ceiling(mapHeight * scale);
        var pixels = new byte[pixelWidth * pixelHeight * 4];
        var blocks = new Block[count];
        for (var index = 0; index < count; index++)
        {
            var first = (int)((long)index * data.Count / count);
            var end = (int)((long)(index + 1) * data.Count / count);
            var counts = data.Counts(first, end);
            var received = 0.0;
            for (var piece = first; piece < end; piece++)
                received += data.States[piece] == PieceKind.Verified ? 1 : data.Downloading.GetValueOrDefault(piece);
            var share = received / (end - first);
            var dominant = 0;
            for (var kind = 1; kind < counts.Length; kind++)
                if (counts[kind] > counts[dominant]) dominant = kind;
            var mixed = counts.Count(value => value > 0) > 1;
            var hidesUnavailable = counts[(int)PieceKind.Unavailable] > 0 && dominant != (int)PieceKind.Unavailable;
            var column = index % columns;
            if (rtl) column = columns - 1 - column;
            var x = Start(column);
            var y = Start(index / columns);
            blocks[index] = new Block(first, end, x, y);
            var left = (int)Math.Round(x * scale);
            var top = (int)Math.Round(y * scale);
            var side = Math.Max(1, (int)Math.Round(Square * scale));
            for (var py = 0; py < side && top + py < pixelHeight; py++)
                for (var px = 0; px < side && left + px < pixelWidth; px++)
                {
                    var dx = (px + 0.5) / scale;
                    var dy = (py + 0.5) / scale;
                    var edge = Edge(dx, dy, space.Radius);
                    var coverage = Math.Clamp(edge * scale + 0.5, 0, 1);
                    if (coverage <= 0) continue;
                    var color = (PieceKind)dominant switch
                    {
                        PieceKind.Downloading when (rtl ? dx >= Square * (1 - share) : dx < Square * share) => palette.Received,
                        PieceKind.Rare when Math.Abs((dx + dy) % 5 - 1) < 0.75 => palette.Hatch,
                        PieceKind.Unavailable when Math.Min(dx, dy) > 4 && Math.Max(dx, dy) < Square - 4
                            && (Math.Abs(dx - dy) < 0.75 || Math.Abs(dx + dy - Square) < 0.75) => palette.Cross,
                        _ => palette.Fills[dominant]
                    };
                    if (edge < 1 && palette.Outline.A > 0) color = palette.Outline;
                    if (mixed) color = Mix(color, palette.Ink, Dot(dx - (Square - 4), dy - 4, scale));
                    if (hidesUnavailable) color = Mix(color, palette.Cross, Dot(dx - 4, dy - (Square - 4), scale));
                    var alpha = color.A * coverage;
                    var offset = ((top + py) * pixelWidth + left + px) * 4;
                    pixels[offset] = (byte)(color.B * alpha / 255);
                    pixels[offset + 1] = (byte)(color.G * alpha / 255);
                    pixels[offset + 2] = (byte)(color.R * alpha / 255);
                    pixels[offset + 3] = (byte)alpha;
                }
        }
        return new Raster(space with { Size = new Size(mapWidth, mapHeight) }, columns, pixels, blocks);
    }

    // Distance in DIPs from a point inside the square to its rounded edge; negative outside.
    private static double Edge(double x, double y, double radius)
    {
        var cornerX = Math.Max(radius - x, x - (Square - radius));
        var cornerY = Math.Max(radius - y, y - (Square - radius));
        if (cornerX > 0 && cornerY > 0) return radius - Math.Sqrt(cornerX * cornerX + cornerY * cornerY);
        return Math.Min(Math.Min(x, Square - x), Math.Min(y, Square - y));
    }

    // Coverage of a 2-DIP-radius corner mark whose centre is the given offset away.
    private static double Dot(double x, double y, double scale) => Math.Clamp((2 - Math.Sqrt(x * x + y * y)) * scale + 0.5, 0, 1);

    private static Color Mix(Color under, Color over, double amount) => amount <= 0 ? under : Color.FromArgb(
        (byte)(under.A + (over.A - under.A) * amount), (byte)(under.R + (over.R - under.R) * amount),
        (byte)(under.G + (over.G - under.G) * amount), (byte)(under.B + (over.B - under.B) * amount));

    private static Color Read(Brush brush)
    {
        var solid = (SolidColorBrush)brush;
        var color = solid.Color;
        color.A = (byte)Math.Round(color.A * solid.Opacity);
        return color;
    }

    private int Find(PointerRoutedEventArgs args)
    {
        if (_layout is not { } layout) return -1;
        var position = args.GetCurrentPoint(Drawing).Position;
        return Array.FindIndex(layout.Blocks, block => position.X >= block.X && position.X < block.X + Square && position.Y >= block.Y && position.Y < block.Y + Square);
    }

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        var index = Find(args);
        PointAt(index);
        Tip(index);
    }
    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        Focus(FocusState.Pointer);
        var index = Find(args);
        if (index >= 0) Select(index);
        PointAt(index);
        Tip(index);
    }
    private void OnPointerExit(object sender, PointerRoutedEventArgs args)
    {
        PointAt(-1);
        _tooltip.IsOpen = false;
    }
    private void OnFocus(object sender, RoutedEventArgs args)
    {
        Select(_selected);
        Tip(_selected);
    }

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        if (_layout is not { } layout) return;
        if (args.Key == VirtualKey.C && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            var package = new DataPackage();
            package.SetText(_value);
            Clipboard.SetContent(package);
            args.Handled = true;
            return;
        }
        var rtl = FlowDirection == FlowDirection.RightToLeft;
        int? next = args.Key switch
        {
            VirtualKey.Left => _selected + (rtl ? 1 : -1),
            VirtualKey.Right => _selected + (rtl ? -1 : 1),
            VirtualKey.Up => _selected - layout.Columns,
            VirtualKey.Down => _selected + layout.Columns,
            VirtualKey.Home => 0,
            VirtualKey.End => layout.Blocks.Length - 1,
            _ => null
        };
        if (next is not { } index) return;
        Select(Math.Clamp(index, 0, layout.Blocks.Length - 1));
        Tip(_selected);
        args.Handled = true;
    }

    private void Clear()
    {
        _layout = null;
        Bitmap.Source = null;
        Selection.Visibility = Visibility.Collapsed;
        PointAt(-1);
        _tooltip.IsOpen = false;
    }

    private void Refresh()
    {
        Select(_selected);
        PointAt(_pointed);
        if (_tooltip.IsOpen) Tip(_tipped);
    }

    private Block? At(int index) =>
        _layout is { } layout && index >= 0 && index < layout.Blocks.Length ? layout.Blocks[index] : null;

    // Clear drops the layout whenever Show drops the data, and the text arrives with the first data.
    private string Describe(Block block) => _data!.Describe(_text!, block.First, block.End);

    private void PointAt(int index)
    {
        if (At(index) is not { } block)
        {
            _pointed = -1;
            Hover.Visibility = Visibility.Collapsed;
            return;
        }
        _pointed = index;
        Hover.Margin = new Thickness(block.X - 1, block.Y - 1, 0, 0);
        Hover.Visibility = Visibility.Visible;
    }

    private void Tip(int index)
    {
        if (At(index) is not { } block)
        {
            _tooltip.IsOpen = false;
            return;
        }
        _tipped = index;
        _tooltip.Content = Describe(block);
        _tooltip.HorizontalOffset = block.X;
        _tooltip.VerticalOffset = block.Y + Square;
        _tooltip.IsOpen = true;
    }

    private void Select(int index)
    {
        var value = Summary.Text;
        if (At(index) is { } block)
        {
            _selected = index;
            value = Describe(block);
            Selection.Margin = new Thickness(block.X - 2, block.Y - 2, 0, 0);
            Selection.Visibility = FocusState != FocusState.Unfocused ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_value == value) return;
        var previous = _value;
        _value = value;
        FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previous, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MapPeer(this);

    private sealed class MapPeer(PiecesMap map) : FrameworkElementAutomationPeer(map), IValueProvider
    {
        protected override string GetClassNameCore() => nameof(PiecesMap);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override object? GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.Value ? this : base.GetPatternCore(pattern);
        public bool IsReadOnly => true;
        public string Value => map._value;
        public void SetValue(string value) => throw new InvalidOperationException();
    }

    private sealed record Block(int First, int End, int X, int Y);
    private sealed record Space(Size Size, double Scale, bool IsRightToLeft, double Radius);
    private sealed record Palette
    {
        public required Color[] Fills { get; init; }
        public required Color Received { get; init; }
        public required Color Hatch { get; init; }
        public required Color Cross { get; init; }
        public required Color Ink { get; init; }
        // Transparent except in High Contrast, where system colours replace the fills and squares need an edge.
        public required Color Outline { get; init; }
    }
    private sealed record Raster(Space Space, int Columns, byte[] Pixels, Block[] Blocks)
    {
        public double Width => Space.Size.Width;
        public double Height => Space.Size.Height;
        public int PixelWidth => (int)Math.Ceiling(Width * Space.Scale);
        public int PixelHeight => (int)Math.Ceiling(Height * Space.Scale);
    }
}
