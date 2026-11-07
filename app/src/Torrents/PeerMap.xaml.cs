using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
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
    private MapWork? _work;
    private MapWork? _working;
    private Presentation? _presentation;
    private CanvasDevice? _device;
    private long _version;
    private object? _loading;
    private TaskCompletionSource? _ready;
    private (MapWork.Input Input, CancellationTokenSource Cancellation)? _preparing;
    private MapMotion? _motion;
    private bool _frameQueued;
    private bool _preparationQueued;
    // The colour of what the map's layer hides, or null until the next
    // picture works it out.
    private Color? _underlay;
    private string _language = string.Empty;
    private Destination? _selected;
    private IReadOnlyList<Peer> _selectedPeers = [];
    private (Peer Peer, Location? Location)[] _selection = [];
    private Destination? _pointed;
    private long _dotBytes = MapMotion.SmallestDot;
    private MapAnimation _animation = MapAnimation.Normal;
    private MapDotTotal _dotTotal = MapDotTotal.Normal;
    private int _arcLimit = MapRenderer.DefaultLimit;
#if CAPTURE
    internal Action<double>? FrameMeasured { get; set; }
    internal Action<double>? PreparationMeasured { get; set; }
    internal Action<string, double>? WorkMeasured { get; set; }

    internal Action<string, int>? CaptureInput { get; set; }

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
            _motion?.Navigate(_viewport.View);
            if (_pointed is not null)
            {
                _pointed = null;
                Describe();
                Invalidate();
            }
            if (navigation)
                RefreshNavigation();
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
    internal Destination? Described => _drag is not { IsActive: true } && Displayed is { } displayed
        ? Resolve(displayed, _pointed) ?? Chosen(displayed) : null;

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
        if (DotBytes != shown)
            DotBytesChanged?.Invoke(this, EventArgs.Empty);
        // The dots take the choice when the map is next prepared; a map that
        // stops animating draws without them at once.
        Invalidate();
    }

    internal void SetArcLimit(int limit)
    {
        if (_arcLimit == limit)
            return;
        _arcLimit = limit;
        Invalidate();
    }

    /// <summary>
    /// Highlights the locations of the peers selected in the table. The location
    /// chosen on the map stays chosen.
    /// </summary>
    internal void Select(IReadOnlyList<Peer> peers)
    {
        if (ReferenceEquals(_selectedPeers, peers) && _pointed is null)
            return;
        SelectPeers(peers);
        _pointed = null;
        Describe();
        Invalidate();
    }

    private void Choose(Destination? destination, MapMotion.Displayed? displayed)
    {
        if (displayed is null || displayed.Inspection.Subject != _model.Subject
            || displayed.Inspection.Cities != _model.Cities || !IsLoaded)
            return;
        var chosen = Resolve(displayed, destination);
        if (destination is not null && chosen is null)
            return;
        _selected = chosen;
        PlaceChanged?.Invoke(this, _selected?.Group.Place);
    }


    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MapModel.Place))
        {
            if (_selected?.Group.Place != _model.Place)
                _selected = null;
            Invalidate();
            return;
        }
        CaptureSelection();
        if (_presentation is { } presentation && presentation.Subject != _model.Subject)
        {
            _selected = _pointed = null;
            SelectPeers([]);
            _motion?.CancelDrawing();
        }
        else if (_presentation is { } current && current.Cities != _model.Cities)
        {
            _pointed = null;
            _motion?.CancelDrawing();
        }
        QueuePreparation();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        var loading = new object();
        _loading = loading;
        _model.PropertyChanged += OnModelChanged;
        WatchSettings();
        RefreshNavigation();
        try
        {
            var world = await Geography.Load();
            if (!IsLoaded || !ReferenceEquals(_loading, loading) || _motion is not null)
                return;
            _work = new(world);
            var motion = new MapMotion(DispatcherQueue, world);
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
                _device = device;
                QueuePreparation();
            };
            motion.DisplayChanged += () => Describe();
