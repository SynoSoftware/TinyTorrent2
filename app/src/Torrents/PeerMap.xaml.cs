using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Shared;
using Windows.Foundation;
using Windows.UI;
using static Syno.TinyTorrent.Torrents.MapModel;

namespace Syno.TinyTorrent.Torrents;

public sealed partial class PeerMap : UserControl
{
    private readonly MapModel _model;
    private readonly MapViewport _viewport = new();
    private Dictionary<string, Geography.Country>? _world;
    private MapRenderer? _renderer;
    private MapMotion? _motion;
    private bool _frameQueued;
    private bool _preparationQueued;
    private bool _drawQueued;
    // The colour of what the map's layer hides, or null until the next
    // picture works it out.
    private Color? _underlay;
    private string _language = string.Empty;
    private Destination? _selected;
    private IReadOnlyList<Peer> _selectedPeers = [];
    // Drawing reads the marks every frame, so they change only with the
    // selection, the destinations or the map detail.
    private Dictionary<PeerPlace, int> _marks = [];
    private Destination? _pointed;
    private long _dotBytes = MapMotion.SmallestDot;
    private MapAnimation _animation = MapAnimation.Normal;
    private MapDotTotal _dotTotal = MapDotTotal.Normal;
#if CAPTURE
    private double _preparation;
    internal Action<double>? FrameMeasured { get; set; }
    internal Action<double>? PreparationMeasured { get; set; }

    /// <summary>The frames the moving dots' layer has drawn.</summary>
    internal int MotionFrames => _motion?.Frames ?? 0;

    /// <summary>The CPU cycles the motion layer's thread has used.</summary>
    internal long MotionCycles => _motion?.Cycles ?? 0;

    /// <summary>The new pictures of the map the motion layer has shown.</summary>
    internal int MotionPictures => _motion?.Pictures ?? 0;

    /// <summary>Whether the moving dots' layer is paused, waiting for the next dot.</summary>
    internal bool MotionPaused => _motion?.Paused ?? true;

    /// <summary>What the moving dots' layer works from, for a capture report.</summary>
    internal string MotionState =>
        $"animates={MapRenderer.SystemAnimates} animation={_animation} origin={_model.OriginPoint is not null}"
        + $" {_motion?.State ?? "no layer"}";
#endif

