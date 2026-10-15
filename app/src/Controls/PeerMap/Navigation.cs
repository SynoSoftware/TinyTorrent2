using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Syno.TinyTorrent.Models;
using Windows.System;
using Windows.UI.Core;

namespace Syno.TinyTorrent.Controls;

public sealed partial class PeerMap
{
    private static readonly InputSystemCursor PanCursor = InputSystemCursor.Create(
        InputSystemCursorShape.SizeAll
    );
    private const VirtualKey Plus = (VirtualKey)0xBB;
    private const VirtualKey Minus = (VirtualKey)0xBD;
    private float _zoom = 1;

    // ZoomAt, the one way to change the zoom, keeps it from 1 to MaxZoom.
    private const float MaxZoom = 32;

    // Longitude from _meridian and inverted latitude keep the view independent
    // of viewport size.
    private Vector2 _center;
    private Drag? _drag;

    private float ViewScale => _scale * _zoom;

    private Matrix3x2 ViewTransform =>
        Matrix3x2.CreateScale(ViewScale)
        * Matrix3x2.CreateTranslation(_offset - _center * ViewScale);

    // A location shows once, in the turn of longitude around the view centre.
    private Vector2 Screen(GeoPoint point)
    {
        var mapped = Coordinates(point);
        mapped.X = Wrap(mapped.X, _center.X);
        return Vector2.Transform(mapped, ViewTransform);
    }

    private Vector2 ToMap(Vector2 point) => (point - _offset) / ViewScale + _center;

    private void SetView(float zoom, Vector2 center)
    {
        var limit = _scale > 0 ? Math.Max(0, 90 - _offset.Y / (_scale * zoom)) : 0;
        // Longitude scrolls without end; latitude stops at the map's edges.
        center = new Vector2(Wrap(center.X, 0), Math.Clamp(center.Y, -limit, limit));
        if (zoom == _zoom && center == _center)
            return;
        var previous = (_zoom, IsFitted);
        _zoom = zoom;
        _center = center;
        if (_pointed is not null)
        {
            _pointed = null;
            Describe();
        }
        if (previous != (_zoom, IsFitted))
            RefreshNavigation();
        _canvas?.Invalidate();
    }

    private void ZoomAt(float factor, Vector2 anchor)
    {
        if (_scale <= 0)
            return;
        EndDrag(false);
        var zoom = Math.Clamp(_zoom * factor, 1, MaxZoom);
        var center = _center + (anchor - _offset) / _scale * (1 / _zoom - 1 / zoom);
        SetView(zoom, center);
    }

    private void Pan(Vector2 delta)
    {
        if (_scale > 0)
            SetView(_zoom, _center - delta / (_scale * _zoom));
    }

    /// <summary>The whole world shows with the origin in the centre, as Fit leaves it.</summary>
    private bool IsFitted => _zoom == 1 && _center == Vector2.Zero;

    private void Fit()
    {
        EndDrag(false);
        SetView(1, Vector2.Zero);
        Focus(FocusState.Programmatic);
    }

    private void OnFit(object sender, RoutedEventArgs args) => Fit();

    private void Reveal(Destination destination)
    {
        var point = Screen(destination.Point);
        var inset = Vector2.Min(new Vector2(12), _offset);
        var visible = Vector2.Clamp(point, inset, 2 * _offset - inset);
        Pan(visible - point);
    }

    private Destination? Inspected => _pointed ?? _destinations.FirstOrDefault(IsMarked);

    private void ZoomInspection(float factor)
    {
        var destination = Inspected;
        if (destination is not null)
            Reveal(destination);
        ZoomAt(factor, destination is null ? _offset : Screen(destination.Point));
    }