#if CAPTURE
            motion.FrameMeasured += milliseconds => FrameMeasured?.Invoke(milliseconds);
            motion.WorkMeasured += (phase, milliseconds) => WorkMeasured?.Invoke(phase, milliseconds);
#endif
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

    internal Task Ready => WaitForReady();

    private async Task WaitForReady()
    {
        await (_ready?.Task ?? Task.CompletedTask);
        if (_motion is { } motion)
            await motion.Ready;
    }

    private void QueuePreparation()
    {
        _preparationQueued = true;
        _version++;
        if (_preparing is { } preparing
            && (preparing.Input.Model.Subject != _model.Subject || preparing.Input.Model.Cities != _model.Cities))
            _ = preparing.Cancellation.CancelAsync();
        QueueFrame();
    }

    private void Invalidate() => Publish();

    private void QueueFrame()
    {
        if (!IsLoaded || _work is null)
            return;
        if (_ready?.Task.IsCompleted != false)
            _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_frameQueued || _working is not null)
            return;
        _frameQueued = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnRendering;
        _frameQueued = false;
        if (_working is null && _work is { } work)
            Prepare(work);
    }

    private MapWork.Input? Capture()
    {
        var size = new Size(Drawing.ActualWidth, Drawing.ActualHeight);
        if (_device is null || _model.Text is null || size.Width < 1 || size.Height < 1)
            return null;
        if (_viewport.Size != size)
        {
            EndDrag(true);
            _viewport.Resize(size);
            _preparationQueued = true;
        }
        if (_viewport.ViewScale <= 0)
            return null;
        var snapshot = _model.Capture();
        if (!ReferenceEquals(snapshot.Subject, _model.Subject))
            return null;
        return new()
        {
            Model = snapshot,
            Scale = _viewport.Scale,
        };
    }

    private async void Prepare(MapWork work)
    {
        _working = work;
        try
        {
            while (ReferenceEquals(_work, work) && IsLoaded)
            {
                _preparing = null;
                var input = Capture();
                if (input is null)
                    return;
                using var cancellation = new CancellationTokenSource();
                input = input with { Cancellation = cancellation.Token };
                _preparing = (input, cancellation);
                var version = _version;
                _preparationQueued = false;
                var preparation = Task.Run(() => work.Prepare(input));
                await ((Task)preparation).ConfigureAwait(
                    ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                if (!ReferenceEquals(_work, work) || !IsLoaded)
                    return;
                if (preparation.IsCanceled || input.Model.Subject != _model.Subject || input.Model.Cities != _model.Cities)
                {
                    continue;
                }
                var prepared = await preparation;
                Apply(prepared);
                if (version != _version)
                    continue;
                _ready?.TrySetResult();
                return;
            }
        }
        catch (Exception error)
        {
            _ready?.TrySetException(error);
            throw;
        }
        finally
        {
            _preparing = null;
            _working = null;
            if (!ReferenceEquals(_work, work))
                _ = Task.Run(work.Dispose);
            if (_work is not null && _preparationQueued)
                QueueFrame();
        }
    }

    private void Apply(MapWork.Output prepared)
    {
#if CAPTURE
        var started = Stopwatch.GetTimestamp();
#endif
        _model.Apply(prepared.Model);
        _presentation = prepared.Model;
        _viewport.Meridian = prepared.Model.Meridian;
        if (_model.Text is { } text && _language != text.Language)
        {
            _language = text.Language;
            AutomationProperties.SetName(this, text.Get("peer_map", "name"));
            RefreshNavigation();
        }
        Publish();
#if CAPTURE
        WorkMeasured?.Invoke("arrange", prepared.PreparationMs);
        PreparationMeasured?.Invoke(prepared.PreparationMs);
        WorkMeasured?.Invoke("publish", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
#endif
    }

    private void Publish()
    {
        if (_motion is not { } motion || _presentation is not { } presentation
            || presentation.Subject != _model.Subject || _viewport.ViewScale <= 0)
            return;
        motion.Navigate(_viewport.View);
        motion.Show(new MapMotion.Input
        {
            Model = presentation,
            Palette = new()
            {
                Ink = Colour(Ink.Foreground),
                Land = Colour(Land.Background),
                Edge = Colour(Edge.Background),
                Download = Colour(Download.Background),
                Upload = Colour(Upload.Background),
            },
            Selection = _selection,
            Chosen = _selected?.Key,
            Pointed = _pointed?.Key,
            Place = _model.Place,
            Background = _underlay ??= Underlay(),
            Animation = _animation,
            DotTotal = _dotTotal,
            ArcLimit = _arcLimit,
            Animates = MapRenderer.SystemAnimates,
            HighContrast = MapRenderer.SystemHighContrast,
        });
    }

    private void Restyle()
    {
        _underlay = null;
        Invalidate();
    }

    private void Stop()
    {
        _model.PropertyChanged -= OnModelChanged;
        StopSettings();
        CompositionTarget.Rendering -= OnRendering;
        _frameQueued = false;
        _preparationQueued = false;
        _version++;
        _ = _preparing?.Cancellation.CancelAsync();
        _loading = null;
        _ready?.TrySetCanceled();
        _underlay = null;
        EndDrag(true);
        _navigationTimer.Stop();
        Navigation.Visibility = Visibility.Collapsed;
        var work = _work;
        _work = null;
        _presentation = null;
        if (work is not null && !ReferenceEquals(work, _working))
            _ = Task.Run(work.Dispose);
        _motion?.Dispose();
        _motion = null;
        _device = null;
        Drawing.Background = null;
        _pointed = null;
        DescribedChanged?.Invoke(this, EventArgs.Empty);
    }

    private MapRenderer.Highlight Highlight(MapMotion.Displayed displayed) =>
        new(Chosen(displayed)?.Key, displayed.Inspection.Highlight.Places, _pointed?.Key);

    private static Destination? Resolve(MapMotion.Displayed displayed, Destination? destination) =>
        destination is null ? null : displayed.Inspection.Destinations.FirstOrDefault(current => current.Key == destination.Key);

    private Destination? Chosen(MapMotion.Displayed displayed) => _model.Place is { } place
        ? displayed.Inspection.Destinations.FirstOrDefault(destination => destination.Key == _selected?.Key && destination.Group.Place == place)
            ?? displayed.Inspection.Destinations.FirstOrDefault(destination => destination.Group.Place == place)
        : null;


    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;

    // The map's layer hides everything under it, so each frame starts with
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
        if (MoveDrag(args) || Displayed is not { } displayed || _viewport.ViewScale <= 0)
            return;
        PointAt(args, displayed);
    }

    private void PointAt(PointerRoutedEventArgs args, MapMotion.Displayed displayed)
    {
        var position = args.GetCurrentPoint(Drawing).Position;
        var nearest = displayed.Inspection.HitTest(new Vector2((float)position.X, (float)position.Y), displayed.View);
        if (ReferenceEquals(_pointed, nearest))
            return;
        _pointed = nearest;
        Describe(displayed);
        Invalidate();
    }

    private MapMotion.Displayed? Displayed => IsLoaded && _motion?.Display is { } displayed
        && displayed.Inspection.Subject == _model.Subject && displayed.Inspection.Cities == _model.Cities
        ? displayed : null;

    private void Describe(MapMotion.Displayed? displayed = null)
    {
        if (_model.Text is not { } text)
            return;
        displayed ??= Displayed;
        AutomationProperties.SetHelpText(this, _model.Describe(Inspected(displayed)) + " " + text.Get("peer_map", "keyboard"));
        DescribedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The peers selected in the table; their places are marked.</summary>
    private void SelectPeers(IReadOnlyList<Peer> peers)
    {
        _selectedPeers = peers;
        CaptureSelection();
    }

    private void CaptureSelection()
    {
        var changed = _selection.Length != _selectedPeers.Count;
        for (var index = 0; !changed && index < _selection.Length; index++)
            changed = !ReferenceEquals(_selection[index].Peer, _selectedPeers[index])
                || _selection[index].Location != _selectedPeers[index].Location;
        if (changed)
            _selection = _selectedPeers.Select(peer => (peer, peer.Location)).ToArray();
    }

}
