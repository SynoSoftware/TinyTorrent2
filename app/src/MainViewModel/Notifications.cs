using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent;

public sealed partial class MainViewModel
{
    // Fluent 2 toasts: at most four show, and one closes after seven seconds.
    // A finished download stays visible in the list after its notice goes.
    private const int MaxTimedNotices = 4;
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(7);

    // The torrents the person resumed, which start once every transfer may
    // run, or none after Resume all; null when no such notice shows.
    private Torrent[]? _waiting;

    // A lasting problem and the action that resolves it, which Fluent 2
    // requires on an error or warning message bar. OffersDataFolder adds
    // Open folder before the action, for a file in the data folder that the
    // person fixes outside the app.
    internal sealed record Status(
        string Title,
        string Body,
        InfoBarSeverity Severity,
        ICommand? Action,
        string ActionText,
        string ActionGlyph,
        string ActionTip,
        bool OffersDataFolder = false
    );

    // The status the person closed. Fluent 2 brings a closed message bar
    // back while its problem lasts, so it returns when the problem changes
    // and at the next start.
    private Status? _dismissedStatus;
    private bool _showsStatus;

    private Subtitles.FileFailure? _subtitleFailure;
    private int _subtitleFailureCount;

    // The events the window reports as toasts, oldest first, so the newest
    // notice sits nearest the window corner.
    public ObservableCollection<Notice> Notices { get; } = [];

    // The message bar's content; ShowsStatus says whether it shows.
    public Notice AppStatus { get; } = new(NoticeKind.Status);
    public bool ShowsStatus => _showsStatus;
    public bool StatusOffersDataFolder { get; private set; }
    public bool HasResumeNotice => _waiting is not null && IsSessionPaused;
    public string ResumeNotice => _waiting is null ? string.Empty : Waiting(_waiting);
    public ICommand ResolvePause { get; }
    public string PauseAction =>
        _pause == PauseReason.Adapter
            ? Text.Get("notifications", "settings")
            : Text.Get("notifications", "resume");
    public string PauseActionTip =>
        Text.Get(
            "notifications",
            _pause switch
            {
                PauseReason.Adapter => "settings_tip",
                PauseReason.Schedule => "resume_schedule_tip",
                _ => "resume_all_tip",
            }
        );
    public string PauseActionGlyph =>
        _pause == PauseReason.Adapter ? Syno.Lucide.Wrench : Syno.Lucide.Play;

    public void DismissResume()
    {
        _waiting = null;
        Changed(nameof(HasResumeNotice));
    }

    // Says when the resumed torrents start, so the person knows the resume took
    // effect although the rows stay paused.
    private string Waiting(Torrent[] torrents)
    {
        if (torrents.Length == 0)
            return Text.Format("notifications", "starts_all", MissingAdapter);
        var (key, detail) = _pause switch
        {
            PauseReason.Adapter => ("starts_adapter", MissingAdapter),
            PauseReason.Schedule when Settings.Schedule.NextChange(DateTime.Now) is { } change => (
                "starts_until",
                change
            ),
            PauseReason.Schedule => ("starts_schedule", string.Empty),
            _ => ("starts_resume", string.Empty),
        };
        return Text.FormatCount("notifications", key, torrents.Length, torrents[0].Name, detail);
    }

