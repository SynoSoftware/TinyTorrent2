using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Windows.Foundation;

namespace Syno.TinyTorrent.Controls;

public sealed partial class WorkStatus : UserControl
{
    private readonly TextBlock _probe = new();
    private Strings? _text;
    private bool _library;
    private bool _compact;
    private bool _minimal;
    internal BackgroundWork.Operation? Operation { get; private set; }
    public bool IsExpanded { get; set; }
    internal bool HasFocus => Cancel.FocusState != FocusState.Unfocused;
    internal bool CanFocus =>
        Visibility == Visibility.Visible
        && Cancel.IsEnabled
        && Cancel.Visibility == Visibility.Visible;
    internal event EventHandler? FocusInvalidated;

    public WorkStatus()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Subscribe();
            Refresh();
        };
        Unloaded += (_, _) => Unsubscribe();
    }

    internal void Show(BackgroundWork.Operation? operation, Strings text, bool library = false)
    {
        if (!ReferenceEquals(Operation, operation))
        {
            Unsubscribe();
            Operation = operation;
            if (IsLoaded)
                Subscribe();
        }
        _text = text;
        _library = library;
        Visibility = operation is null ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    internal void Fit(bool compact, bool minimal = false)
    {
        MaxWidth = double.PositiveInfinity;
        if (_compact == compact && _minimal == minimal)
            return;
        _compact = compact;
        _minimal = minimal;
        Refresh();
    }

    internal bool FocusCancel(FocusState focus = FocusState.Keyboard) =>
        CanFocus && Cancel.Focus(focus);

    private void Subscribe()
    {
        if (Operation is not { } operation)
            return;
        operation.PropertyChanged += OnObservation;
        // Recover focused Cancel before Button disables it for this event.
        operation.Cancel.CanExecuteChanged += OnCommand;
        Cancel.Command = operation.Cancel;
    }

    private void Unsubscribe()
    {
        Cancel.Command = null;
        if (Operation is not { } operation)
            return;
        operation.PropertyChanged -= OnObservation;
        operation.Cancel.CanExecuteChanged -= OnCommand;
    }

    private void OnObservation(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void OnCommand(object? sender, EventArgs args) => Refresh();

    internal static string Describe(WorkObservation observation, Strings text, bool library)
    {
        var title = text.Get(observation.Description, library);
        return observation.Execution switch
        {
            WorkExecution.Cancelling => text.Format("work", "cancelling", title),
            WorkExecution.Waiting => text.Format("work", "waiting", title),
            _ => title,
        };
    }

    private void Refresh()
    {
        if (HasFocus && (Operation is null || !Operation.Cancel.CanExecute(null)))
            FocusInvalidated?.Invoke(this, EventArgs.Empty);
        if (Operation is not { Current: var observation } operation || _text is not { } text)
            return;
        var identity = text.Get(observation.Description, _library);
        var caption = Describe(observation, text, _library);
        var fullCount = string.Empty;
        var shortCount = string.Empty;
        var widest = string.Empty;
        if (observation.Progress is WorkProgress.Fraction fraction)
        {
            fullCount = fraction.Unit switch
            {
                WorkUnit.Percent => text.Format(
                    "work",
                    fraction.Approximate ? "approximate" : "percent",
                    Math.Floor(100 * fraction.Completed / fraction.Total)
                ),
                WorkUnit.Bytes => text.Format(
                    "work",
                    "bytes_fraction",
                    text.Bytes((long)fraction.Completed),
                    text.Bytes((long)fraction.Total)
                ),
                _ => text.Format(
                    "work",
                    fraction.Unit == WorkUnit.Folders ? "folders" : "processed",
                    fraction.Completed,
                    fraction.Total
                ),
            };
            shortCount = fraction.Unit is WorkUnit.Files or WorkUnit.Folders
                ? text.Format("work", "fraction", fraction.Completed, fraction.Total)
                : fullCount;
            widest = fraction.Unit is WorkUnit.Files or WorkUnit.Folders
                ? text.Format(
                    "work",
                    _compact ? "fraction"
                        : fraction.Unit == WorkUnit.Folders ? "folders"
                        : "processed",
                    fraction.Total,
                    fraction.Total
                )
                : fullCount;
            Progress.Maximum = fraction.Total;
            Progress.Value = fraction.Completed;
        }
        else if (observation.Progress is WorkProgress.Amount amount)
            fullCount = shortCount =
                amount.Unit == WorkUnit.Bytes
                    ? text.Bytes((long)amount.Completed)
                    : text.Format("work", "amount", amount.Completed);
        var description = caption + (fullCount.Length == 0 ? string.Empty : " · " + fullCount);
        if (observation.Execution is WorkExecution.Waiting { RetryAt: { } retry })
            description = text.Format("work", "retry_at", description, retry.ToLocalTime());
        Label.Text = IsExpanded ? description : caption;
        Label.TextWrapping = IsExpanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Label.TextTrimming = IsExpanded ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        Count.Text = _compact ? shortCount : fullCount;
        Count.Visibility =
            IsExpanded || Count.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _probe.Style = Count.Style;
        _probe.Text = widest;
        _probe.InvalidateMeasure();
        _probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Count.MinWidth = _probe.DesiredSize.Width;
        Icon.Glyph =
            observation.Execution is WorkExecution.Waiting
                ? Lucide.Clock
                : observation.Description switch
                {
                    WorkDescription.Imdb => Lucide.Database,
                    WorkDescription.City => Lucide.MapPin,
                    WorkDescription.Metadata => Lucide.Download,
                    WorkDescription.Subtitles { Work: SubtitleWork.Discovery } =>
                        Lucide.FolderSearch,
                    WorkDescription.Subtitles => Lucide.Captions,
                    WorkDescription.FileProperties => Lucide.Files,
                    WorkDescription.Update => Lucide.RefreshCw,
                    WorkDescription.Sources => Lucide.FilePlus,
                    _ => Lucide.Wrench,
                };
        Icon.Visibility = _minimal && !IsExpanded ? Visibility.Collapsed : Visibility.Visible;
        Progress.IsIndeterminate =
            observation.Progress is not WorkProgress.Fraction
            && observation.Execution is not WorkExecution.Waiting;
        Progress.Visibility =
            observation.Execution is WorkExecution.Waiting
                && observation.Progress is not WorkProgress.Fraction
            || _minimal && !IsExpanded && !Progress.IsIndeterminate
                ? Visibility.Collapsed
                : Visibility.Visible;
        Cancel.Visibility = operation.HasCancel ? Visibility.Visible : Visibility.Collapsed;
        Cancel.IsEnabled = operation.Cancel.CanExecute(null);
        AutomationProperties.SetName(Label, description);
        AutomationProperties.SetName(Progress, description);
        var cancel = text.Format("work", "cancel", identity);
        AutomationProperties.SetName(Cancel, cancel);
        ToolTipService.SetToolTip(Cancel, cancel);
        ToolTipService.SetToolTip(this, description);
    }
}
