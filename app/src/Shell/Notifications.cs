using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;

namespace Syno.TinyTorrent.Shell;

// The window's toast stack: what each toast says, how long it stays and what
// closing it means. MainWindow says when time on screen passes and when the
// stack shows.
public sealed class Notifications
{
    // Fluent 2 toasts: at most four timed toasts show. A finished download
    // stays visible in the list after its toast goes.
    private const int MaxTimed = 4;

    private readonly MainViewModel _owner;
    private readonly Strings _text;
    private readonly ICommand _openErrorFolder;

    // The toasts on screen while the stack shows.
    private readonly HashSet<Toast> _shown = [];

    internal Notifications(MainViewModel owner, Strings text)
    {
        _owner = owner;
        _text = text;
        _openErrorFolder = new RelayCommand(
            () =>
            {
                if (Error?.Failure.Folder is { } folder)
                    _owner.RequestOpen(folder);
            },
            () => true
        );
    }

    // Oldest first, so the newest toast sits nearest the window corner.
    public ObservableCollection<Toast> Toasts { get; } = [];

    // At most one shows; a later failure replaces its failure in place.
    private Toast.CommandError? Error => Toasts.OfType<Toast.CommandError>().FirstOrDefault();

    internal event EventHandler<string>? AnnouncementRequested;

    internal void Announce(string message)
    {
        if (!_owner.IsClosed)
            AnnouncementRequested?.Invoke(this, message);
    }

    internal void AnnounceAccepted(string group, string key) =>
        Announce(_text.Format("outcomes", "accepted", _text.Get(group, key)));

    internal void Report(Exception error) => Report(error, ErrorTitle(error), null, null);

    internal void Report(
        Exception error,
        string title,
        Func<string>? subject,
        Func<string>? reason
    ) => Report(new CommandFailure(error, title, subject, reason, null, null));

    private void Report(CommandFailure failure)
    {
        if (_owner.IsClosed)
            return;
        var toast = Error;
        if (toast is null)
        {
            toast = new Toast.CommandError(failure);
            Toasts.Add(toast);
        }
        else
        {
            toast.Failure = failure;
        }
        Describe(toast);
        Announce(toast.Message);
    }

    private string ErrorTitle(Exception error) =>
        error is CommandException { Command: { } command }
        && _text.Find("notifications", "failed_" + command) is not null
            ? "failed_" + command
            : "failed";

    // The error lasts until the person closes it or a later command succeeds.
    internal void ClearError()
    {
        if (Error is { } toast)
            Toasts.Remove(toast);
    }

    internal void Receive(JsonElement message)
    {
        var kind = message.GetProperty("kind").GetString();
        var name = message.GetProperty("name").GetString() ?? string.Empty;
        var torrentId = message.GetProperty("torrent_id").GetString() ?? string.Empty;
        var count = message.TryGetProperty("count", out var total) ? total.GetInt32() : 1;
        var @switch = message.GetProperty("switch").GetString();
        if (kind == "completed")
            ShowCompletion(torrentId, name, @switch);
        else if (kind is "error" or "add_failed" or "delete_failed")
        {
            var detail = message.GetProperty("detail").GetString() ?? string.Empty;
            var code =
                message.GetProperty("code").GetString() is { Length: > 0 } problem ? problem
                : kind == "error"
                    ? torrentId.Length == 0 ? "unknown"
                        : "torrent_error"
                : kind;
            var error = new CommandException(code, detail, _text);
            // The title already says what failed, so the engine's reason
            // alone is the body; without one, the error code's text is.
            string Reason()
            {
                var reason =
                    kind == "delete_failed" && detail == "recycle_failed"
                        ? _text.Get("errors", "recycle_failed")
                    : detail.Length > 0 ? detail
                    : _text.Error(error);
                return count > 1
                    ? _text.Format(
                        "errors",
                        "detail",
                        reason,
                        _text.Format("errors", "additional_problems", count - 1)
                    )
                    : reason;
            }
            var title = kind switch
            {
                "add_failed" => "not_added",
                "delete_failed" => "not_deleted",
                _ => "torrent_stopped",
            };
            var folder = message.GetProperty("folder").GetString();
            Report(
                new CommandFailure(
                    error,
                    title,
                    () => name,
                    Reason,
                    folder is { Length: > 0 } ? folder : null,
                    @switch
                )
            );
        }
    }

