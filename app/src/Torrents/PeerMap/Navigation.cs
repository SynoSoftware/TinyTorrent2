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

    private Destination? Inspected(MapMotion.Displayed? displayed) => displayed is null ? null
        : Resolve(displayed, _pointed) ?? displayed.Inspection.Destinations.FirstOrDefault(Highlight(displayed).IsMarked);

    private void ZoomInspection(float factor, MapMotion.Displayed displayed)
    {
        // A followed drag leaves the viewport at its start until it ends.
        EndDrag(false);
        var destination = Inspected(displayed);
        if (destination is not null)
            _viewport.Reveal(destination.Point);
        ZoomAt(factor, destination is null ? _viewport.Offset : _viewport.Screen(destination.Point));
    }

    private void RefreshNavigation()
    {
        if (_model.Text is not { } text)
            return;
        if (_viewport.IsFitted)
        {
            if (FitButton.FocusState != FocusState.Unfocused)
                Focus(FocusState.Programmatic);
            Navigation.Visibility = Visibility.Collapsed;
        }
        ZoomLabel.Text = text.Format("peer_map", "zoom", _viewport.Zoom);
        var fit = text.Get("peer_map", "fit");
        var shortcut = text.Shortcut(VirtualKeyModifiers.Control, VirtualKey.Number0);
        AutomationProperties.SetName(FitButton, fit);
        ToolTipService.SetToolTip(FitButton, text.ShortcutTip(fit, shortcut));
    }

    private void ShowNavigation()
    {
        Navigation.Visibility = IsLoaded && !_viewport.IsFitted ? Visibility.Visible : Visibility.Collapsed;
        HoldNavigation();
    }

    // The zoom level and the Fit button stay while the button has focus or the
    // map has keyboard focus, so Tab reaches it, and the pointer's leaving
    // restarts their timer, so they never disappear under the pointer.
    private void HoldNavigation()
    {
        _navigationTimer.Stop();
        if (
            Navigation.Visibility == Visibility.Visible
            && FitButton.FocusState == FocusState.Unfocused
            && FocusState != FocusState.Keyboard
        )
            _navigationTimer.Start();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(Drawing);
        if (point.Properties.IsHorizontalMouseWheel)
            return;
#if CAPTURE
        CaptureInput?.Invoke("wheel", point.Properties.MouseWheelDelta);
#endif
        ZoomAt(
            (float)Math.Pow(1.25, point.Properties.MouseWheelDelta / 120.0),
            new Vector2((float)point.Position.X, (float)point.Position.Y)
        );
        OnPointer(sender, args);
        args.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        var point = args.GetPosition(Drawing);
        ZoomAt(1.25f, new Vector2((float)point.X, (float)point.Y));
        args.Handled = true;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(Drawing);
        if (!point.Properties.IsLeftButtonPressed || _drag is not null)
            return;
        Focus(FocusState.Pointer);
        var start = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _drag = new Drag(args.Pointer.PointerId, start, _viewport.ToMap(start), _viewport.Center, _viewport.Ratio);
        args.Handled = true;
    }

    private bool MoveDrag(PointerRoutedEventArgs args)
    {
        if (_drag is not { } drag || drag.PointerId != args.Pointer.PointerId)
            return false;
#if CAPTURE
        CaptureInput?.Invoke("pan", 0);
#endif
        var point = args.GetCurrentPoint(Drawing);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndDrag(true);
            return true;
        }
        var delta = new Vector2((float)point.Position.X, (float)point.Position.Y) - drag.Start;
        if (!drag.IsActive && delta.LengthSquared() >= 16)
        {
            if (!Drawing.CapturePointer(args.Pointer))
            {
                EndDrag(true);
                return true;
            }
            drag.IsActive = true;
            ProtectedCursor = PanCursor;
            Describe();
            if (args.Pointer.PointerDeviceType == PointerDeviceType.Mouse && Cursor() is { } cursor)
            {
                drag.IsFollowed = _motion?.Follow(drag.Grip, cursor) == true;
            }
        }
        if (drag.IsActive && !drag.IsFollowed && _viewport.Scale > 0)
        {
            _viewport.Hold(_viewport.Zoom, drag.Grip, new Vector2((float)point.Position.X, (float)point.Position.Y));
        }
        args.Handled = true;
        return true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_drag is not { } drag || drag.PointerId != args.Pointer.PointerId)
            return;
        var moved = drag.IsActive;
        if (moved && !drag.IsFollowed && _viewport.Scale > 0)
        {
            var point = args.GetCurrentPoint(Drawing).Position;
            _viewport.Hold(_viewport.Zoom, drag.Grip, new Vector2((float)point.X, (float)point.Y));
        }
        EndDrag(false);
        var displayed = Displayed;
        if (displayed is not null)
            PointAt(args, displayed);
        if (!moved && displayed is not null)
        {
            SelectPeers([]);
            Choose(_pointed, displayed);
            Describe(displayed);
            Invalidate();
        }
        args.Handled = true;
    }

    private void EndDrag(bool cancel)
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        ProtectedCursor = null;
        Drawing.ReleasePointerCaptures();
        // Released before the view is set, because until then Navigate keeps
        // the centre the game loop reached.
        var reached = drag.IsFollowed ? _motion?.Release() : null;
        if (cancel && drag.IsActive)
        {
            _viewport.SetView(_viewport.Zoom, drag.Center, drag.Ratio);
        }
        else if (reached is not null)
        {
            _viewport.SetView(_viewport.Zoom, reached.Center, reached.AxisScale.X / reached.AxisScale.Y);
        }
        // A followed drag leaves the viewport at its start, so cancelling
        // changes nothing there and raises no navigation of its own.
        if (drag.IsFollowed)
        {
            _motion?.Navigate(_viewport.View);
        }
        // The rest that came during the drag waited for its end, also when the release changes no view.
        if (drag.IsActive)
        {
            _restTimer.Stop();
            _restTimer.Start();
        }
        Describe();
    }

