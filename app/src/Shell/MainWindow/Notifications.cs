using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _noticeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };
    private bool _noticeHovered;
    private bool _windowActive = true;
    private readonly HashSet<Notice> _shownNotices = [];

    private void ConfigureNotifications()
    {
        AppStatusBar.RegisterPropertyChangedCallback(
            InfoBar.MessageProperty,
            (_, _) =>
            {
                if (!AppStatusBar.IsOpen || AppStatusBar.Message.Length == 0)
                    return;
                // InfoBar does not announce message changes while it stays open.
                AppStatusBar.IsOpen = false;
                AppStatusBar.IsOpen = true;
            }
        );
        Model.Notices.CollectionChanged += (_, _) =>
        {
            UpdateNoticeTimer();
            if (Model.Notices.Count == 0)
            {
                // A notice that closes under the pointer may leave without
                // a PointerExited, which would pause the next notices forever.
                _noticeHovered = false;
            }
            // Retiring shown notices can change the collection again.
            DispatcherQueue.TryEnqueue(UpdateNotices);
        };
        Model.PropertyChanged += (_, args) =>
        {
            if (
                string.IsNullOrEmpty(args.PropertyName)
                || args.PropertyName
                    is nameof(MainViewModel.IsClosing)
                        or nameof(MainViewModel.IsPicking)
            )
                UpdateNotices();
            if (args.PropertyName == nameof(MainViewModel.ShowsStatus))
                PlaceNotices();
        };
        Root.GotFocus += (_, _) => UpdateNotices();
        Root.LostFocus += (_, _) =>
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                UpdateNotices
            );
        Root.SizeChanged += (_, _) => UpdateNotices();
        NoticeStack.SizeChanged += (_, _) => PlaceNotices();
        AppStatusLayer.SizeChanged += (_, _) => PlaceNotices();
        // A notice that arrives while the person works in another window
        // waits until they return, as Windows and VS Code notices do.
        Activated += (_, args) =>
        {
            _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            UpdateNoticeTimer();
        };
        _noticeTimer.Tick += (_, _) =>
        {
            UpdateNotices();
            if (
                NoticeStack.Visibility != Visibility.Visible
                || _noticeHovered
                || !_windowActive
                || HasNoticeKeyboardFocus()
            )
                return;
            foreach (var notice in Model.Notices.ToArray())
            {
                if (notice.Remaining is not { } remaining)
                    continue;
                notice.Remaining = remaining - _noticeTimer.Interval;
                if (notice.Remaining <= TimeSpan.Zero)
                    Model.DismissNotice(notice);
            }
        };
        Closed += (_, _) => _noticeTimer.Stop();
    }

    private void UpdateNoticeTimer()
    {
        if (_windowActive && Model.Notices.Count > 0)
            _noticeTimer.Start();
        else
            _noticeTimer.Stop();
    }

    // The popup's corner is the stack's bottom-right, so it follows the
    // stack's size as notices come and go. The stack sits above the message
    // bar while the bar shows, so neither covers the other.
    private void PlaceNotices()
    {
        var bar = Model.ShowsStatus ? AppStatusLayer.ActualHeight + 8 : 0;
        NoticeLayer.HorizontalOffset = -NoticeStack.Width;
        NoticeLayer.VerticalOffset = -NoticeStack.ActualHeight - bar;
    }

    // Only keyboard focus pauses the notices: a click on a notice's button
    // usually closes that notice, and focus that WinUI then moves to a
    // neighbouring notice is not the person reading it.
    private bool HasNoticeKeyboardFocus()
    {
        if (
            FocusManager.GetFocusedElement(Root.XamlRoot)
            is not Control { FocusState: FocusState.Keyboard } control
        )
            return false;
        DependencyObject? element = control;
        while (element is not null)
        {
            if (ReferenceEquals(element, NoticeStack))
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    // Notices show above an open dialog, so a result reported while it is
    // open is seen at once. New notices wait while they would cover the
    // editor the person is typing in.
    private void UpdateNotices()
    {
        if (Root.XamlRoot is null)
            return;
        NoticeStack.Width = Math.Min(400, Math.Max(0, Root.ActualWidth - 32));
        var visible =
            Model.Notices.Count > 0 && !HasEditorFocus() && !Model.IsClosing && !Model.IsPicking;
        if (!visible && _shownNotices.Count > 0)
        {
            var shown = _shownNotices.ToArray();
            _shownNotices.Clear();
            foreach (var notice in shown)
                if (Model.Notices.Contains(notice))
                    Model.DismissNotice(notice);
        }
        if (visible)
        {
            _shownNotices.Clear();
            _shownNotices.UnionWith(Model.Notices);
        }
        NoticeStack.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlaceNotices();
        NoticeLayer.IsOpen = visible;
        if (!visible)
            _noticeHovered = false;
    }

    // A popup draws above the popups opened before it, so the notices reopen
    // above each dialog that opens.
    private void RaiseNotices()
    {
        if (!NoticeLayer.IsOpen)
            return;
        NoticeLayer.IsOpen = false;
        NoticeLayer.IsOpen = true;
    }

    private void OnNoticePointerEntered(object sender, PointerRoutedEventArgs args) =>
        _noticeHovered = true;

    private void OnNoticePointerExited(object sender, PointerRoutedEventArgs args) =>
        _noticeHovered = false;

    // A tooltip that repeats readable text adds nothing, so a notice line has
    // one only while it is cut off. The tooltip follows the line's text,
    // which can change while it stays cut off.
    private void OnNoticeTextTrimmed(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        if (!sender.IsTextTrimmed)
        {
            ToolTipService.SetToolTip(sender, null);
            return;
        }
        var tip = new ToolTip();
        tip.SetBinding(
            ContentControl.ContentProperty,
            new Binding { Source = sender, Path = new PropertyPath(nameof(TextBlock.Text)) }
        );
        ToolTipService.SetToolTip(sender, tip);
    }

    private void OnDismissStatus(InfoBar sender, object args) => Model.DismissStatus();

    private void OnCloseNotice(object sender, RoutedEventArgs args) =>
        Model.DismissNotice((Notice)((FrameworkElement)sender).DataContext);
}