    // A torrent that finishes again replaces its earlier toast.
    private void ShowCompletion(string torrentId, string name, string? @switch)
    {
        if (
            Toasts
                .OfType<Toast.Completion>()
                .FirstOrDefault(shown => shown.TorrentId == torrentId) is
            { } earlier
        )
            Toasts.Remove(earlier);
        var toast = new Toast.Completion(torrentId, name, @switch);
        toast.Action = new RelayCommand(
            () => OpenCompletion(toast),
            () => _owner.CanEdit && _owner.Find(torrentId) is not null
        );
        Show(toast);
        Announce(_text.Format("notifications", "completed", name));
    }

    // Opening the folder is what the toast offers, so the toast closes with it.
    private async Task OpenCompletion(Toast.Completion toast)
    {
        if (!_owner.CanEdit || _owner.Find(toast.TorrentId) is not { } torrent)
            return;
        Toasts.Remove(toast);
        await _owner.OpenTorrent(torrent, true);
    }

    internal void ShowOutcome(string group, string key, string subject)
    {
        var toast = new Toast.Outcome(group, key, subject);
        Show(toast);
        Announce(toast.Title);
    }

    // Work that fails again, such as an automatic run that restarts, replaces
    // its earlier failure, so lasting toasts cannot pile up.
    internal void ShowWorkResult(WorkResult result)
    {
        if (
            result.Outcome == WorkOutcome.Failed
            && Toasts
                .OfType<Toast.Work>()
                .FirstOrDefault(shown =>
                    shown.Result.Outcome == WorkOutcome.Failed
                    && shown.Result.Observation.Description == result.Observation.Description
                ) is
            { } earlier
        )
            Toasts.Remove(earlier);
        var toast = new Toast.Work(result);
        Show(toast);
        Announce(
            string.Join(
                Environment.NewLine,
                new[] { toast.Title, toast.Subject, toast.Message }.Where(value =>
                    value.Length > 0
                )
            )
        );
    }

    // Fetch metadata leaves a file unidentified when its title reached the
    // search limit, which only raising the limit lets the batch search again.
    internal void ShowSearchLimit(int files)
    {
        var toast = new Toast.SearchLimit(files);
        // Changing the limit is what the toast offers, so it closes with it.
        toast.Action = new RelayCommand(
            () =>
            {
                Toasts.Remove(toast);
                return _owner.RequestSettings(new(SettingsCategory.Plugins, "lookups"));
            },
            () => true
        );
        Show(toast);
        Announce(toast.Title);
    }

    // Beyond the limit the oldest timed toast gives way to the new one.
    private void Show(Toast toast)
    {
        if (_owner.IsClosed)
            return;
        Describe(toast);
        Toasts.Add(toast);
        if (
            Toasts.Where(shown => shown.Remaining is not null).ToArray() is
            { Length: > MaxTimed } timed
        )
            Toasts.Remove(timed[0]);
    }

    // Subtitle work fails in the background, often for several movies at
    // once, so one toast names the latest file and counts the others.
    internal void ShowSubtitleFailure(Subtitles.FileFailure failure)
    {
        if (_owner.IsClosed)
            return;
        var toast = Toasts.OfType<Toast.SubtitleError>().FirstOrDefault();
        if (toast is null)
        {
            toast = new Toast.SubtitleError(failure);
            Toasts.Add(toast);
        }
        else
        {
            toast.Latest = failure;
            toast.Count++;
        }
        Describe(toast);
        Announce(toast.Message);
    }

