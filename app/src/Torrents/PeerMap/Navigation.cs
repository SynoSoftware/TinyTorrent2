using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using static Syno.TinyTorrent.Torrents.MapModel;

namespace Syno.TinyTorrent.Torrents;

public sealed partial class PeerMap
{
    private static readonly InputSystemCursor PanCursor = InputSystemCursor.Create(
        InputSystemCursorShape.SizeAll
    );
    private const VirtualKey Plus = (VirtualKey)0xBB;
    private const VirtualKey Minus = (VirtualKey)0xBD;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _navigationTimer;
    private Drag? _drag;

    private void ZoomAt(float factor, Vector2 anchor)
    {
        EndDrag(false);
        _viewport.ZoomAt(factor, anchor);
        ShowNavigation();
    }

    private void Fit()
    {
        EndDrag(false);
        _viewport.Fit();
        Focus(FocusState.Programmatic);
    }

    private void OnFit(object sender, RoutedEventArgs args) => Fit();

    private Destination? Inspected => _pointed ?? _model.Destinations.FirstOrDefault(IsMarked);

    private void ZoomInspection(float factor)
    {
        var destination = Inspected;
        if (destination is not null)
            _viewport.Reveal(destination.Point);
        ZoomAt(factor, destination is null ? _viewport.Offset : _viewport.Screen(destination.Point));
    }

    private void RefreshNavigation()
    {
        if (_model.Text is not { } text)
            return;
        if (_viewport.IsFitted && FitButton.FocusState != FocusState.Unfocused)
            Focus(FocusState.Programmatic);
        ShowNavigation();
        ZoomLabel.Text = text.Format("peer_map", "zoom", _viewport.Zoom);
        var fit = text.Get("peer_map", "fit");
        var shortcut = text.Get("shortcuts", "control") + "+" + text.Get("shortcuts", "number_0");
        AutomationProperties.SetName(FitButton, fit);
        ToolTipService.SetToolTip(FitButton, text.Format("shortcuts", "tip", fit, shortcut));
    }

    private void ShowNavigation()
    {
        _navigationTimer.Stop();
        Navigation.Visibility = IsLoaded && !_viewport.IsFitted ? Visibility.Visible : Visibility.Collapsed;
        if (Navigation.Visibility == Visibility.Visible && FitButton.FocusState == FocusState.Unfocused)
            _navigationTimer.Start();
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
        ShowNavigation();
        var start = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _drag = new Drag(args.Pointer.PointerId, start, _viewport.ToMap(start), _viewport.Center, _viewport.Ratio);
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
        if (drag.IsActive && _viewport.Scale > 0)
            _viewport.Hold(_viewport.Zoom, drag.Grip, new Vector2((float)point.Position.X, (float)point.Position.Y));
        args.Handled = true;
        return true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_drag is not { } drag || drag.PointerId != args.Pointer.PointerId)
            return;
        var moved = drag.IsActive;
        if (moved && _viewport.Scale > 0)
        {
            var point = args.GetCurrentPoint(_canvas).Position;
            _viewport.Hold(_viewport.Zoom, drag.Grip, new Vector2((float)point.X, (float)point.Y));
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
            _viewport.SetView(_viewport.Zoom, drag.Center, drag.Ratio);
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
            _viewport.Pan(
                args.Key switch
                {
                    VirtualKey.Left => new Vector2(_viewport.Offset.X / 4, 0),
                    VirtualKey.Right => new Vector2(-_viewport.Offset.X / 4, 0),
                    VirtualKey.Up => new Vector2(0, _viewport.Offset.Y / 4),
                    _ => new Vector2(0, -_viewport.Offset.Y / 4),
                }
            );
        }
        else if (
            args.Key is VirtualKey.Left or VirtualKey.Up or VirtualKey.Right or VirtualKey.Down
        )
        {
            if (_model.Destinations.Count == 0)
                return;
            var step = args.Key is VirtualKey.Left or VirtualKey.Up ? -1 : 1;
            var index = _pointed is null
                ? _model.Destinations.FindIndex(IsMarked)
                : _model.Destinations.IndexOf(_pointed);
            index =
                index < 0
                    ? (step > 0 ? 0 : _model.Destinations.Count - 1)
                    : (index + step + _model.Destinations.Count) % _model.Destinations.Count;
            _pointed = _model.Destinations[index];
            SelectPeers([]);
            Choose(_pointed);
            _viewport.Reveal(_pointed.Point);
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

    private sealed class Drag(uint pointerId, Vector2 start, Vector2 grip, Vector2 center, float ratio)
    {
        internal uint PointerId { get; } = pointerId;
        internal Vector2 Start { get; } = start;

        /// <summary>The map point under the pointer when the drag began; the drag keeps it there.</summary>
        internal Vector2 Grip { get; } = grip;

        /// <summary>The view before the drag, which cancelling restores.</summary>
        internal Vector2 Center { get; } = center;
        internal float Ratio { get; } = ratio;
        internal bool IsActive { get; set; }
    }
}
