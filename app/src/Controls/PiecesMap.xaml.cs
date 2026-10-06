using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
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
    private PieceKind[] _states = [];
    private Raster? _layout;
    private Strings? _text;
    private string _language = string.Empty;
    private string _value = string.Empty;
    private int _selected;
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
    private TextBlock[] Labels() => [UnavailableLabel, RareLabel, CommonLabel, MissingLabel, DownloadingLabel, VerifiedLabel];

    internal void Show(Pieces? data, Strings text)
    {
        var changed = _data is null || data is null || !_data.SameMap(data);
        var languageChanged = _language != text.Language;
        if (data is null || _data?.Count != data.Count)
        {
            _layout = null;
            Bitmap.Source = null;
            Selection.Visibility = Visibility.Collapsed;
            _tooltip.IsOpen = false;
        }
        _data = data;
        _text = text;
        _language = text.Language;
        if (changed) _states = data?.Classify() ?? [];
        if (changed || languageChanged)
        {
            Summary.Text = data?.Summary(text, _states) ?? text.Get("pieces", "metadata");
            var labels = Labels();
            foreach (var kind in Enum.GetValues<PieceKind>())
                labels[(int)kind].Text = text.Format("pieces", "count", text.Get("pieces", kind.ToString().ToLowerInvariant()), _states.Count(state => state == kind));
            PieceCount.Text = text.FormatCount("pieces", "size", data?.Count ?? 0, text.Bytes(data?.PieceSize ?? 0));
            MeasureLegend();
            AutomationProperties.SetName(this, text.Get("inspector", "pieces"));
            Select(_selected, _tooltip.IsOpen);
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
        Bitmap.Source = null;
        _layout = null;
        _tooltip.IsOpen = false;
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
            Bitmap.Source = null;
            _layout = null;
            _tooltip.IsOpen = false;
            Selection.Visibility = Visibility.Collapsed;
            return;
        }
        _drawing = true;
        var revision = _revision;
        try
        {
            var palette = Swatches().Select(swatch => ((SolidColorBrush)swatch.Background).Color)
                .Append(((SolidColorBrush)Ink.Foreground).Color).ToArray();
            palette[(int)PieceKind.Unavailable] = ((SolidColorBrush)UnavailableSwatch.BorderBrush).Color;
            var space = new Space(new Size(Viewport.ActualWidth, Viewport.ActualHeight),
                XamlRoot.RasterizationScale, FlowDirection == FlowDirection.RightToLeft);
            var states = _states;
            var layout = await Task.Run(() => Render(data, states, space, palette));
            if (revision != _revision || !IsLoaded) return;
            var bitmap = new WriteableBitmap(layout.PixelWidth, layout.PixelHeight);
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(layout.Pixels);
            bitmap.Invalidate();
            _layout = layout with { Pixels = [] };
            Drawing.Width = Bitmap.Width = layout.Width;
            Drawing.Height = Bitmap.Height = layout.Height;
            Bitmap.Source = bitmap;
            Select(Math.Min(_selected, layout.Blocks.Length - 1), _tooltip.IsOpen);
        }
        finally
        {
            _drawing = false;
            if (revision != _revision) QueueDraw();
        }
    }

    private static int Start(int position) => position * (Square + Gap) + position / Band * Gutter;
    private static int Extent(int count) => count == 0 ? 0 : Start(count - 1) + Square;

    private static Raster Render(Pieces data, PieceKind[] states, Space space, Color[] palette)
    {
        var scale = space.Scale;
        var rtl = space.IsRightToLeft;
        var ink = palette[^1];
        var maxColumns = 1;
        while (Extent(maxColumns + 1) <= space.Size.Width) maxColumns++;
        if (maxColumns >= Band) maxColumns = maxColumns / Band * Band;
        var maxRows = 1;
        while (Extent(maxRows + 1) <= space.Size.Height) maxRows++;
        var perBlock = Math.Max(1, (int)Math.Ceiling((double)data.Count / (maxColumns * maxRows)));
        var count = (data.Count + perBlock - 1) / perBlock;
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
            var first = index * perBlock;
            var end = Math.Min(data.Count, first + perBlock);
            var counts = new int[6];
            var received = 0.0;
            for (var piece = first; piece < end; piece++)
            {
                counts[(int)states[piece]]++;
                if (states[piece] == PieceKind.Verified) received++;
                else if (data.Downloading.TryGetValue(piece, out var progress)) received += progress;
            }
            var dominant = 0;
            for (var kind = 1; kind < counts.Length; kind++)
                if (counts[kind] > counts[dominant]) dominant = kind;
            var mixed = counts.Count(value => value > 0) > 1;
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
                    var dx = px / scale;
                    var dy = py / scale;
                    var border = dx < 1 || dy < 1 || dx >= Square - 1 || dy >= Square - 1;
                    var color = palette[dominant];
                    switch ((PieceKind)dominant)
                    {
                        case PieceKind.Downloading:
                            var filled = rtl ? dx >= Square * (1 - received / (end - first)) : dx < Square * received / (end - first);
                            color = filled ? palette[dominant] : palette[(int)PieceKind.Missing];
                            if (border) color = ink;
                            break;
                        case PieceKind.Common:
                            if (border || Math.Abs(dy - Square / 2.0) < 1) color = ink;
                            break;
                        case PieceKind.Rare:
                            if ((int)(dx + dy) % 6 == 0) color = ink;
                            break;
                        case PieceKind.Unavailable:
                            color = palette[(int)PieceKind.Missing];
                            if (border || Math.Abs(dx - dy) < 1 || Math.Abs(dx + dy - Square + 1) < 1) color = palette[(int)PieceKind.Unavailable];
                            break;
                        case PieceKind.Missing:
                            if (border) color = ink;
                            break;
                    }
                    if (mixed && dx >= Square - 4 && dy < dx - Square + 4) color = ink;
                    var offset = ((top + py) * pixelWidth + left + px) * 4;
                    pixels[offset] = (byte)(color.B * color.A / 255);
                    pixels[offset + 1] = (byte)(color.G * color.A / 255);
                    pixels[offset + 2] = (byte)(color.R * color.A / 255);
                    pixels[offset + 3] = color.A;
                }
        }
        return new Raster(space with { Size = new Size(mapWidth, mapHeight) }, columns, pixels, blocks);
    }

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (_layout is not { } layout) return;
        var position = args.GetCurrentPoint(Drawing).Position;
        var index = Array.FindIndex(layout.Blocks, block => position.X >= block.X && position.X < block.X + Square && position.Y >= block.Y && position.Y < block.Y + Square);
        if (index < 0) { _tooltip.IsOpen = false; return; }
        Select(index, true);
    }
    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        Focus(FocusState.Pointer);
        OnPointer(sender, args);
    }
    private void OnPointerExit(object sender, PointerRoutedEventArgs args) => _tooltip.IsOpen = false;
    private void OnFocus(object sender, RoutedEventArgs args) => Select(_selected, true);

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        if (_layout is not { } layout) return;
        var rtl = FlowDirection == FlowDirection.RightToLeft;
        var next = args.Key switch
        {
            VirtualKey.Left => _selected + (rtl ? 1 : -1),
            VirtualKey.Right => _selected + (rtl ? -1 : 1),
            VirtualKey.Up => _selected - layout.Columns,
            VirtualKey.Down => _selected + layout.Columns,
            VirtualKey.Home => 0,
            VirtualKey.End => layout.Blocks.Length - 1,
            _ => _selected
        };
        if (args.Key == VirtualKey.C && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            var package = new DataPackage();
            package.SetText(_value);
            Clipboard.SetContent(package);
            args.Handled = true;
            return;
        }
        if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)
        {
            Select(Math.Clamp(next, 0, layout.Blocks.Length - 1), true);
            args.Handled = true;
        }
    }

    private void Select(int index, bool tooltip)
    {
        var value = Summary.Text;
        if (_layout is { } layout && _data is { } data && _text is { } text && index < layout.Blocks.Length)
        {
            _selected = index;
            var block = layout.Blocks[index];
            value = data.Describe(text, block.First, block.End, _states);
            Selection.Margin = new Thickness(block.X - 2, block.Y - 2, 0, 0);
            Selection.Visibility = FocusState != FocusState.Unfocused ? Visibility.Visible : Visibility.Collapsed;
            _tooltip.Content = value;
            _tooltip.HorizontalOffset = block.X;
            _tooltip.VerticalOffset = block.Y + Square;
            _tooltip.IsOpen = tooltip;
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
    private sealed record Space(Size Size, double Scale, bool IsRightToLeft);
    private sealed record Raster(Space Space, int Columns, byte[] Pixels, Block[] Blocks)
    {
        public double Width => Space.Size.Width;
        public double Height => Space.Size.Height;
        public int PixelWidth => (int)Math.Ceiling(Width * Space.Scale);
        public int PixelHeight => (int)Math.Ceiling(Height * Space.Scale);
    }
}