    // A toast for a condition shows while the condition holds, and every
    // toast follows the model's state and language, so MainViewModel calls
    // this on each change instead of each place that changes the state.
    internal void Sync()
    {
        var resume = Toasts.OfType<Toast.Resume>().FirstOrDefault();
        if (_owner.HasResumeToast && resume is null)
            Toasts.Add(new Toast.Resume());
        else if (!_owner.HasResumeToast && resume is not null)
            Toasts.Remove(resume);
        foreach (var toast in Toasts)
        {
            Describe(toast);
            if (toast is Toast.Completion { Action: RelayCommand open })
                open.Refresh();
        }
    }

    // Counts time on screen down. MainWindow calls it only while the person
    // can see the stack and is not reading or using it.
    internal void Elapse(TimeSpan interval)
    {
        foreach (var toast in Toasts.ToArray())
        {
            if (toast.Remaining is not { } remaining)
                continue;
            toast.Remaining = remaining - interval;
            if (toast.Remaining <= TimeSpan.Zero)
                Dismiss(toast);
        }
    }

    // Timed toasts the person saw retire when the stack hides, so showing it
    // again does not replay them. Lasting toasts show again, and toasts that
    // arrived while it was hidden wait to be shown.
    internal void SetVisible(bool visible)
    {
        if (visible)
        {
            _shown.Clear();
            _shown.UnionWith(Toasts);
            return;
        }
        var shown = _shown.ToArray();
        _shown.Clear();
        foreach (var toast in shown)
            if (toast.Remaining is not null && Toasts.Contains(toast))
                Dismiss(toast);
    }

    // Closing a condition's toast ends what it reports, so the condition
    // does not bring the toast back.
    internal void Dismiss(Toast toast)
    {
        if (toast is Toast.Resume)
            _owner.DismissResume();
        else
            Toasts.Remove(toast);
    }