#if CAPTURE
    /// <summary>
    /// Starts a drag grabbed at <paramref name="start"/> that the game loop
    /// moves along <paramref name="pointer"/>, for a replay that has no mouse.
    /// <see cref="EndDrag"/> ends it.
    /// </summary>
    /// <returns>Whether the game loop follows the drag; when it does not, no drag is left.</returns>
    internal bool CaptureDrag(Vector2 start, Func<Vector2?> pointer)
    {
        EndDrag(false);
        var drag = new Drag(0, start, _viewport.ToMap(start), _viewport.Center, _viewport.Ratio)
        {
            IsActive = true,
        };
        _drag = drag;
        drag.IsFollowed = _motion?.Follow(drag.Grip, pointer) == true;
        if (!drag.IsFollowed)
        {
            EndDrag(false);
        }
        return drag.IsFollowed;
    }
#endif

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        if (
            (
                !ReferenceEquals(args.OriginalSource, this)
                && !ReferenceEquals(args.OriginalSource, FitButton)
            )
            || Displayed is not { } displayed
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
        else if (
            args.Key
            is VirtualKey.Add
                or (VirtualKey)OemKey.Plus
                or VirtualKey.Subtract
                or (VirtualKey)OemKey.Minus
        )
            ZoomInspection(
                args.Key is VirtualKey.Add or (VirtualKey)OemKey.Plus ? 1.25f : .8f,
                displayed
            );
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
            var destinations = displayed.Inspection.Destinations;
            if (destinations.Count == 0)
                return;
            var step = args.Key is VirtualKey.Left or VirtualKey.Up ? -1 : 1;
            var index = -1;
            var highlight = Highlight(displayed);
            // From the pointed destination, else the chosen one while it shows, else the first
            // marked: after a zoom the chosen places can show as several destinations.
            var start = _pointed?.Key ?? Resolve(displayed, _selected)?.Key;
            for (var candidate = 0; candidate < destinations.Count; candidate++)
                if (start is null ? highlight.IsMarked(destinations[candidate]) : destinations[candidate].Key == start)
                {
                    index = candidate;
                    break;
                }
            index =
                index < 0
                    ? (step > 0 ? 0 : destinations.Count - 1)
                    : (index + step + destinations.Count) % destinations.Count;
            _pointed = destinations[index];
            SelectPeers([]);
            Choose(_pointed, displayed);
            _viewport.Reveal(_pointed.Point);
            Describe(displayed);
            Invalidate();
        }
        else if (args.Key == VirtualKey.Escape)
        {
            SelectPeers([]);
            Choose(null, displayed);
            _pointed = null;
            Describe(displayed);
            Invalidate();
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

        /// <summary>Whether the map's game loop moves the view for this drag.</summary>
        internal bool IsFollowed { get; set; }
    }
}
