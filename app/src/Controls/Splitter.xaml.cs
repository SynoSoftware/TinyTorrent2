using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Syno.TinyTorrent.Controls;

// Value is the size of the pane after the splitter, so dragging or pressing
// towards the start of the layout grows it.
public sealed partial class Splitter : UserControl
{
    private double _value = 360;
    private double _minimum = 300;
    private double _maximum = 500;
    private Orientation _orientation;
    private bool _pointed;
    public double Value => _value;
    public event EventHandler<double>? ValueChanged;

    // Horizontal is a bar between rows; Vertical is a bar between columns.
    public Orientation Orientation
    {
        get => _orientation;
        set
        {
            _orientation = value;
            var horizontal = value == Orientation.Horizontal;
            Width = horizontal ? double.NaN : 16;
            Height = horizontal ? 16 : double.NaN;
            Grip.Width = horizontal ? 32 : 4;
            Grip.Height = horizontal ? 4 : 32;
            ProtectedCursor = InputSystemCursor.Create(
                horizontal
                    ? InputSystemCursorShape.SizeNorthSouth
                    : InputSystemCursorShape.SizeWestEast
            );
        }
    }

    public Splitter()
    {
        InitializeComponent();
        Orientation = Orientation.Horizontal;
        Handle.DragStarted += (_, _) => Focus(FocusState.Pointer);
        Handle.DragDelta += (_, args) =>
            SetValue(
                _value
                    - (
                        _orientation == Orientation.Horizontal
                            ? args.VerticalChange
                            : args.HorizontalChange
                    )
            );
        // The grip turns accent while pointed at, and stays so through a drag
        // that leaves the strip.
        PointerEntered += (_, _) => ShowActive(_pointed = true);
        PointerExited += (_, _) => ShowActive(_pointed = false);
        Handle.DragCompleted += (_, _) => ShowActive(_pointed);
        KeyDown += OnKey;
    }

    private void ShowActive(bool pointed) =>
        VisualStateManager.GoToState(
            this,
            pointed || Handle.IsDragging ? "Active" : "Normal",
            false
        );

    internal void SetBounds(double minimum, double maximum, double value)
    {
        _minimum = minimum;
        _maximum = Math.Max(minimum, maximum);
        _value = Math.Clamp(value, _minimum, _maximum);
    }

    internal void SetValue(double value)
    {
        var clamped = Math.Clamp(value, _minimum, _maximum);
        if (_value == clamped)
            return;
        _value = clamped;
        ValueChanged?.Invoke(this, _value);
    }

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        var (grow, shrink) =
            _orientation == Orientation.Horizontal
                ? (VirtualKey.Up, VirtualKey.Down)
                : (VirtualKey.Left, VirtualKey.Right);
        if (_orientation == Orientation.Vertical && FlowDirection == FlowDirection.RightToLeft)
            (grow, shrink) = (shrink, grow);
        if (
            args.Key != grow
            && args.Key != shrink
            && args.Key is not (VirtualKey.Home or VirtualKey.End)
        )
            return;
        SetValue(
            args.Key == grow ? _value + 16
            : args.Key == shrink ? _value - 16
            : args.Key == VirtualKey.Home ? _minimum
            : _maximum
        );
        args.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(Splitter owner)
        : FrameworkElementAutomationPeer(owner),
            IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Separator;

        protected override string GetClassNameCore() => nameof(Splitter);

        protected override object GetPatternCore(PatternInterface pattern) =>
            pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);

        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => 64;
        public double SmallChange => 16;
        public double Maximum => owner._maximum;
        public double Minimum => owner._minimum;
        public double Value => owner.Value;

        public void SetValue(double value)
        {
            if (IsReadOnly)
                throw new InvalidOperationException();
            owner.SetValue(value);
        }
    }
}