    private void RefreshNavigation()
    {
        if (_text is not { } text)
            return;
        if (IsFitted && FitButton.FocusState != FocusState.Unfocused)
            Focus(FocusState.Programmatic);
        Navigation.Visibility = IsFitted ? Visibility.Collapsed : Visibility.Visible;
        ZoomLabel.Text = text.Format("peer_map", "zoom", _zoom);
        var fit = text.Get("peer_map", "fit");
        var shortcut = text.Get("shortcuts", "control") + "+" + text.Get("shortcuts", "number_0");
        AutomationProperties.SetName(FitButton, fit);
        ToolTipService.SetToolTip(FitButton, text.Format("shortcuts", "tip", fit, shortcut));
    }

    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_canvas);
        if (point.Properties.IsHorizontalMouseWheel)
            return;
        ZoomAt(
            (float)Math.Pow(1.25, point.Properties.MouseWheelDelta / 120.0),
            new Vector2((float)point.Position.X, (float)point.Position.Y)
        );
        OnPointer(sender, args);
        args.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        var point = args.GetPosition(_canvas);
        ZoomAt(1.25f, new Vector2((float)point.X, (float)point.Y));
        args.Handled = true;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed || _drag is not null)
            return;
        Focus(FocusState.Pointer);
        _drag = new Drag(
            args.Pointer.PointerId,
            new Vector2((float)point.Position.X, (float)point.Position.Y),
            _center
        );
        args.Handled = true;
    }

    private bool MoveDrag(PointerRoutedEventArgs args)
    {
        if (_drag is not { } drag || drag.PointerId != args.Pointer.PointerId || _canvas is null)
            return false;
        var point = args.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndDrag(true);
            return true;
        }
        var delta = new Vector2((float)point.Position.X, (float)point.Position.Y) - drag.Start;
        if (!drag.IsActive && delta.LengthSquared() >= 16)
        {
            if (!_canvas.CapturePointer(args.Pointer))
            {
                EndDrag(true);
                return true;
            }
            drag.IsActive = true;
            ProtectedCursor = PanCursor;
            Describe();
        }
        if (drag.IsActive && _scale > 0)
            SetView(_zoom, drag.Center - delta / (_scale * _zoom));
        args.Handled = true;
        return true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_drag is not { } drag || drag.PointerId != args.Pointer.PointerId)
            return;
        var moved = drag.IsActive;
        if (moved && _scale > 0)
        {
            var point = args.GetCurrentPoint(_canvas).Position;
            var delta = new Vector2((float)point.X, (float)point.Y) - drag.Start;
            SetView(_zoom, drag.Center - delta / (_scale * _zoom));
        }
        EndDrag(false);
        OnPointer(sender, args);
        if (!moved)
        {
            SelectPeers([]);
            Choose(_pointed);
            Describe();
            _canvas?.Invalidate();
        }
        args.Handled = true;
    }

    private void EndDrag(bool cancel)
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        ProtectedCursor = null;
        _canvas?.ReleasePointerCaptures();
        if (cancel && drag.IsActive)
            SetView(_zoom, drag.Center);
        Describe();
    }

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        if (
            (
                !ReferenceEquals(args.OriginalSource, this)
                && !ReferenceEquals(args.OriginalSource, FitButton)
            )
            || _canvas is null
            || InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Menu)
                .HasFlag(CoreVirtualKeyStates.Down)
        )
            return;
        var control = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (args.Key == VirtualKey.Escape && _drag is not null)
            EndDrag(true);
        else if (args.Key is VirtualKey.Add or Plus or VirtualKey.Subtract or Minus)
            ZoomInspection(args.Key is VirtualKey.Add or Plus ? 1.25f : .8f);
        else if (control && args.Key is VirtualKey.Number0 or VirtualKey.NumberPad0)
            Fit();
        else if (
            control
            && args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
        )
        {
            EndDrag(false);
            Pan(
                args.Key switch
                {
                    VirtualKey.Left => new Vector2(_offset.X / 4, 0),
                    VirtualKey.Right => new Vector2(-_offset.X / 4, 0),
                    VirtualKey.Up => new Vector2(0, _offset.Y / 4),
                    _ => new Vector2(0, -_offset.Y / 4),
                }
            );
        }
        else if (
            args.Key is VirtualKey.Left or VirtualKey.Up or VirtualKey.Right or VirtualKey.Down
        )
        {
            if (_destinations.Count == 0)
                return;
            var step = args.Key is VirtualKey.Left or VirtualKey.Up ? -1 : 1;
            var index = _pointed is null
                ? _destinations.FindIndex(IsMarked)
                : _destinations.IndexOf(_pointed);
            index =
                index < 0
                    ? (step > 0 ? 0 : _destinations.Count - 1)
                    : (index + step + _destinations.Count) % _destinations.Count;
            _pointed = _destinations[index];
            SelectPeers([]);
            Choose(_pointed);
            Reveal(_pointed);
            Describe();
            _canvas.Invalidate();
        }
        else if (args.Key == VirtualKey.Escape)
        {
            SelectPeers([]);
            Choose(null);
            _pointed = null;
            Describe();
            _canvas.Invalidate();
        }
        else
            return;
        args.Handled = true;
    }

    private sealed class Drag(uint pointerId, Vector2 start, Vector2 center)
    {
        internal uint PointerId { get; } = pointerId;
        internal Vector2 Start { get; } = start;
        internal Vector2 Center { get; } = center;
        internal bool IsActive { get; set; }
    }
}
