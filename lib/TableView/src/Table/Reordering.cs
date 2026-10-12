using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Syno.TableView;

public sealed partial class Table
{
    private Order? _order;
    private Body.Wheel? _wheel;
    private Order.Entry[] _dragOrder = [];
    private Order.Entry[] _remaining = [];
    private IReadOnlyList<object> _dragPacket = Array.Empty<object>();
    private int? _gap;
    private UIElement? _inputRoot;
    private XamlRoot? _dragRoot;
    private ScrollViewer? _dragScroller;
    private ScrollMode _scrollMode;

    /// <summary>
    /// Requests a host-owned reorder after the gesture completes. TableView does not mutate
    /// the source or infer whether the host accepts the request.
    /// </summary>
    public event EventHandler<ReorderRequestedEventArgs>? ReorderRequested;

    internal void SetOrder(Order order)
    {
        if (_order is not null)
            throw ConfigurationError("An order is already configured.");
        _order = order;
    }

    internal bool CanStartDrag(object item) =>
        _hierarchy is null && _order is not null && CanReorder && IsInteractive(item);

    private Point ToRoot(Point point) => _surface!.TransformToVisual(null).TransformPoint(point);

    private void BeginDrag(object item)
    {
        _dragOrder = _order!.Capture(_identity);
        var selected = new HashSet<object>(
            _selection.IsSelected(item) ? SelectedItems : new[] { item },
            _identity
        );
        var packet = new List<object>();
        var remaining = new List<Order.Entry>();
        double total = 0;
        foreach (var entry in _dragOrder)
        {
            if (selected.Contains(entry.Item) && IsInteractive(entry.Item))
            {
                packet.Add(entry.Item);
                total += entry.Position;
            }
            else
            {
                remaining.Add(entry);
            }
        }
        _dragPacket = packet.ToArray();
        _remaining = remaining.ToArray();
        double? average = packet.Count == 0 ? null : total / packet.Count;
        _gesture = RowGesture.RowDrag;
        _dragScroller = InnerScrollViewer();
        if (_dragScroller is not null)
        {
            _scrollMode = _dragScroller.VerticalScrollMode;
            _dragScroller.VerticalScrollMode = ScrollMode.Disabled;
        }
        _wheel = new Body.Wheel(this, _order.Column, packet.Count);
        _wheel.Begin(_remaining, average, ToRoot(_gestureOrigin));
        _inputRoot = XamlRoot.Content;
        _inputRoot.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnWheelChanged),
            true
        );
        _inputRoot.PreviewKeyDown += OnDragKey;
        _dragRoot = XamlRoot;
        _dragRoot.Changed += OnRootChanged;
        SetDestination(null);
        RowVisualsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture != RowGesture.RowDrag)
            return;
        e.Handled = true;
        var point = e.GetCurrentPoint(_surface);
        if (!point.Properties.IsHorizontalMouseWheel)
            SetDestination(
                _wheel!.Scroll(point.Properties.MouseWheelDelta, ToRoot(point.Position))
            );
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => CancelDrag();

    private void OnDragKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu)
            return;
        if (e.Key == VirtualKey.Escape)
            e.Handled = true;
        CancelDrag();
    }

    private void EndDrag()
    {
        _wheel?.End();
        _wheel = null;
        if (_dragScroller is not null)
        {
            _dragScroller.VerticalScrollMode = _scrollMode;
            _dragScroller = null;
        }
        if (_inputRoot is not null)
        {
            _inputRoot.RemoveHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(OnWheelChanged)
            );
            _inputRoot.PreviewKeyDown -= OnDragKey;
            _inputRoot = null;
        }
        if (_dragRoot is not null)
        {
            _dragRoot.Changed -= OnRootChanged;
            _dragRoot = null;
        }
        _dragOrder = [];
        _remaining = [];
        _dragPacket = Array.Empty<object>();
        _gap = null;
        RefreshDestination();
    }

    private bool IsDragCurrent()
    {
        if (!CanReorder || _order is null || _dragPacket.Any(item => !IsInteractive(item)))
            return false;
        var current = _order.Capture(_identity);
        if (current.Length != _dragOrder.Length)
            return false;
        for (int index = 0; index < current.Length; index++)
        {
            if (
                !ReferenceEquals(current[index].Item, _dragOrder[index].Item)
                || current[index].Position != _dragOrder[index].Position
            )
                return false;
        }
        return true;
    }

    private void ReconcileDrag()
    {
        if (_gesture == RowGesture.RowDrag && !IsDragCurrent())
            CancelGesture();
    }

    private void CompleteDrag(Point point)
    {
        int? gap = _wheel!.Track(point);
        object? before =
            gap is int index && index < _remaining.Length ? _remaining[index].Item : null;
        var packet = _dragPacket;
        bool requested =
            gap.HasValue && packet.Count > 0 && IsDragCurrent() && !KeepsOrder(packet, before);
        CancelGesture();
        if (requested)
            ReorderRequested?.Invoke(this, new ReorderRequestedEventArgs(packet, before));
    }

    private bool KeepsOrder(IReadOnlyList<object> packet, object? before)
    {
        int start = Array.FindIndex(_dragOrder, entry => _identity.Equals(entry.Item, packet[0]));
        for (int index = 0; index < packet.Count; index++)
        {
            if (
                start + index >= _dragOrder.Length
                || !_identity.Equals(_dragOrder[start + index].Item, packet[index])
            )
                return false;
        }
        int next = start + packet.Count;
        return _identity.Equals(next < _dragOrder.Length ? _dragOrder[next].Item : null, before);
    }

    internal bool IsDragging(object? item) =>
        _gesture == RowGesture.RowDrag && item is not null && _dragPacket.Contains(item, _identity);

    private void SetDestination(int? gap)
    {
        _gap = gap;
        RefreshDestination();
    }

    private void RefreshDestination()
    {
        string status = string.Empty;
        if (_gesture == RowGesture.RowDrag)
        {
            if (_dragPacket.Count == 0 || _remaining.Length == 0)
                status = Strings.NothingToMove;
            else if (_gap is not int index)
                status = Strings.ChoosePosition;
            else if (index == _remaining.Length)
                status = Strings.DropAtEnd;
            else
                status = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.DropBeforeRow,
                    _remaining[index].Position
                );
            _wheel!.RefreshText(status);
        }
        if (AutomationProperties.GetItemStatus(this) == status)
            return;
        AutomationProperties.SetItemStatus(this, status);
        if (status.Length > 0)
            FrameworkElementAutomationPeer
                .FromElement(this)
                ?.RaiseNotificationEvent(
                    AutomationNotificationKind.Other,
                    AutomationNotificationProcessing.MostRecent,
                    status,
                    "TableViewRowDrag"
                );
    }

    private void CancelDrag()
    {
        if (_gesture == RowGesture.RowDrag)
            CancelGesture();
    }
}
