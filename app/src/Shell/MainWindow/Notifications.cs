using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _noticeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };
    private bool _noticeHovered;
    // The More button whose menu is open. The pointer leaves the stack for
    // the menu, so the menu pauses the notices itself, or its notice would
    // close under it, and keeps its More showing.
    private Button? _noticeMenuButton;
    private FrameworkElement? _pointedToast;
    private bool _windowActive = true;

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
        Model.Notifications.Notices.CollectionChanged += (_, _) =>
        {
            UpdateNoticeTimer();
            if (Model.Notifications.Notices.Count == 0)
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
                || _noticeMenuButton is not null
                || !_windowActive
                || HasNoticeKeyboardFocus()
            )
                return;
            Model.Notifications.Elapse(_noticeTimer.Interval);
        };
        Closed += (_, _) => _noticeTimer.Stop();
    }

    private void UpdateNoticeTimer()
    {
        if (_windowActive && Model.Notifications.Notices.Count > 0)
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
        return FocusManager.GetFocusedElement(Root.XamlRoot)
                is Control { FocusState: FocusState.Keyboard } control
            && IsInside(control, NoticeStack);
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, container))
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnToastPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _pointedToast = (FrameworkElement)sender;
        ShowNoticeMore(_pointedToast);
    }

    private void OnToastPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (ReferenceEquals(_pointedToast, sender))
            _pointedToast = null;
        ShowNoticeMore((FrameworkElement)sender);
    }

    private void OnToastGotFocus(object sender, RoutedEventArgs args) =>
        ShowNoticeMore((FrameworkElement)sender);

    // Focus has not reached its next element yet when LostFocus runs.
    private void OnToastLostFocus(object sender, RoutedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => ShowNoticeMore((FrameworkElement)sender));

    // More shows while the pointer is over its toast, focus is inside it or
    // its menu is open, as the engine's popup shows More only under the
    // pointer. Hidden, it keeps its space, so the title does not reflow and
    // Tab still reaches it.
    private void ShowNoticeMore(FrameworkElement toast)
    {
        if (toast.FindName("NoticeMore") is not Button more || toast.XamlRoot is null)
            return;
        var shows =
            ReferenceEquals(_pointedToast, toast)
            || ReferenceEquals(_noticeMenuButton, more)
            || IsInside(FocusManager.GetFocusedElement(toast.XamlRoot) as DependencyObject, toast);
        more.Opacity = shows ? 1 : 0;
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
            Model.Notifications.Notices.Count > 0
            && !HasEditorFocus()
            && !Model.IsClosing
            && !Model.IsPicking;
        Model.Notifications.SetVisible(visible);
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
        Model.Notifications.Dismiss((Notice)((FrameworkElement)sender).DataContext);

    private void OnNoticeMore(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        var notice = (Notice)button.DataContext;
        if (notice.Switch is not { } notices)
            return;
        var menu = MenuItems.Flyout(
            () =>
                [
                    new MenuCommand(
                        Model.Text.Get("notifications", "off_" + notices),
                        Lucide.BellMinus,
                        new RelayCommand(() => Model.TurnOffNotices(notice), () => Model.CanEdit)
                    ),
                    new MenuCommand(
                        Model.Text.Get("notifications", "off_all"),
                        Lucide.BellOff,
                        new RelayCommand(() => Model.TurnOffAllNotices(notice), () => Model.CanEdit)
                    ),
                    new MenuCommand(
                        Model.Text.Get("notifications", "notification_settings"),
                        Lucide.Settings,
                        new RelayCommand(() => Model.ShowNotificationSettings(notice), () => true)
                    ),
                ],
            () => Model.Text
        );
        menu.Opened += (_, _) => _noticeMenuButton = button;
        menu.Closed += (_, _) =>
        {
            _noticeMenuButton = null;
            // Focus returns to More after Closed, so the toast decides then.
            DispatcherQueue.TryEnqueue(() =>
            {
                DependencyObject? toast = button;
                while (toast is not null and not FrameworkElement { Name: "NoticeToast" })
                    toast = VisualTreeHelper.GetParent(toast);
                if (toast is FrameworkElement shown)
                    ShowNoticeMore(shown);
            });
        };
        // The toasts sit at the window's right edge, so the menu grows left
        // from More instead of past the edge, where the window cuts it off.
        menu.ShowAt(
            button,
            new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedRight }
        );
    }
}