    private void Describe(Toast toast)
    {
        switch (toast)
        {
            case Toast.Work work:
            {
                var result = work.Result;
                work.Title = _text.Get(
                    "work",
                    "result_" + result.Outcome.ToString().ToLowerInvariant()
                );
                work.Subject = _text.Get(result.Observation.Description);
                var progress = result.Observation.Progress;
                var summary = result.SummaryKey is { } key
                    ? _text.Format("work", key, result.Arguments)
                    : progress is WorkProgress.Fraction { Unit: WorkUnit.Files or WorkUnit.Folders } fraction
                        ? _text.Format(
                            "work",
                            fraction.Unit == WorkUnit.Folders ? "folders" : "processed",
                            fraction.Completed,
                            fraction.Total
                        )
                        : string.Empty;
                var detail = result.Error is { } error ? _text.Error(error) : string.Empty;
                work.Message =
                    detail.Length == 0 ? summary
                    : summary.Length == 0 ? detail
                    : _text.Format("errors", "detail", summary, detail);
                break;
            }
            case Toast.Outcome outcome:
                outcome.Title = _text.Get(outcome.Group, outcome.Key);
                break;
            case Toast.SearchLimit limit:
                limit.Title = _text.Get("notifications", "search_limit");
                limit.Message = _text.FormatCount(
                    "notifications",
                    "search_limit_files",
                    limit.Files
                );
                limit.ActionText = _text.Get("notifications", "settings");
                limit.ActionTip = _text.Get("notifications", "search_limit_tip");
                limit.ActionGlyph = Syno.Lucide.Settings;
                break;
            case Toast.Resume resume:
                resume.Title = _text.Get("notifications", "delayed");
                resume.Message = _owner.ResumeMessage;
                resume.Action = _owner.ResolvePause;
                resume.ActionText = _owner.PauseAction;
                resume.ActionTip = _owner.PauseActionTip;
                resume.ActionGlyph = _owner.PauseActionGlyph;
                break;
            case Toast.CommandError error:
                var failure = error.Failure;
                error.Title = _text.Get("notifications", failure.Title);
                error.Subject = failure.Subject?.Invoke() ?? string.Empty;
                error.Message = failure.Reason?.Invoke() ?? _text.Error(failure.Error);
                error.Action = failure.Folder is null ? null : _openErrorFolder;
                error.ActionText = _text.Get("commands", "open_folder");
                error.ActionTip = _text.Get("notifications", "files_folder_tip");
                error.ActionGlyph = Syno.Lucide.FolderOpen;
                error.Switch = failure.Switch;
                break;
            case Toast.SubtitleError subtitle:
                var latest = subtitle.Latest;
                var reason = _text.Get(
                    "subtitles",
                    "failure_" + latest.Reason.ToString().ToLowerInvariant()
                );
                subtitle.Title = _text.Get(
                    "notifications",
                    latest.Reason == SubtitleFailure.Delete
                        ? "subtitle_not_deleted"
                        : "subtitle_not_saved"
                );
                subtitle.Subject = Path.GetFileName(latest.Path);
                subtitle.Message =
                    subtitle.Count == 1
                        ? reason
                        : _text.Format(
                            "errors",
                            "detail",
                            reason,
                            _text.Format("errors", "additional_problems", subtitle.Count - 1)
                        );
                subtitle.Action = _owner.ShowSubtitleSettings;
                subtitle.ActionText = _text.Get("notifications", "settings");
                subtitle.ActionTip = _text.Get("notifications", "subtitles_tip");
                subtitle.ActionGlyph = Syno.Lucide.Settings;
                break;
            case Toast.Completion completion:
                completion.Title = _text.Get("notifications", "finished");
                completion.ActionText = _text.Get("commands", "open_folder");
                completion.ActionTip = _text.Format(
                    "notifications",
                    "open_folder_tip",
                    completion.Subject
                );
                completion.ActionGlyph = Syno.Lucide.FolderOpen;
                break;
        }
        toast.CloseText = _text.Get("commands", "close");
        toast.MoreText = _text.Get("notifications", "more");
    }
}

// A failed command as its toast describes it: a title key from the
// notifications text, the torrent it names, the reason when the engine sent
// one instead of an error code, the folder where the person finishes a failed
// deletion, and the engine's word for the Settings switch that turns off a
// problem the engine reports.
internal sealed record CommandFailure(
    Exception Error,
    string Title,
    Func<string>? Subject,
    Func<string>? Reason,
    string? Folder,
    string? Switch
);

// One message in the window's toast stack. Each kind carries the facts it
// reports, so closing the toast is what forgets them. Notifications describes
// it again on every change, so its text follows the state and the language.
public abstract class Toast : INotifyPropertyChanged
{
    // Fluent 2: a timed toast closes after seven seconds on screen.
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(7);

    private string _title = string.Empty;
    private string _subject = string.Empty;
    private string _message = string.Empty;
    private ICommand? _action;
    private string _actionText = string.Empty;
    private string _actionTip = string.Empty;
    private string _actionGlyph = string.Empty;
    private string _closeText = string.Empty;
    private string _moreText = string.Empty;
    private string? _switch;

