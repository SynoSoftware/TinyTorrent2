using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace Syno.TableView.Body;

public sealed partial class Wheel : UserControl
{
    private const double Spacing = 42;
    private readonly Table _owner;
    private readonly Column _column;
    private readonly Popup _popup;
    private readonly FlyoutPresenter _presenter;
    private Order.Entry[] _entries = [];
    private double _center;
    private int _remainder;
    private readonly int _count;

    internal Wheel(Table owner, Column column, int count)
    {
        InitializeComponent();
        _owner = owner;
        _column = column;
        _count = count;
        Packet.Value = count;
        _presenter = new FlyoutPresenter
        {
            Content = this,
            Padding = new Thickness(0),
            IsHitTestVisible = false,
            IsTabStop = false,
        };
        _presenter.SetBinding(
            FlowDirectionProperty,
            new Binding
            {
                Source = owner,
                Path = new PropertyPath(nameof(FlowDirection)),
                Mode = BindingMode.OneWay,
            }
        );
        _popup = new Popup
        {
            Child = _presenter,
            IsLightDismissEnabled = false,
            ShouldConstrainToRootBounds = true,
        };
    }

    internal void Begin(Order.Entry[] entries, double? average, Point origin)
    {
        _entries = entries;
        _remainder = 0;
        _center = 0;
        if (average is double value && entries.Length > 0)
        {
            int index = Array.FindIndex(entries, entry => entry.Position >= value);
            if (index < 0)
            {
                _center = entries.Length - .5;
            }
            else if (index == 0)
            {
                _center = entries[0].Position == value ? 0 : -.5;
            }
            else
            {
                double previous = entries[index - 1].Position;
                _center = index - 1 + (value - previous) / (entries[index].Position - previous);
            }
        }
        _popup.XamlRoot = _owner.XamlRoot;
        _presenter.RequestedTheme = _owner.ActualTheme;
        _owner.ActualThemeChanged += OnThemeChanged;
        RefreshText(_owner.Strings.ChoosePosition);
        _presenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _presenter.DesiredSize;
        var root = _owner.XamlRoot.Size;
        _popup.HorizontalOffset = Math.Clamp(
            origin.X + 24,
            0,
            Math.Max(0, root.Width - size.Width)
        );
        _popup.VerticalOffset = Math.Clamp(
            origin.Y - size.Height / 2,
            0,
            Math.Max(0, root.Height - size.Height)
        );
        DrawNumbers();
        Marker.Visibility = Visibility.Collapsed;
        _popup.IsOpen = true;
    }

    internal int? Track(Point point)
    {
        Marker.Visibility = Visibility.Collapsed;
        if (_count == 0 || _entries.Length == 0)
            return null;
        var local = Rail.TransformToVisual(null).Inverse.TransformPoint(point);
        double x = local.X;
        double y = local.Y;
        if (x < 0 || x > Rail.ActualWidth || y < 0 || y > Rail.ActualHeight)
            return null;
        double position = _center + (y - Rail.ActualHeight / 2) / Spacing;
        int gap = (int)Math.Round(position + .5);
        if (gap < 0 || gap > _entries.Length || Math.Abs(position - gap + .5) * Spacing > 16)
            return null;
        MarkerOffset.Y = Rail.ActualHeight / 2 + (gap - .5 - _center) * Spacing - Marker.Height / 2;
        Marker.Visibility = Visibility.Visible;
        return gap;
    }

    internal int? Scroll(int delta, Point point)
    {
        _remainder -= delta;
        int steps = _remainder / 120;
        _remainder %= 120;
        if (steps != 0)
        {
            _center = Math.Clamp(_center + steps, -.5, Math.Max(-.5, _entries.Length - .5));
            DrawNumbers();
        }
        return Track(point);
    }

    private void DrawNumbers()
    {
        Numbers.Children.Clear();
        Empty.Visibility = _entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Spine.Visibility = _entries.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_entries.Length == 0)
            return;
        for (
            int index = Math.Max(0, (int)Math.Floor(_center) - 3);
            index <= Math.Min(_entries.Length, (int)Math.Ceiling(_center) + 4);
            index++
        )
        {
            double y = 126 + (index - _center) * Spacing;
            var gap = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = Width - 32 - 48,
                Height = 1,
            };
            gap.SetBinding(
                Microsoft.UI.Xaml.Shapes.Shape.FillProperty,
                new Binding
                {
                    Source = Spine,
                    Path = new PropertyPath(nameof(Spine.Fill)),
                    Mode = BindingMode.OneWay,
                }
            );
            Canvas.SetLeft(gap, 48);
            Canvas.SetTop(gap, y - Spacing / 2);
            Numbers.Children.Add(gap);
            if (index == _entries.Length)
                continue;
            var row = new Grid { Width = Width - 32, Height = Spacing };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            );
            var number = new TextBlock
            {
                Text = _entries[index].Position.ToString(CultureInfo.CurrentCulture),
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = _entries[index].Item.ToString(),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Style = Hint.Style,
            };
            label.SetBinding(
                TextBlock.ForegroundProperty,
                new Binding
                {
                    Source = Hint,
                    Path = new PropertyPath(nameof(Hint.Foreground)),
                    Mode = BindingMode.OneWay,
                }
            );
            Grid.SetColumn(label, 1);
            row.Children.Add(number);
            row.Children.Add(label);
            Canvas.SetTop(row, y - Spacing / 2);
            Numbers.Children.Add(row);
        }
    }

    internal void RefreshText(string destination)
    {
        Heading.Text = _count == 0 ? _column.DisplayName : _owner.Strings.MoveItems(_count);
        Destination.Text = destination;
        Hint.Text =
            _count == 0 || _entries.Length == 0
                ? _owner.Strings.BrowseOrder
                : _owner.Strings.DragHint;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) =>
        _presenter.RequestedTheme = _owner.ActualTheme;

    internal void End()
    {
        _owner.ActualThemeChanged -= OnThemeChanged;
        _popup.IsOpen = false;
        _entries = [];
        Numbers.Children.Clear();
    }
}
