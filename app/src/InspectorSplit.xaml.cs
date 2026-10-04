using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Syno.TinyTorrent;

public sealed partial class InspectorSplit : UserControl
{
    private double _value = 360;
    private double _minimum = 300;
    private double _maximum = 500;
    public double Value => _value;
    public event EventHandler<double>? ValueChanged;

    public InspectorSplit()
    {
        InitializeComponent();
        Handle.DragStarted += (_, _) => Focus(FocusState.Pointer);
        Handle.DragDelta += (_, args) => SetValue(_value - args.VerticalChange);
        KeyDown += OnKey;
    }

    internal void SetBounds(double minimum, double maximum, double value)
    {
        _minimum = minimum;
        _maximum = Math.Max(minimum, maximum);
        _value = Math.Clamp(value, _minimum, _maximum);
    }

    internal void SetValue(double value)
    {
        var clamped = Math.Clamp(value, _minimum, _maximum);
        if (_value == clamped) return;
        _value = clamped;
        ValueChanged?.Invoke(this, _value);
    }

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        var value = args.Key switch
        {
            VirtualKey.Up => _value + 16,
            VirtualKey.Down => _value - 16,
            VirtualKey.Home => _minimum,
            VirtualKey.End => _maximum,
            _ => _value
        };
        if (args.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)) return;
        SetValue(value);
        args.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(InspectorSplit owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Separator;
        protected override string GetClassNameCore() => nameof(InspectorSplit);
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);
        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => 64;
        public double SmallChange => 16;
        public double Maximum => owner._maximum;
        public double Minimum => owner._minimum;
        public double Value => owner.Value;
        public void SetValue(double value)
        {
            if (IsReadOnly) throw new InvalidOperationException();
            owner.SetValue(value);
        }
    }
}