    internal void ReceiveNotice(JsonElement notice)
    {
        if (_closed)
            return;
        var kind = notice.GetProperty("kind").GetString();
        var name = notice.GetProperty("name").GetString() ?? string.Empty;
        var torrentId = notice.GetProperty("torrent_id").GetString() ?? string.Empty;
        var count = notice.TryGetProperty("count", out var total) ? total.GetInt32() : 1;
        if (kind == "completed")
            ShowCompletion(torrentId, name);
        else if (kind is "error" or "add_failed" or "delete_failed")
        {
            var detail = notice.GetProperty("detail").GetString() ?? string.Empty;
            var code =
                notice.GetProperty("code").GetString() is { Length: > 0 } problem ? problem
                : kind == "error"
                    ? torrentId.Length == 0 ? "unknown"
                        : "torrent_error"
                : kind;
            var error = new CommandException(code, detail, Text);
            // The title already says what failed, so the engine's reason
            // alone is the body; without one, the error code's text is.
            string Reason()
            {
                var reason =
                    kind == "delete_failed" && detail == "recycle_failed"
                        ? Text.Get("errors", "recycle_failed")
                    : detail.Length > 0 ? detail
                    : Text.Error(error);
                return count > 1
                    ? Text.Format(
                        "errors",
                        "detail",
                        reason,
                        Text.Format("errors", "additional_problems", count - 1)
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
            Report(error, title, () => name, Reason, folder is { Length: > 0 } ? folder : null);
        }
    }

    // A notice for a condition shows while the condition holds. Each change
    // of the model can change a condition, so every change syncs the stack
    // instead of each place that changes the state.
    private void SyncNotices()
    {
        if (_closed)
            return;
        SyncStatus();
        SyncCondition(NoticeKind.Resume, HasResumeNotice);
        SyncCondition(NoticeKind.CommandError, HasCommandError);
        var close = Text.Get("commands", "close");
        foreach (var notice in Notices)
        {
            notice.CloseText = close;
            Describe(notice);
            if (notice.Kind == NoticeKind.Completion)
                ((RelayCommand)notice.Action!).Refresh();
        }
    }

    // Raising ShowsStatus runs this sync again, which then finds nothing new.
    private void SyncStatus()
    {
        var status = CurrentStatus;
        if (status is not null)
        {
            AppStatus.Title = status.Title;
            AppStatus.Message = status.Body;
            AppStatus.Severity = status.Severity;
            AppStatus.Action = status.Action;
            AppStatus.ActionText = status.ActionText;
            AppStatus.ActionGlyph = status.ActionGlyph;
            AppStatus.ActionTip = status.ActionTip;
            if (StatusOffersDataFolder != status.OffersDataFolder)
            {
                StatusOffersDataFolder = status.OffersDataFolder;
                Changed(nameof(StatusOffersDataFolder));
            }
        }
        AppStatus.CloseText = Text.Get("commands", "close");
        var shows = status is not null && status != _dismissedStatus;
        if (shows == _showsStatus)
            return;
        _showsStatus = shows;
        Changed(nameof(ShowsStatus));
    }

    public void DismissStatus()
    {
        _dismissedStatus = CurrentStatus;
        SyncStatus();
    }

    private void SyncCondition(NoticeKind kind, bool shows)
    {
        var shown = Notices.FirstOrDefault(notice => notice.Kind == kind);
        if (shows && shown is null)
            Notices.Add(new Notice(kind));
        else if (!shows && shown is not null)
            Notices.Remove(shown);
    }

    private void Describe(Notice notice)
    {
        switch (notice.Kind)
        {
            case NoticeKind.WorkResult:
            {
                var result = notice.Result!;
                notice.Title = Text.Get(
                    "work",
                    "result_" + result.Outcome.ToString().ToLowerInvariant()
                );
                notice.Subject = Text.Get(result.Observation.Description);
                var progress = result.Observation.Progress;
                var summary = result.SummaryKey is { } key
                    ? Text.Format("work", key, result.Arguments)
                    : progress is WorkProgress.Fraction { Unit: WorkUnit.Files or WorkUnit.Folders } fraction
                        ? Text.Format(
                            "work",
                            fraction.Unit == WorkUnit.Folders ? "folders" : "processed",
                            fraction.Completed,
                            fraction.Total
                        )
                        : string.Empty;
                var detail = result.Error is { } error ? Text.Error(error) : string.Empty;
                notice.Message =
                    detail.Length == 0 ? summary
                    : summary.Length == 0 ? detail
                    : Text.Format("errors", "detail", summary, detail);
                break;
            }
            case NoticeKind.Outcome:
                notice.Title = Text.Get(notice.Group, notice.Key);
                break;
            case NoticeKind.SearchLimit:
                notice.Title = Text.Get("notifications", "search_limit");
                notice.Message = Text.FormatCount(
                    "notifications",
                    "search_limit_files",
                    notice.Count
                );
                notice.ActionText = Text.Get("notifications", "settings");
                notice.ActionTip = Text.Get("notifications", "search_limit_tip");
                break;
            case NoticeKind.Resume:
                notice.Severity = InfoBarSeverity.Informational;
                notice.Title = Text.Get("notifications", "delayed");
                notice.Message = ResumeNotice;
                notice.Action = ResolvePause;
                notice.ActionText = PauseAction;
                notice.ActionTip = PauseActionTip;
                notice.ActionGlyph = PauseActionGlyph;
                break;
            case NoticeKind.CommandError:
                notice.Severity = InfoBarSeverity.Error;
                notice.Title = Text.Get("notifications", _errorTitle);
                notice.Subject = _errorSubject?.Invoke() ?? string.Empty;
                notice.Message = CommandError;
                notice.Action = _errorFolder is null ? null : OpenErrorFolder;
                notice.ActionText = Text.Get("commands", "open_folder");
                notice.ActionTip = Text.Get("notifications", "files_folder_tip");
                notice.ActionGlyph = Syno.Lucide.FolderOpen;
                break;
            case NoticeKind.SubtitleError:
                var latest = _subtitleFailure!;
                var reason = Text.Get(
                    "subtitles",
                    "failure_" + latest.Reason.ToString().ToLowerInvariant()
                );
                notice.Severity = InfoBarSeverity.Error;
                notice.Title = Text.Get(
                    "notifications",
                    latest.Reason == SubtitleFailure.Delete
                        ? "subtitle_not_deleted"
                        : "subtitle_not_saved"
                );
                notice.Subject = Path.GetFileName(latest.Path);
                notice.Message =
                    _subtitleFailureCount == 1
                        ? reason
                        : Text.Format(
                            "errors",
                            "detail",
                            reason,
                            Text.Format("errors", "additional_problems", _subtitleFailureCount - 1)
                        );
                notice.Action = ShowSubtitleSettings;
                notice.ActionText = Text.Get("notifications", "settings");
                notice.ActionTip = Text.Get("notifications", "subtitles_tip");
                notice.ActionGlyph = Syno.Lucide.Settings;
                break;
            case NoticeKind.Completion:
                notice.Severity = InfoBarSeverity.Success;
                notice.Title = Text.Get("notifications", "finished");
                notice.ActionText = Text.Get("commands", "open_folder");
                notice.ActionTip = Text.Format("notifications", "open_folder_tip", notice.Subject);
                notice.ActionGlyph = Syno.Lucide.FolderOpen;
                break;
        }
    }

    // A torrent that finishes again replaces its earlier notice, and beyond
    // the limit the oldest timed notice gives way to the new one.
    private void ShowCompletion(string torrentId, string name)
    {
        if (
            Notices.FirstOrDefault(shown =>
                shown.Kind == NoticeKind.Completion && shown.TorrentId == torrentId
            ) is
            { } earlier
        )
            Notices.Remove(earlier);
        var notice = new Notice(NoticeKind.Completion, torrentId, NoticeLifetime)
        {
            Subject = name,
            CloseText = Text.Get("commands", "close"),
        };
        notice.Action = new RelayCommand(
            () => OpenCompletion(notice),
            () => CanEdit && _byId.ContainsKey(notice.TorrentId)
        );
        Describe(notice);
        Notices.Add(notice);
        if (
            Notices.Where(shown => shown.Remaining is not null).ToArray() is
            { Length: > MaxTimedNotices } timed
        )
            Notices.Remove(timed[0]);
        Announce(Text.Format("notifications", "completed", name));
    }

    // A result the person asked for, such as a passed check. It closes by
    // itself like a finished download, because nothing remains to do.
    internal void ShowOutcome(string group, string key, string subject) =>
        ShowTimed(
            new Notice(NoticeKind.Outcome, lifetime: NoticeLifetime)
            {
                Severity = InfoBarSeverity.Success,
                Group = group,
                Key = key,
                Subject = subject,
            }
        );

    private void ShowWorkResult(WorkResult result)
    {
        if (_closed || _closing || _featuresClosed)
            return;
        ShowTimed(
            new Notice(NoticeKind.WorkResult, lifetime: NoticeLifetime)
            {
                Result = result,
                Severity = result.Outcome switch
                {
                    WorkOutcome.Completed => InfoBarSeverity.Success,
                    WorkOutcome.Cancelled => InfoBarSeverity.Informational,
                    _ => InfoBarSeverity.Error,
                },
            }
        );
    }

    // Fetch metadata leaves a file unidentified when its title reached the
    // search limit, which only raising the limit lets the batch search again.
    internal void ShowSearchLimit(int files)
    {
        var notice = new Notice(NoticeKind.SearchLimit, lifetime: NoticeLifetime)
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
                return RequestSettings(new(SettingsCategory.Plugins, "lookup_limit"));
            },
            () => true
        );
        ShowTimed(notice);
    }

