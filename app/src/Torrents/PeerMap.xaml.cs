using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Shared;
using Windows.UI;
using static Syno.TinyTorrent.Torrents.MapModel;

namespace Syno.TinyTorrent.Torrents;

public sealed partial class PeerMap : UserControl
{
    private readonly MapModel _model;
    private readonly MapViewport _viewport = new();
    private CanvasControl? _canvas;
    private Dictionary<string, Geography.Country>? _world;
    private MapRenderer? _renderer;
    private bool _ready;
    private bool _queued;
    private string _language = string.Empty;
    private Destination? _selected;
    private IReadOnlyList<Peer> _selectedPeers = [];
    // Drawing reads the marks every frame, so they change only with the
    // selection, the destinations or the map detail.
    private Dictionary<PeerPlace, int> _marks = [];
    private Destination? _pointed;
#if CAPTURE
    private double _preparation;
    internal Action<double>? FrameMeasured { get; set; }
    internal Action<double>? PreparationMeasured { get; set; }
#endif

    internal PeerMap(MapModel model)
    {
        _model = model;
        InitializeComponent();
        _navigationTimer = DispatcherQueue.CreateTimer();
        _navigationTimer.Interval = TimeSpan.FromSeconds(3);
        _navigationTimer.IsRepeating = false;
        _navigationTimer.Tick += (_, _) => Navigation.Visibility = Visibility.Collapsed;
        PointerEntered += (_, _) => ShowNavigation();
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) => ShowNavigation()), true);
        GotFocus += (_, _) => ShowNavigation();
        PreviewKeyDown += (_, _) => ShowNavigation();
        FitButton.RegisterPropertyChangedCallback(FocusStateProperty, (_, _) => ShowNavigation());
        Loaded += OnLoaded;
        Unloaded += (_, _) => Stop();
        ActualThemeChanged += (_, _) => _canvas?.Invalidate();
        foreach (var swatch in new[] { Land, Edge, Download, Upload })
            swatch.RegisterPropertyChangedCallback(
                Border.BackgroundProperty, (_, _) => _canvas?.Invalidate());
        Ink.RegisterPropertyChangedCallback(
            TextBlock.ForegroundProperty, (_, _) => _canvas?.Invalidate());
        _viewport.Changed += (_, navigation) =>
        {
            if (_pointed is not null)
            {
                _pointed = null;
                Describe();
            }
            if (navigation)
                RefreshNavigation();
            _canvas?.Invalidate();
        };
        KeyDown += OnKey;
        ContextRequested += (_, _) => EndDrag(true);
        FitGlyph.FontFamily = Syno.Lucide.Font;
        FitGlyph.Glyph = Syno.Lucide.Maximize;
    }

    /// <summary>
    /// Raised when the person chooses a location on the map or clears the
    /// choice; the peers table shows only that place's peers.
    /// </summary>
    internal event EventHandler<PeerPlace?>? PlaceChanged;

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
        _canvas?.Invalidate();
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
            _canvas?.Invalidate();
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
            if (!IsLoaded || _canvas is not null)
                return;
            var canvas = new CanvasControl();
            _canvas = canvas;
            _world = world;
            var renderer = new MapRenderer(canvas, _model, _viewport, world);
            _renderer = renderer;
            canvas.CreateResources += (_, _) =>
            {
                // Win2D also raises this for a canvas that Stop has retired.
                if (!ReferenceEquals(_canvas, canvas))
                    return;
                _ready = true;
                renderer.ReleaseGeometry();
                QueuePreparation();
            };
            canvas.SizeChanged += (_, _) => QueuePreparation();
            canvas.Draw += Draw;
            canvas.PointerMoved += OnPointer;
            canvas.PointerExited += (_, _) =>
            {
                if (_drag is { IsActive: true })
                    return;
                EndDrag(false);
                if (_pointed is null)
                    return;
                _pointed = null;
                Describe();
                canvas.Invalidate();
            };
            canvas.PointerPressed += OnPressed;
            canvas.PointerReleased += OnReleased;
            canvas.PointerCaptureLost += (_, _) => EndDrag(true);
            canvas.PointerCanceled += (_, _) => EndDrag(true);
            canvas.PointerWheelChanged += OnWheel;
            canvas.DoubleTapped += OnDoubleTapped;
            Drawing.Children.Add(canvas);
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
        if (_queued)
            return;
        // Data and layout can change several times before a frame. Prepare
        // their final state once, without doing model work in Draw.
        _queued = true;
        CompositionTarget.Rendering += OnPreparing;
    }

    private void OnPreparing(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnPreparing;
        _queued = false;
        Prepare();
    }

    private void Prepare()
    {
        if (!_ready || _canvas is null || _renderer is null || _world is null
            || _model.Text is not { } text || _canvas.Size.Width < 1 || _canvas.Size.Height < 1)
            return;
#if CAPTURE
        var started = Stopwatch.GetTimestamp();
#endif
        if (_viewport.Size != _canvas.Size)
        {
            EndDrag(true);
            _viewport.Resize(_canvas.Size);
        }
        var pointed = _pointed?.Group.Place.Cities == _model.Cities ? _pointed?.Key : null;
        _model.Arrange(_world, _viewport.Scale);
        _renderer.Prepare();
        RefreshPlace();
        _pointed = _model.Destinations.FirstOrDefault(destination => destination.Key == pointed);
        if (_selectedPeers.Count > 0)
        {
            var present = _model.Peers.ToHashSet();
            _selectedPeers = _selectedPeers.Where(present.Contains).ToArray();
        }
        _marks = _model.Marks(_selectedPeers);
        if (_language != text.Language)
        {
            _language = text.Language;
            AutomationProperties.SetName(this, text.Get("peer_map", "name"));
            RefreshNavigation();
        }
        Describe();
        _canvas.Invalidate();
#if CAPTURE
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _preparation += elapsed;
        PreparationMeasured?.Invoke(elapsed);
#endif
    }

    private void Stop()
    {
        _model.PropertyChanged -= OnModelChanged;
        CompositionTarget.Rendering -= OnPreparing;
        _queued = false;
        EndDrag(true);
        _navigationTimer.Stop();
        Navigation.Visibility = Visibility.Collapsed;
        ContextFlyout?.Hide();
        _ready = false;
        _renderer?.Dispose();
        _renderer = null;
        _world = null;
        _canvas?.RemoveFromVisualTree();
        _canvas = null;
        _pointed = null;
#if CAPTURE
        _preparation = 0;
#endif
    }

    private MapRenderer.Highlight Highlight => new(_selected?.Key, _marks, _pointed?.Key);

    private bool IsMarked(Destination destination) => Highlight.IsMarked(destination);

    private void Draw(CanvasControl canvas, CanvasDrawEventArgs args)
    {
        if (_model.Text is null || _renderer is null || _viewport.ViewScale <= 0)
            return;
#if CAPTURE
        var started = Stopwatch.GetTimestamp();
#endif
        _renderer.Draw(args.DrawingSession, new MapRenderer.Palette
        {
            Ink = Colour(Ink.Foreground),
            Land = Colour(Land.Background),
            Edge = Colour(Edge.Background),
            Download = Colour(Download.Background),
            Upload = Colour(Upload.Background),
        }, Highlight);
#if CAPTURE
        // Include preparation moved out of Draw so before/after CPU totals remain comparable.
        FrameMeasured?.Invoke(_preparation + Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        _preparation = 0;
#endif
    }

    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (_canvas is null || _renderer is null || MoveDrag(args) || _viewport.ViewScale <= 0)
            return;
        var position = args.GetCurrentPoint(_canvas).Position;
        var nearest = _renderer.HitTest(new Vector2((float)position.X, (float)position.Y));
        if (ReferenceEquals(_pointed, nearest))
            return;
        _pointed = nearest;
        Describe();
        _canvas.Invalidate();
    }

    private void Describe()
    {
        if (_model.Text is not { } text)
            return;
        var destination = Inspected;
        var value = _model.Describe(destination);
        ToolTipService.SetToolTip(this, destination is null || _drag is { IsActive: true } ? null : value);
        AutomationProperties.SetHelpText(this, value + " " + text.Get("peer_map", "keyboard"));
    }

    /// <summary>The peers selected in the table; their places are marked.</summary>
    private void SelectPeers(IReadOnlyList<Peer> peers)
    {
        _selectedPeers = peers;
        _marks = _model.Marks(peers);
    }

}
