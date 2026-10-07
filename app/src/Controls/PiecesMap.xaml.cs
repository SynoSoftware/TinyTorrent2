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
    // The hatch and cross line width, matching the legend's StrokeThickness.
    private const double Stroke = 1.5;
    private Pieces? _data;
    private Raster? _layout;
    private Strings? _text;
    private string _language = string.Empty;
    private string _value = string.Empty;
    // A piece rather than a square, so the selection stays on the same pieces when a resize regroups them.
    private int _selectedPiece = -1;
    private int _pointed = -1;
    private int _revision;
    private bool _queued;
    private bool _drawing;
    private XamlRoot? _root;

    public PiecesMap()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => QueueDraw();
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) => QueueDraw());
        foreach (var swatch in Swatches())
            swatch.RegisterPropertyChangedCallback(Border.BackgroundProperty, (_, _) => QueueDraw());
        Ink.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => QueueDraw());
    }

    private Border[] Swatches() => [UnavailableSwatch, RareSwatch, CommonSwatch, MissingSwatch, DownloadingSwatch, VerifiedSwatch];
    private Run[] Labels() => [UnavailableLabel, RareLabel, CommonLabel, MissingLabel, DownloadingLabel, VerifiedLabel];
    private Run[] Totals() => [UnavailableCount, RareCount, CommonCount, MissingCount, DownloadingCount, VerifiedCount];
    private FontIcon[] Signs() => [InformationalSign, SuccessSign, WarningSign, ErrorSign];
    private StackPanel[] DetailEntries() => [DetailUnavailable, DetailRare, DetailCommon, DetailMissing, DetailDownloading, DetailVerified];
    private TextBlock[] DetailLabels() => [DetailUnavailableLabel, DetailRareLabel, DetailCommonLabel, DetailMissingLabel, DetailDownloadingLabel, DetailVerifiedLabel];

    internal void Show(Pieces? data, Strings text)
    {
        if (_data is null && data is null && _language == text.Language) return;
        var changed = _data is null || data is null || !_data.SameMap(data);
        var languageChanged = _language != text.Language;
        if (data is null || _data?.Count != data.Count)
        {
            _selectedPiece = -1;
            Clear();
        }
        _data = data;
        _text = text;
        _language = text.Language;
        if (changed || languageChanged)
        {
            var conclusion = Conclude(text);
            Answer.Text = conclusion.Answer;
            ToolTipService.SetToolTip(Status, conclusion.Reason);
            AutomationProperties.SetHelpText(Answer, conclusion.Reason);
            var signs = Signs();
            for (var index = 0; index < signs.Length; index++)
                signs[index].Visibility = index == (int)conclusion.Severity ? Visibility.Visible : Visibility.Collapsed;
            var counts = data?.Counts(0, data.Count);
            var labels = Labels();
            var totals = Totals();
            foreach (var kind in Enum.GetValues<PieceKind>())
            {
                labels[(int)kind].Text = Pieces.Name(text, kind);
                totals[(int)kind].Text = (counts?[(int)kind] ?? 0).ToString("N0", CultureInfo.CurrentCulture);
            }
            AutomationProperties.SetName(this, text.Get("inspector", "pieces"));
            Refresh();
        }
        if (changed) QueueDraw();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _root = XamlRoot;
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
        var maxRows = 1;
        while (Extent(maxRows + 1) <= space.Size.Height) maxRows++;
        var count = Math.Min(data.Count, maxColumns * maxRows);
        var columns = Math.Min(maxColumns, count);
        if (columns >= Band) columns = Math.Min(maxColumns, (columns + Band - 1) / Band * Band);
        var rows = (count + columns - 1) / columns;
        // A full row widens the gutters between groups by the width left over
        // after the last whole square, so the map ends at the right edge.
        var gutters = (columns - 1) / Band;
        var spare = count >= maxColumns && gutters > 0 ? (int)space.Size.Width - Extent(columns) : 0;
        int Left(int column) => Start(column) + (spare == 0 ? 0 : spare * (column / Band) / gutters);
        var mapWidth = Left(columns - 1) + Square;
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
            var x = Left(index % columns);
            // Mirror positions so a shorter last group stays at the reading end.
            if (rtl) x = mapWidth - x - Square;
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
                        PieceKind.Rare => Mix(palette.Fills[dominant], palette.Hatch, Cover(HatchDistance(dx, dy), Stroke / 2, scale)),
                        PieceKind.Unavailable => Mix(palette.Fills[dominant], palette.Cross, Cover(CrossDistance(dx, dy), Stroke / 2, scale)),
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
    private static double Dot(double x, double y, double scale) => Cover(Math.Sqrt(x * x + y * y), 2, scale);

    // Coverage of a pixel the given distance in DIPs from a mark's centre line or point, blending one
    // pixel across the mark's edge so marks are drawn as smooth as XAML shapes.
    private static double Cover(double distance, double radius, double scale) => Math.Clamp((radius - distance) * scale + 0.5, 0, 1);

    // Distance in DIPs to the nearest hatch line, the legend's lines x + y = 1 (mod 5).
    private static double HatchDistance(double x, double y)
    {
        var offset = ((x + y - 1) % 5 + 5) % 5;
        return Math.Min(offset, 5 - offset) / Math.Sqrt(2);
    }

    // Distance in DIPs to the legend's cross: both diagonals from 4 to Square - 4, with round ends.
    private static double CrossDistance(double x, double y)
    {
        // Signed distances from the two full diagonals; each is also the position along the other.
        var falling = (x - y) / Math.Sqrt(2);
        var rising = (x + y - Square) / Math.Sqrt(2);
        var half = (Square / 2 - 4) * Math.Sqrt(2);
        double Segment(double offset, double position) => Math.Sqrt(offset * offset + Math.Pow(Math.Max(0, Math.Abs(position) - half), 2));
        return Math.Min(Segment(falling, rising), Segment(rising, falling));
    }

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
        // A gap keeps the last square, so the detail does not flicker while the pointer crosses the map.
        var index = Find(args);
        if (index >= 0 && index != _pointed) PointAt(index);
    }
    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        Focus(FocusState.Pointer);
        var index = Find(args);
        if (index < 0) return;
        Select(index);
        PointAt(index);
    }
    private void OnPointerExit(object sender, PointerRoutedEventArgs args) => PointAt(-1);

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
        var current = Selected();
        int? next = args.Key switch
        {
            VirtualKey.Left => current + (rtl ? 1 : -1),
            VirtualKey.Right => current + (rtl ? -1 : 1),
            VirtualKey.Up => current >= layout.Columns ? current - layout.Columns : current,
            VirtualKey.Down => current / layout.Columns < (layout.Blocks.Length - 1) / layout.Columns ? current + layout.Columns : current,
            VirtualKey.Home => 0,
            VirtualKey.End => layout.Blocks.Length - 1,
            _ => null
        };
        if (next is not { } index) return;
        Select(current < 0 && args.Key != VirtualKey.End ? 0 : Math.Clamp(index, 0, layout.Blocks.Length - 1));
        // The key is the latest input, so the detail shows the selection until the pointer moves.
        PointAt(-1);
        args.Handled = true;
    }

    private void Clear()
    {
        _layout = null;
        Bitmap.Source = null;
        Refresh();
    }

    private void Refresh()
    {
        ShowCount();
        ShowSelection();
        PointAt(_pointed);
    }

    private void ShowCount()
    {
        if (_text is not { } text) return;
        if (_data is not { Count: > 0 } data)
        {
            PieceCount.Text = string.Empty;
            return;
        }
        var size = text.FormatCount("pieces", "size", data.Count, text.Bytes(data.PieceSize), data.Counts(0, data.Count)[(int)PieceKind.Verified]);
        if (_layout is not { } layout || layout.Blocks.Length == data.Count)
        {
            PieceCount.Text = size;
            return;
        }
        var fewest = data.Count / layout.Blocks.Length;
        var most = (data.Count + layout.Blocks.Length - 1) / layout.Blocks.Length;
        PieceCount.Text = text.Format("pieces", "detail", size,
            fewest == most ? text.Format("pieces", "square", most) : text.Format("pieces", "square_range", fewest, most));
    }

    private Block? At(int index) =>
        _layout is { } layout && index >= 0 && index < layout.Blocks.Length ? layout.Blocks[index] : null;

    private int Selected() => _selectedPiece < 0 || _layout is not { } layout ? -1 : Array.FindIndex(layout.Blocks, block => block.End > _selectedPiece);

    private Conclusion Conclude(Strings text) => _data?.Conclude(text) ?? Pieces.Waiting(text);

    // Clear drops the layout whenever Show drops the data.
    private PieceDetail Describe(Block block, Strings text) => _data!.Describe(text, block.First, block.End);

    private void PointAt(int index)
    {
        var block = At(index);
        _pointed = block is null ? -1 : index;
        Hover.Visibility = block is null ? Visibility.Collapsed : Visibility.Visible;
        if (block is not null) Hover.Margin = new Thickness(block.X - 1, block.Y - 1, 0, 0);
        ShowDetail();
    }

    private void Select(int index)
    {
        _selectedPiece = At(index)?.First ?? -1;
        ShowSelection();
    }

    private void ShowSelection()
    {
        var block = At(Selected());
        Selection.Visibility = block is null ? Visibility.Collapsed : Visibility.Visible;
        if (block is not null) Selection.Margin = new Thickness(block.X - 2, block.Y - 2, 0, 0);
        ShowDetail();
        var value = _text is not { } text ? string.Empty
            : block is null ? Conclude(text).Line(text)
            : Describe(block, text).Line(text);
        if (_value == value) return;
        var previous = _value;
        _value = value;
        FrameworkElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previous, value);
    }

    private void ShowDetail()
    {
        if (_text is not { } text) return;
        var block = At(_pointed) ?? At(Selected());
        Hint.Text = block is null && _layout is not null ? text.Get("pieces", "hint") : string.Empty;
        var detail = block is null ? null : Describe(block, text);
        DetailRange.Text = detail?.Range ?? string.Empty;
        DetailPeers.Text = detail?.Peers ?? string.Empty;
        DetailPeers.Visibility = detail?.Peers is null ? Visibility.Collapsed : Visibility.Visible;
        var entries = DetailEntries();
        var labels = DetailLabels();
        foreach (var kind in Enum.GetValues<PieceKind>())
        {
            var count = detail?.Counts[(int)kind] ?? 0;
            entries[(int)kind].Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            labels[(int)kind].Text = count > 0 ? Pieces.Label(text, kind, count) : string.Empty;
        }
        DetailFiles.Text = detail?.Files ?? string.Empty;
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