    private void ShowTimed(Notice notice)
    {
        if (_closed)
            return;
        Describe(notice);
        notice.CloseText = Text.Get("commands", "close");
        Notices.Add(notice);
        if (
            Notices.Where(shown => shown.Remaining is not null).ToArray() is
            { Length: > MaxTimedNotices } timed
        )
            Notices.Remove(timed[0]);
        Announce(
            notice.Kind == NoticeKind.WorkResult
                ? string.Join(
                    Environment.NewLine,
                    new[] { notice.Title, notice.Subject, notice.Message }.Where(value => value.Length > 0)
                )
                : notice.Title
        );
    }

    // Subtitle work fails in the background, often for several movies at
    // once, so one notice names the latest file and counts the others.
    internal void ShowSubtitleFailure(Subtitles.FileFailure failure)
    {
        if (_closed)
            return;
        _subtitleFailure = failure;
        _subtitleFailureCount++;
        var notice = Notices.FirstOrDefault(shown => shown.Kind == NoticeKind.SubtitleError);
        if (notice is null)
        {
            notice = new Notice(NoticeKind.SubtitleError)
            {
                CloseText = Text.Get("commands", "close"),
            };
            Notices.Add(notice);
        }
        Describe(notice);
        Announce(notice.Message);
    }

    // Closing a condition's notice ends what it reports, so the condition
    // does not bring the notice back.
    public void DismissNotice(Notice notice)
    {
        switch (notice.Kind)
        {
            case NoticeKind.Resume:
                DismissResume();
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

    // Opening the folder is what the notice offers, so the notice closes with it.
    private async Task OpenCompletion(Notice notice)
    {
        if (!CanEdit || !_byId.TryGetValue(notice.TorrentId, out var torrent))
            return;
        Notices.Remove(notice);
        await OpenTorrent(torrent, true);
    }
}

// One message in the window's toast stack, or the message bar's content.
// MainViewModel describes it again on every change, so its text follows the
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
    // notice that stays until it is closed or its condition ends. The
    // window counts only time in which the person can see it.
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