    internal PeerMap(MapModel model)
    {
        _model = model;
        InitializeComponent();
        _navigationTimer = DispatcherQueue.CreateTimer();
        _navigationTimer.Interval = TimeSpan.FromSeconds(3);
        _navigationTimer.IsRepeating = false;
        _navigationTimer.Tick += (_, _) => Navigation.Visibility = Visibility.Collapsed;
        // Keyboard focus shows the Fit button, so Tab can reach it.
        GotFocus += (_, _) =>
        {
            if (FocusState == FocusState.Keyboard)
                ShowNavigation();
        };
        LostFocus += (_, _) => HoldNavigation();
        FitButton.RegisterPropertyChangedCallback(FocusStateProperty, (_, _) => HoldNavigation());
        Navigation.PointerEntered += (_, _) => _navigationTimer.Stop();
        Navigation.PointerExited += (_, _) => HoldNavigation();
        Loaded += OnLoaded;
        Unloaded += (_, _) => Stop();
        // A theme or High Contrast change also changes the swatches' brushes.
        ActualThemeChanged += (_, _) => Restyle();
        foreach (var swatch in new[] { Land, Edge, Download, Upload })
            swatch.RegisterPropertyChangedCallback(Border.BackgroundProperty, (_, _) => Restyle());
        Ink.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => Restyle());
        Drawing.SizeChanged += (_, _) => QueuePreparation();
        Drawing.PointerMoved += OnPointer;
        Drawing.PointerExited += (_, _) =>
        {
            if (_drag is { IsActive: true })
                return;
            EndDrag(false);
            if (_pointed is null)
                return;
            _pointed = null;
            Describe();
            Invalidate();
        };
        Drawing.PointerPressed += OnPressed;
        Drawing.PointerReleased += OnReleased;
        Drawing.PointerCaptureLost += (_, _) => EndDrag(true);
        Drawing.PointerCanceled += (_, _) => EndDrag(true);
        Drawing.PointerWheelChanged += OnWheel;
        Drawing.DoubleTapped += OnDoubleTapped;
        _viewport.Changed += (_, navigation) =>
        {
            if (_pointed is not null)
            {
                _pointed = null;
                Describe();
            }
            if (navigation)
                RefreshNavigation();
            Invalidate();
        };
        KeyDown += OnKey;
        ContextRequested += (_, _) => EndDrag(true);
        FitGlyph.FontFamily = Syno.Segoe.Font;
        FitGlyph.Glyph = Syno.Segoe.Maximize;
    }

    /// <summary>
    /// Raised when the person chooses a location on the map or clears the
    /// choice; the peers table shows only that place's peers.
    /// </summary>
    internal event EventHandler<PeerPlace?>? PlaceChanged;

    /// <summary>
    /// The bytes one moving dot stands for, or null while the map's animation
    /// or Windows animations are off or High Contrast is on. The legend shows
    /// it also while no data moves and before the map has loaded, so the Speed
    /// statistic never changes size on its own: the smallest size before the
    /// first, then the last.
    /// </summary>
    internal long? DotBytes =>
        MapRenderer.SystemAnimates && _animation != MapAnimation.Off ? _dotBytes : null;

    internal event EventHandler? DotBytesChanged;

    /// <summary>
    /// The place the speed legend describes: the one under the pointer, else
    /// the chosen one. Null when there is neither and while the map is dragged.
    /// </summary>
    internal Destination? Described => _drag is { IsActive: true } ? null : _pointed ?? _selected;

    /// <summary>Raised when <see cref="Described"/> or its speeds may have changed.</summary>
    internal event EventHandler? DescribedChanged;

    /// <summary>Sets how the map animates and how many moving dots it aims to show.</summary>
    internal void SetAnimation(MapAnimation animation, MapDotTotal total)
    {
        if (animation == _animation && total == _dotTotal)
            return;
        var shown = DotBytes;
        _animation = animation;
        _dotTotal = total;
        if (_renderer is { } renderer)
        {
            renderer.Animation = animation;
            renderer.DotTotal = total;
        }
        if (DotBytes != shown)
            DotBytesChanged?.Invoke(this, EventArgs.Empty);
        // The dots take the choice when the map is next prepared; a map that
        // stops animating draws without them at once.
        QueuePreparation();
        Invalidate();
    }

    /// <summary>
    /// Highlights the locations of the peers selected in the table. The location
    /// chosen on the map stays chosen.
    /// </summary>
    internal void Select(IReadOnlyList<Peer> peers)
    {
        if (_selectedPeers.SequenceEqual(peers) && _pointed is null)
            return;
        SelectPeers(peers);
        _pointed = null;
        Describe();
        Invalidate();
    }

    private void Choose(Destination? destination)
    {
        _selected = destination;
        PlaceChanged?.Invoke(this, destination?.Group.Place);
    }


    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MapModel.Place))
        {
            RefreshPlace();
            Describe();
            Invalidate();
            return;
        }
        QueuePreparation();
    }

    private void RefreshPlace()
    {
        if (_selected?.Group.Place != _model.Place)
            _selected = _model.Destinations.FirstOrDefault(destination => destination.Group.Place == _model.Place);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _model.PropertyChanged += OnModelChanged;
        RefreshNavigation();
        RefreshPlace();
        try
        {
            var world = await Geography.Load();
            if (!IsLoaded || _motion is not null)
                return;
            _world = world;
            var motion = new MapMotion(DispatcherQueue);
            _motion = motion;
            motion.Measured += (bytes, total) =>
            {
                _model.RaisePeak(total);
                if (bytes is not { } size || size == _dotBytes)
                    return;
                _dotBytes = size;
                DotBytesChanged?.Invoke(this, EventArgs.Empty);
            };
            motion.SurfaceChanged += (device, dpi) =>
            {
                if (_renderer is { } renderer)
                    renderer.UseDevice(device, dpi);
                else
                    _renderer = new MapRenderer(device, dpi, Invalidate, DispatcherQueue, motion, _model, _viewport, world)
                    {
                        Animation = _animation,
                        DotTotal = _dotTotal,
                    };
                QueuePreparation();
            };
            // A transparent background takes the pointer; the map's layer
            // ignores it.
            Drawing.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Drawing.Children.Add(motion.Control);
        }
        catch (Exception error)
            when (error is InvalidDataException or EndOfStreamException or InvalidOperationException)
        {
            var message = _model.Text?.Error(error) ?? error.Message;
            Failure.Text = message;
            Failure.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(this, message);
            AutomationProperties.SetHelpText(this, message);
        }
    }

    private void QueuePreparation()
    {
        _preparationQueued = true;
        QueueFrame();
    }

    private void Invalidate()
    {
        _drawQueued = true;
        QueueFrame();
    }

    // Data, layout and the view can change several times before a frame.
    // Prepare their final state once, without doing model work in Draw, then
    // draw it once.
    private void QueueFrame()
    {
        if (_frameQueued)
            return;
        _frameQueued = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        if (_preparationQueued)
        {
            _preparationQueued = false;
            Prepare();
        }
        if (_drawQueued)
        {
            _drawQueued = false;
            Draw();
        }
        else if (_renderer is { HasStaleBorders: true } renderer)
        {
#if CAPTURE
            var started = Stopwatch.GetTimestamp();
#endif
            renderer.StrokeBorders();
#if CAPTURE
            FrameMeasured?.Invoke(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
#endif
        }
        // A drawing that left work for the next frame queued it again, or
        // left border strokes to replace.
        if (_preparationQueued || _drawQueued || _renderer?.HasStaleBorders == true)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _frameQueued = false;
    }

    private void Restyle()
    {
        _underlay = null;
        Invalidate();
    }

    private void Prepare()
    {
        var size = new Size(Drawing.ActualWidth, Drawing.ActualHeight);
        if (_renderer is null || _world is null || _model.Text is not { } text || size.Width < 1 || size.Height < 1)
            return;
#if CAPTURE
        var started = Stopwatch.GetTimestamp();
#endif
        var resized = _viewport.Size != size;
        if (resized)
        {
            EndDrag(true);
            _viewport.Resize(size);
        }
        var highlight = Highlight;
        var pointed = _pointed?.Group.Place.Cities == _model.Cities ? _pointed?.Key : null;
        _model.Arrange(_world, _viewport.Scale);
        var changed = _renderer.Prepare();
        RefreshPlace();
        _pointed = _model.Destinations.FirstOrDefault(destination => destination.Key == pointed);
        if (_selectedPeers.Count > 0)
        {
            var present = _model.Peers.ToHashSet();
            _selectedPeers = _selectedPeers.Where(present.Contains).ToArray();
        }
        // Highlights compare their marks by instance, so equal marks keep the
        // old one and a rate update that changes nothing draws nothing.
        var marks = _model.Marks(_selectedPeers);
        if (marks.Count != _marks.Count || marks.Any(mark => _marks.GetValueOrDefault(mark.Key) != mark.Value))
            _marks = marks;
        if (_language != text.Language)
        {
            _language = text.Language;
            AutomationProperties.SetName(this, text.Get("peer_map", "name"));
            RefreshNavigation();
        }
        Describe();
        // A rate update that changes nothing visible draws nothing; the dots
        // move over the last picture.
        if (changed || resized || Highlight != highlight)
            Invalidate();
#if CAPTURE
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _preparation += elapsed;
        PreparationMeasured?.Invoke(elapsed);
#endif
    }

    private void Stop()
    {
        _model.PropertyChanged -= OnModelChanged;
        CompositionTarget.Rendering -= OnRendering;
        _frameQueued = false;
        _preparationQueued = false;
        _drawQueued = false;
        _underlay = null;
        EndDrag(true);
        _navigationTimer.Stop();
        Navigation.Visibility = Visibility.Collapsed;
        ContextFlyout?.Hide();
        _renderer?.Dispose();
        _renderer = null;
        _motion?.Dispose();
        _motion = null;
        _world = null;
        Drawing.Background = null;
        _pointed = null;
        DescribedChanged?.Invoke(this, EventArgs.Empty);
#if CAPTURE
        _preparation = 0;
#endif
    }

    private MapRenderer.Highlight Highlight => new(_selected?.Key, _marks, _pointed?.Key);

    private bool IsMarked(Destination destination) => Highlight.IsMarked(destination);

    private void Draw()
    {
        if (_model.Text is null || _renderer is not { } renderer || _motion is not { } motion || _viewport.ViewScale <= 0)
            return;
#if CAPTURE
        var started = Stopwatch.GetTimestamp();
#endif
        var underlay = _underlay ??= Underlay();
        var palette = new MapRenderer.Palette
        {
            Ink = Colour(Ink.Foreground),
            Land = Colour(Land.Background),
            Edge = Colour(Edge.Background),
            Download = Colour(Download.Background),
            Upload = Colour(Upload.Background),
        };
        var highlight = Highlight;
        motion.Draw(_viewport.View, session =>
        {
            session.Clear(underlay);
            return renderer.Draw(session, palette, highlight);
        });
#if CAPTURE
        // Include preparation moved out of Draw so before/after CPU totals remain comparable.
        FrameMeasured?.Invoke(_preparation + Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        _preparation = 0;
#endif
    }

    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;

    // The map's layer hides everything under it, so each picture starts with
    // the backgrounds between the window's backdrop and the map, composed as
    // XAML draws them, and the map matches the peers table beside it. Only
    // solid backgrounds count; no other kind lies under the map.
    private Color Underlay()
    {
        List<Color> layers = [];
        for (var element = VisualTreeHelper.GetParent(this); element is not null; element = VisualTreeHelper.GetParent(element))
        {
            var brush = element switch
            {
                Panel panel => panel.Background,
                Border border => border.Background,
                ContentPresenter presenter => presenter.Background,
                _ => null,
            };
            if (brush is SolidColorBrush solid)
                layers.Add(MapRenderer.Alpha(solid.Color, solid.Opacity));
        }
        // Premultiplied, from the window's root down.
        double alpha = 0, red = 0, green = 0, blue = 0;
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            var layer = layers[index];
            var cover = layer.A / 255.0;
            red = layer.R * cover + red * (1 - cover);
            green = layer.G * cover + green * (1 - cover);
            blue = layer.B * cover + blue * (1 - cover);
            alpha = cover + alpha * (1 - cover);
        }
        return alpha == 0
            ? Microsoft.UI.Colors.Transparent
            : Color.FromArgb(
                (byte)Math.Round(alpha * 255),
                (byte)Math.Round(red / alpha),
                (byte)Math.Round(green / alpha),
                (byte)Math.Round(blue / alpha)
            );
    }

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (_renderer is null || MoveDrag(args) || _viewport.ViewScale <= 0)
            return;
        var position = args.GetCurrentPoint(Drawing).Position;
        var nearest = _renderer.HitTest(new Vector2((float)position.X, (float)position.Y));
        if (ReferenceEquals(_pointed, nearest))
            return;
        _pointed = nearest;
        Describe();
        Invalidate();
    }

    private void Describe()
    {
        if (_model.Text is not { } text)
            return;
        AutomationProperties.SetHelpText(this, _model.Describe(Inspected) + " " + text.Get("peer_map", "keyboard"));
        DescribedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The peers selected in the table; their places are marked.</summary>
    private void SelectPeers(IReadOnlyList<Peer> peers)
    {
        _selectedPeers = peers;
        _marks = _model.Marks(peers);
    }

}