    private Toast(InfoBarSeverity severity, TimeSpan? lifetime)
    {
        Severity = severity;
        Remaining = lifetime;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // The time on screen before the toast closes by itself; null for a
    // toast that stays until it is closed or its condition ends.
    internal TimeSpan? Remaining { get; set; }

    public InfoBarSeverity Severity { get; }
    public bool IsSuccess => Severity == InfoBarSeverity.Success;
    public bool IsInformational => Severity == InfoBarSeverity.Informational;
    public bool IsWarning => Severity == InfoBarSeverity.Warning;
    public bool IsError => Severity == InfoBarSeverity.Error;

    // Fluent 2 toast text: Title says what happened in one line, Subject
    // names what it happened to, such as a torrent, in one line, and Message
    // gives the reason or the next step.
    public string Title
    {
        get => _title;
        internal set => Set(ref _title, value);
    }
    public string Subject
    {
        get => _subject;
        internal set
        {
            if (Set(ref _subject, value))
                Changed(nameof(HasSubject));
        }
    }
    public bool HasSubject => Subject.Length > 0;
    public string Message
    {
        get => _message;
        internal set
        {
            if (Set(ref _message, value))
                Changed(nameof(HasMessage));
        }
    }
    public bool HasMessage => Message.Length > 0;
    public ICommand? Action
    {
        get => _action;
        internal set
        {
            if (Set(ref _action, value))
                Changed(nameof(HasAction));
        }
    }
    public bool HasAction => Action is not null;
    public string ActionText
    {
        get => _actionText;
        internal set => Set(ref _actionText, value);
    }
    public string ActionTip
    {
        get => _actionTip;
        internal set => Set(ref _actionTip, value);
    }
    public string ActionGlyph
    {
        get => _actionGlyph;
        internal set => Set(ref _actionGlyph, value);
    }
    public string CloseText
    {
        get => _closeText;
        internal set => Set(ref _closeText, value);
    }
    public string MoreText
    {
        get => _moreText;
        internal set => Set(ref _moreText, value);
    }

    // The engine's word for the Settings switch that turns off toasts like
    // this one, which the More menu offers; null when none does, such as for
    // a failure of the person's own command.
    internal string? Switch
    {
        get => _switch;
        set
        {
            if (Set(ref _switch, value))
                Changed(nameof(HasSwitch));
        }
    }
    public bool HasSwitch => Switch is not null;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Changed(property);
        return true;
    }

    private void Changed(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    // Says when the torrents the person resumed start. It shows while
    // MainViewModel.HasResumeToast holds.
    internal sealed class Resume() : Toast(InfoBarSeverity.Informational, null);

    internal sealed class CommandError(CommandFailure failure)
        : Toast(InfoBarSeverity.Error, null)
    {
        internal CommandFailure Failure { get; set; } = failure;
    }

    // The latest subtitle file that failed, and how many failed.
    internal sealed class SubtitleError(Subtitles.FileFailure latest)
        : Toast(InfoBarSeverity.Error, null)
    {
        internal Subtitles.FileFailure Latest { get; set; } = latest;
        internal int Count { get; set; } = 1;
    }

    internal sealed class Completion : Toast
    {
        internal Completion(string torrentId, string name, string? @switch)
            : base(InfoBarSeverity.Success, Lifetime)
        {
            TorrentId = torrentId;
            Subject = name;
            Switch = @switch;
        }

        internal string TorrentId { get; }
    }

    // A result the person asked for, such as a passed check. It closes by
    // itself like a finished download, because nothing remains to do.
    internal sealed class Outcome : Toast
    {
        internal Outcome(string group, string key, string subject)
            : base(InfoBarSeverity.Success, Lifetime)
        {
            Group = group;
            Key = key;
            Subject = subject;
        }

        // The text key of the title.
        internal string Group { get; }
        internal string Key { get; }
    }

    internal sealed class SearchLimit(int files) : Toast(InfoBarSeverity.Warning, Lifetime)
    {
        internal int Files { get; } = files;
    }

    // A failed result stays until the person closes it, because a
    // disappearing message cannot be the only explanation of failed work.
    internal sealed class Work(WorkResult result)
        : Toast(
            result.Outcome switch
            {
                WorkOutcome.Completed => InfoBarSeverity.Success,
                WorkOutcome.Cancelled => InfoBarSeverity.Informational,
                _ => InfoBarSeverity.Error,
            },
            result.Outcome == WorkOutcome.Failed ? null : Lifetime
        )
    {
        internal WorkResult Result { get; } = result;
    }
}
