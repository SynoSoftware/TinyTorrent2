using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _completionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _completionTicks;
    private bool _completionHovered;

    private void ConfigureNotifications()
    {
        Feedback.RegisterPropertyChangedCallback(InfoBar.MessageProperty, (_, _) =>
        {
            if (!Feedback.IsOpen || Feedback.Message.Length == 0) return;
            // InfoBar does not announce message changes while it stays open.
            Feedback.IsOpen = false;
            Feedback.IsOpen = true;
        });
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CompletionText))
            {
                _completionTicks = 32;
                if (Model.HasCompletion) _completionTimer.Start(); else _completionTimer.Stop();
            }
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is nameof(MainViewModel.CompletionText)
                or nameof(MainViewModel.HasCompletion) or nameof(MainViewModel.IsClosing) or nameof(MainViewModel.IsPicking))
                UpdateCompletion();
        };
        Model.TextChanged += (_, _) => UpdateCompletion();
        CompletionNotice.Loaded += (_, _) => UpdateCompletion();
        CompletionNotice.PointerEntered += (_, _) => _completionHovered = true;
        CompletionNotice.PointerExited += (_, _) => _completionHovered = false;
        Root.GotFocus += (_, _) => UpdateCompletion();
        Root.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateCompletion);
        Root.SizeChanged += (_, _) => UpdateCompletion();
        _completionTimer.Tick += (_, _) =>
        {
            UpdateCompletion();
            if (CompletionNotice.Visibility == Visibility.Visible && !_completionHovered && !HasCompletionFocus() && --_completionTicks <= 0)
                Model.DismissCompletion();
        };
        Closed += (_, _) => _completionTimer.Stop();
    }

    private bool HasCompletionFocus()
    {
        var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (ReferenceEquals(element, CompletionNotice)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void UpdateCompletion()
    {
        if (Root.XamlRoot is null) return;
        CompletionNotice.Width = Math.Min(560, Math.Max(0, Root.ActualWidth - 32));
        var visible = Model.HasCompletion && !HasDialog && !HasEditorFocus() && !Model.IsClosing && !Model.IsPicking;
        CompletionNotice.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CompletionNotice.IsOpen = visible;
        if (!visible) _completionHovered = false;
    }

    private void OnDismissCompletion(InfoBar sender, object args) => Model.DismissCompletion();
}
