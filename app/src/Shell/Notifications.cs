using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;

namespace Syno.TinyTorrent.Shell;

// The window's toast stack: what each notice says, how long it stays and what
// closing it means. MainWindow says when time on screen passes and when the
// stack shows.
public sealed class Notifications
{
    // Fluent 2 toasts: at most four timed notices show, and each closes after
    // seven seconds on screen. A finished download stays visible in the list
    // after its notice goes.
    private const int MaxTimed = 4;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(7);

    private readonly MainViewModel _owner;
    private readonly Strings _text;
    private readonly ICommand _openErrorFolder;

    // The notices on screen while the stack shows.
    private readonly HashSet<Notice> _shown = [];
    private CommandError? _error;
    private Subtitles.FileFailure? _subtitleFailure;
    private int _subtitleFailureCount;

    internal Notifications(MainViewModel owner, Strings text)
    {
        _owner = owner;
        _text = text;
        _openErrorFolder = new RelayCommand(
            () =>
            {
                if (_error?.Folder is { } folder)
                    _owner.RequestOpen(folder);
            },
            () => true
        );
    }

    // Oldest first, so the newest notice sits nearest the window corner.
    public ObservableCollection<Notice> Notices { get; } = [];

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
    ) => Report(new CommandError(error, title, subject, reason, null, null));

    private void Report(CommandError error)
    {
        if (_owner.IsClosed)
            return;
        _error = error;
        var notice = ShowLasting(NoticeKind.CommandError);
        Describe(notice);
        Announce(notice.Message);
    }

    // The one notice of a kind that stays, added as the newest when none shows.
    private Notice ShowLasting(NoticeKind kind)
    {
        var notice = Notices.FirstOrDefault(shown => shown.Kind == kind);
        if (notice is null)
        {
            notice = new Notice(kind);
            Notices.Add(notice);
        }
        return notice;
    }

    private string ErrorTitle(Exception error) =>
        error is CommandException { Command: { } command }
        && _text.Find("notifications", "failed_" + command) is not null
            ? "failed_" + command
            : "failed";

    // The error lasts until the person closes it or a later command succeeds.
    internal void ClearError()
    {
        _error = null;
        if (Notices.FirstOrDefault(shown => shown.Kind == NoticeKind.CommandError) is { } notice)
            Notices.Remove(notice);
    }

    internal void Receive(JsonElement notice)
    {
        var kind = notice.GetProperty("kind").GetString();
        var name = notice.GetProperty("name").GetString() ?? string.Empty;
        var torrentId = notice.GetProperty("torrent_id").GetString() ?? string.Empty;
        var count = notice.TryGetProperty("count", out var total) ? total.GetInt32() : 1;
        var notices = notice.GetProperty("switch").GetString();
        if (kind == "completed")
            ShowCompletion(torrentId, name, notices);
        else if (kind is "error" or "add_failed" or "delete_failed")
        {
            var detail = notice.GetProperty("detail").GetString() ?? string.Empty;
            var code =
                notice.GetProperty("code").GetString() is { Length: > 0 } problem ? problem
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
            var folder = notice.GetProperty("folder").GetString();
            Report(
                new CommandError(
                    error,
                    title,
                    () => name,
                    Reason,
                    folder is { Length: > 0 } ? folder : null,
                    notices
                )
            );
        }
    }

    // A torrent that finishes again replaces its earlier notice.
    private void ShowCompletion(string torrentId, string name, string? notices)
    {
        if (
            Notices.FirstOrDefault(shown =>
                shown.Kind == NoticeKind.Completion && shown.TorrentId == torrentId
            ) is
            { } earlier
        )
            Notices.Remove(earlier);
        var notice = new Notice(NoticeKind.Completion, torrentId, Lifetime)
        {
            Subject = name,
            Switch = notices,
        };
        notice.Action = new RelayCommand(
            () => OpenCompletion(notice),
            () => _owner.CanEdit && _owner.Find(torrentId) is not null
        );
        Show(notice);
        Announce(_text.Format("notifications", "completed", name));
    }

    // Opening the folder is what the notice offers, so the notice closes with it.
    private async Task OpenCompletion(Notice notice)
    {
        if (!_owner.CanEdit || _owner.Find(notice.TorrentId) is not { } torrent)
            return;
        Notices.Remove(notice);
        await _owner.OpenTorrent(torrent, true);
    }

    // A result the person asked for, such as a passed check. It closes by
    // itself like a finished download, because nothing remains to do.
    internal void ShowOutcome(string group, string key, string subject)
    {
        var notice = new Notice(NoticeKind.Outcome, lifetime: Lifetime)
        {
            Severity = InfoBarSeverity.Success,
            Group = group,
            Key = key,
            Subject = subject,
        };
        Show(notice);
        Announce(notice.Title);
    }

    // A failed result stays until the person closes it, because a
    // disappearing message cannot be the only explanation of failed work.
    // Work that fails again, such as an automatic run that restarts, replaces
    // its earlier failure, so lasting notices cannot pile up.
    internal void ShowWorkResult(WorkResult result)
    {
        if (
            result.Outcome == WorkOutcome.Failed
            && Notices.FirstOrDefault(shown =>
                shown.Result is { Outcome: WorkOutcome.Failed } failed
                && failed.Observation.Description == result.Observation.Description
            ) is
            { } earlier
        )
            Notices.Remove(earlier);
        var notice = new Notice(
            NoticeKind.WorkResult,
            lifetime: result.Outcome == WorkOutcome.Failed ? null : Lifetime
        )
        {
            Result = result,
            Severity = result.Outcome switch
            {
                WorkOutcome.Completed => InfoBarSeverity.Success,
                WorkOutcome.Cancelled => InfoBarSeverity.Informational,
                _ => InfoBarSeverity.Error,
            },
        };
        Show(notice);
        Announce(
            string.Join(
                Environment.NewLine,
                new[] { notice.Title, notice.Subject, notice.Message }.Where(value =>
                    value.Length > 0
                )
            )
        );
    }

    // Fetch metadata leaves a file unidentified when its title reached the
    // search limit, which only raising the limit lets the batch search again.
    internal void ShowSearchLimit(int files)
    {
        var notice = new Notice(NoticeKind.SearchLimit, lifetime: Lifetime)
        {
            Severity = InfoBarSeverity.Warning,
            Count = files,
            ActionGlyph = Syno.Lucide.Settings,
        };
        // Changing the limit is what the notice offers, so it closes with it.
        notice.Action = new RelayCommand(
            () =>
            {
                Notices.Remove(notice);
                return _owner.RequestSettings(new(SettingsCategory.Plugins, "lookups"));
            },
            () => true
        );
        Show(notice);
        Announce(notice.Title);
    }

    // Beyond the limit the oldest timed notice gives way to the new one.
    private void Show(Notice notice)
    {
        if (_owner.IsClosed)
            return;
        Describe(notice);
        Notices.Add(notice);
        if (
            Notices.Where(shown => shown.Remaining is not null).ToArray() is
            { Length: > MaxTimed } timed
        )
            Notices.Remove(timed[0]);
    }

    // Subtitle work fails in the background, often for several movies at
    // once, so one notice names the latest file and counts the others.
    internal void ShowSubtitleFailure(Subtitles.FileFailure failure)
    {
        if (_owner.IsClosed)
            return;
        _subtitleFailure = failure;
        _subtitleFailureCount++;
        var notice = ShowLasting(NoticeKind.SubtitleError);
        Describe(notice);
        Announce(notice.Message);
    }

    // A notice for a condition shows while the condition holds, and every
    // notice follows the model's state and language, so MainViewModel calls
    // this on each change instead of each place that changes the state.
    internal void Sync()
    {
        var resume = Notices.FirstOrDefault(notice => notice.Kind == NoticeKind.Resume);
        if (_owner.HasResumeNotice && resume is null)
            Notices.Add(new Notice(NoticeKind.Resume));
        else if (!_owner.HasResumeNotice && resume is not null)
            Notices.Remove(resume);
        foreach (var notice in Notices)
        {
            Describe(notice);
            if (notice.Kind == NoticeKind.Completion)
                ((RelayCommand)notice.Action!).Refresh();
        }
    }

    // Counts time on screen down. MainWindow calls it only while the person
    // can see the stack and is not reading or using it.
    internal void Elapse(TimeSpan interval)
    {
        foreach (var notice in Notices.ToArray())
        {
            if (notice.Remaining is not { } remaining)
                continue;
            notice.Remaining = remaining - interval;
            if (notice.Remaining <= TimeSpan.Zero)
                Dismiss(notice);
        }
    }

    // Timed notices the person saw retire when the stack hides, so showing it
    // again does not replay them. Lasting notices show again, and notices
    // that arrived while it was hidden wait to be shown.
    internal void SetVisible(bool visible)
    {
        if (visible)
        {
            _shown.Clear();
            _shown.UnionWith(Notices);
            return;
        }
        var shown = _shown.ToArray();
        _shown.Clear();
        foreach (var notice in shown)
            if (notice.Remaining is not null && Notices.Contains(notice))
                Dismiss(notice);
    }

    // Closing a condition's notice ends what it reports, so the condition
    // does not bring the notice back.
    internal void Dismiss(Notice notice)
    {
        switch (notice.Kind)
        {
            case NoticeKind.Resume:
                _owner.DismissResume();
                break;
            case NoticeKind.CommandError:
                ClearError();
                break;
            case NoticeKind.SubtitleError:
                _subtitleFailure = null;
                _subtitleFailureCount = 0;
                Notices.Remove(notice);
                break;
            default:
                Notices.Remove(notice);
                break;
        }
    }

    private void Describe(Notice notice)
    {
        switch (notice.Kind)
        {
            case NoticeKind.WorkResult:
            {
                var result = notice.Result!;
                notice.Title = _text.Get(
                    "work",
                    "result_" + result.Outcome.ToString().ToLowerInvariant()
                );
                notice.Subject = _text.Get(result.Observation.Description);
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
                notice.Message =
                    detail.Length == 0 ? summary
                    : summary.Length == 0 ? detail
                    : _text.Format("errors", "detail", summary, detail);
                break;
            }
            case NoticeKind.Outcome:
                notice.Title = _text.Get(notice.Group, notice.Key);
                break;
            case NoticeKind.SearchLimit:
                notice.Title = _text.Get("notifications", "search_limit");
                notice.Message = _text.FormatCount(
                    "notifications",
                    "search_limit_files",
                    notice.Count
                );
                notice.ActionText = _text.Get("notifications", "settings");
                notice.ActionTip = _text.Get("notifications", "search_limit_tip");
                break;
            case NoticeKind.Resume:
                notice.Severity = InfoBarSeverity.Informational;
                notice.Title = _text.Get("notifications", "delayed");
                notice.Message = _owner.ResumeNotice;
                notice.Action = _owner.ResolvePause;
                notice.ActionText = _owner.PauseAction;
                notice.ActionTip = _owner.PauseActionTip;
                notice.ActionGlyph = _owner.PauseActionGlyph;
                break;
            case NoticeKind.CommandError:
                // The notice shows exactly while an error is recorded.
                var failure = _error!;
                notice.Severity = InfoBarSeverity.Error;
                notice.Title = _text.Get("notifications", failure.Title);
                notice.Subject = failure.Subject?.Invoke() ?? string.Empty;
                notice.Message = failure.Reason?.Invoke() ?? _text.Error(failure.Error);
                notice.Action = failure.Folder is null ? null : _openErrorFolder;
                notice.ActionText = _text.Get("commands", "open_folder");
                notice.ActionTip = _text.Get("notifications", "files_folder_tip");
                notice.ActionGlyph = Syno.Lucide.FolderOpen;
                notice.Switch = failure.Switch;
                break;
            case NoticeKind.SubtitleError:
                var latest = _subtitleFailure!;
                var reason = _text.Get(
                    "subtitles",
                    "failure_" + latest.Reason.ToString().ToLowerInvariant()
                );
                notice.Severity = InfoBarSeverity.Error;
                notice.Title = _text.Get(
                    "notifications",
                    latest.Reason == SubtitleFailure.Delete
                        ? "subtitle_not_deleted"
                        : "subtitle_not_saved"
                );
                notice.Subject = Path.GetFileName(latest.Path);
                notice.Message =
                    _subtitleFailureCount == 1
                        ? reason
                        : _text.Format(
                            "errors",
                            "detail",
                            reason,
                            _text.Format(
                                "errors",
                                "additional_problems",
                                _subtitleFailureCount - 1
                            )
                        );
                notice.Action = _owner.ShowSubtitleSettings;
                notice.ActionText = _text.Get("notifications", "settings");
                notice.ActionTip = _text.Get("notifications", "subtitles_tip");
                notice.ActionGlyph = Syno.Lucide.Settings;
                break;
            case NoticeKind.Completion:
                notice.Severity = InfoBarSeverity.Success;
                notice.Title = _text.Get("notifications", "finished");
                notice.ActionText = _text.Get("commands", "open_folder");
                notice.ActionTip = _text.Format(
                    "notifications",
                    "open_folder_tip",
                    notice.Subject
                );
                notice.ActionGlyph = Syno.Lucide.FolderOpen;
                break;
        }
        notice.CloseText = _text.Get("commands", "close");
        notice.MoreText = _text.Get("notifications", "more");
    }

    // A failed command as its notice describes it: a title key from the
    // notifications text, the torrent it names, the reason when the engine
    // sent one instead of an error code, the folder where the person finishes
    // a failed deletion, and the engine's word for the Settings switch that
    // turns off a problem the engine reports.
    private sealed record CommandError(
        Exception Error,
        string Title,
        Func<string>? Subject,
        Func<string>? Reason,
        string? Folder,
        string? Switch
    );
}

// One message in the window's toast stack, or the message bar's content.
// Its owner describes it again on every change, so its text follows the
// state and the language.
public sealed class Notice : INotifyPropertyChanged
{
    private InfoBarSeverity _severity;
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

    internal Notice(NoticeKind kind, string torrentId = "", TimeSpan? lifetime = null)
    {
        Kind = kind;
        TorrentId = torrentId;
        Remaining = lifetime;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal NoticeKind Kind { get; }
    internal string Group { get; init; } = string.Empty;
    internal string Key { get; init; } = string.Empty;
    internal int Count { get; init; }
    internal WorkResult? Result { get; init; }

    // The torrent of a Completion notice; empty for every other kind.
    internal string TorrentId { get; }

    // The time on screen before the notice closes by itself; null for a
    // notice that stays until it is closed or its condition ends.
    internal TimeSpan? Remaining { get; set; }

    public InfoBarSeverity Severity
    {
        get => _severity;
        internal set
        {
            if (!Set(ref _severity, value))
                return;
            Changed(nameof(IsSuccess));
            Changed(nameof(IsInformational));
            Changed(nameof(IsWarning));
            Changed(nameof(IsError));
        }
    }
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

    // The engine's word for the Settings switch that turns off notices like
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
}
