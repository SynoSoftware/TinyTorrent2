using System.Windows.Input;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml.Controls;
using SQLitePCL;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Storage;
using Syno.TinyTorrent.Torrents;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainViewModel
{
    // The torrents the person resumed, which start once every transfer may
    // run, or none after Resume all; null when no such toast shows.
    private Torrent[]? _waiting;

    // A lasting problem and the action that resolves it, which Fluent 2
    // requires on an error or warning message bar. Fluent 2 allows a second
    // action, shown before it, such as Open folder for a file in the data
    // folder that the person fixes outside the app.
    internal sealed record Status(
        string Title,
        string Body,
        InfoBarSeverity Severity,
        StatusAction? Action,
        StatusAction? Secondary = null
    )
    {
        internal bool HasAction => Action is not null;
        internal bool HasSecondary => Secondary is not null;
    }

    internal sealed record StatusAction(ICommand Command, string Text, string Glyph, string Tip);

    // A lasting storage problem as the engine reports it, until the engine
    // resolves it. The "stopped" condition counts the torrents that need
    // Retry: Name and Detail describe the only one, and Code is the problem
    // they share, empty when their problems differ. A location condition
    // names its drive root or UNC share in Folder, the cause's error code in
    // Detail, and the torrents that wait for it in Count.
    internal sealed record Condition(
        string Code,
        string Detail,
        string Name,
        string Folder,
        int Count
    );

    // The status the person closed. Fluent 2 brings a closed message bar
    // back while its problem lasts, so it returns when the problem changes
    // and at the next start.
    private Status? _dismissedStatus;
    private bool _showsStatus;

    private Condition? _stopped;

    // In the order the engine raised them, keyed by its condition key.
    private readonly List<(string Key, Condition Condition)> _locations = [];

    // The events the window reports as toasts.
    public Notifications Notifications { get; }

    // The message bar's content, the latest status; ShowsStatus says whether
    // it shows.
    internal Status? AppStatus { get; private set; }
    public bool ShowsStatus => _showsStatus;
    public bool HasResumeToast => _waiting is not null && IsSessionPaused;
    public string ResumeMessage => _waiting is null ? string.Empty : Waiting(_waiting);
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
        Changed(nameof(HasResumeToast));
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

    private void SyncMessages()
    {
        if (_closed)
            return;
        SyncStatus();
        Notifications.Sync();
    }

    // Raising AppStatus or ShowsStatus runs this sync again, which then finds
    // nothing new.
    private void SyncStatus()
    {
        var status = CurrentStatus;
        if (status is not null && status != AppStatus)
        {
            AppStatus = status;
            Changed(nameof(AppStatus));
        }
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

    // Records a condition the engine raised or updated, or forgets it when
    // condition is null because the engine resolved it.
    internal void Observe(string key, Condition? condition)
    {
        if (key == "stopped")
            _stopped = condition;
        else
        {
            var index = _locations.FindIndex(location => location.Key == key);
            if (condition is null)
            {
                if (index >= 0)
                    _locations.RemoveAt(index);
            }
            else if (index >= 0)
                _locations[index] = (key, condition);
            else
                _locations.Add((key, condition));
        }
        Changed(nameof(CurrentStatus));
    }

    // One bar covers every torrent that needs Retry, and its Retry runs them
    // all, so torrents that stopped together need one click.
    private Status StoppedStatus(Condition stopped)
    {
        var title =
            stopped.Count == 1
                ? Text.Get("notifications", Notifications.ProblemTitle(stopped.Code))
                : Text.Format("notifications", "attention", stopped.Count);
        var body =
            stopped.Count == 1
                ? Text.Format("errors", "line", stopped.Name, Text.Error(stopped.Code, stopped.Detail))
            // A torrent error's text is each torrent's own libtorrent message,
            // so several of them share no reason to show.
            : stopped.Code is { Length: > 0 } and not "torrent_error"
                ? Text.Error(stopped.Code, null)
            : Text.Get("notifications", "different_problems");
        return new(
            title,
            body,
            InfoBarSeverity.Error,
            new(
                RetryAll,
                Text.Get("inspector", "retry"),
                Syno.Lucide.RotateCcw,
                Text.Get("notifications", stopped.Count == 1 ? "retry_torrent_tip" : "retry_torrents_tip")
            ),
            ShowErrorsAction
        );
    }

    // The bar names the first location that stopped answering and counts
    // the others, which Show torrents lists with it.
    private Status LocationStatus()
    {
        var location = _locations[0].Condition;
        var affected = Text.FormatCount("notifications", "location_torrents", location.Count);
        var reason =
            location.Detail.Length == 0
                ? affected
                : Text.Format("errors", "detail", Text.Get("errors", location.Detail), affected);
        var body = Text.Format("errors", "line", location.Folder, reason);
        if (_locations.Count > 1)
            body = Text.Format(
                "errors",
                "detail",
                body,
                Text.Format("errors", "additional_problems", _locations.Count - 1)
            );
        return new(
            Text.Get("notifications", "location_unavailable"),
            body,
            InfoBarSeverity.Warning,
            ShowErrorsAction
        );
    }

    // Show alone would not say what the bar shows, so the label is two words.
    private StatusAction ShowErrorsAction =>
        new(
            ShowErrors,
            Text.Get("notifications", "show_torrents"),
            Syno.Lucide.List,
            Text.Get("notifications", "show_torrents_tip")
        );

    private void RefreshFeedback()
    {
        if (_closed || Features.IsClosed)
            return;
        Changed(nameof(CurrentStatus));
        ((RelayCommand)RetryFeatures).Refresh();
        ((RelayCommand)DeleteDatabase).Refresh();
        ((RelayCommand)RepairDatabase).Refresh();
        ((RelayCommand)DownloadSubtitles).Refresh();
    }

    // The message bar names the cause. It offers the fix the app can make,
    // or Open folder beside Retry when the person fixes the file outside the
    // app.
    private Status FeatureStatus(FeatureFailure failure)
    {
        var title = Text.Get(
            "notifications",
            failure.SubtitlesOnly ? "subtitles_unavailable" : "library_unavailable"
        );
        var error = failure.Cause;
        // An incompatible database fails both features, whichever reports
        // first, but a failed deletion of it says why it failed.
        if (failure.Database == DatabaseCondition.Incompatible)
            return new(
                title,
                error is IOException or UnauthorizedAccessException
                    ? Text.Error(error)
                    : Text.Get("errors", "database_incompatible"),
                InfoBarSeverity.Error,
                new(
                    DeleteDatabase,
                    Text.Get("file_action", "delete"),
                    Syno.Lucide.Trash2,
                    Text.Get("window", "delete_database_tip")
                )
            );
        if (failure.Database == DatabaseCondition.ReadOnlyFile)
            return new(
                title,
                Text.Format("window", "database_read_only", Database.FileName),
                InfoBarSeverity.Error,
                new(
                    RepairDatabase,
                    Text.Get("window", "repair_database"),
                    Syno.Lucide.Wrench,
                    Text.Get("window", "repair_database_tip")
                )
            );
        if (error is not SqliteException { SqliteErrorCode: var code })
            return Retry(Text.Error(error));
        var cause = code switch
        {
            raw.SQLITE_READONLY or raw.SQLITE_PERM or raw.SQLITE_CANTOPEN => "database_denied",
            raw.SQLITE_IOERR => "database_disk",
            raw.SQLITE_FULL => "database_full",
            raw.SQLITE_BUSY or raw.SQLITE_LOCKED => "database_in_use",
            _ => null,
        };
        // Any other code is a defect, and SQLite's own message is what a
        // report of it needs.
        return cause is null
            ? Retry(Text.Error(error))
            : Retry(
                Text.Format("window", cause, Database.FileName),
                new(
                    OpenDataFolder,
                    Text.Get("commands", "open_folder"),
                    Syno.Lucide.FolderOpen,
                    Text.Get("window", "database_folder_tip")
                )
            );

        Status Retry(string body, StatusAction? dataFolder = null) =>
            new(
                title,
                body,
                InfoBarSeverity.Error,
                new(
                    RetryFeatures,
                    Text.Get("inspector", "retry"),
                    Syno.Lucide.RotateCcw,
                    Text.Get("window", "retry_features_tip")
                ),
                dataFolder
            );
    }

    // A toast's More menu offers what the engine's popup menu offers, and the
    // engine turns the switches off for both. Each choice closes the toast,
    // because the person has decided about it.
    internal Task TurnOffToasts(Toast toast) =>
        toast.Switch is { } @switch ? TurnOff(toast, @switch) : Task.CompletedTask;

    internal Task TurnOffAllToasts(Toast toast) => TurnOff(toast, "all");

    private async Task TurnOff(Toast toast, string @switch)
    {
        if (!CanEdit)
            return;
        Notifications.Dismiss(toast);
        try
        {
            await _client.Send("turn_off", new { @switch }, Requester.Person);
        }
        catch (Exception error)
        {
            Notifications.Report(error);
        }
    }

    internal Task ShowNotificationSettings(Toast toast)
    {
        Notifications.Dismiss(toast);
        return RequestSettings(new(SettingsCategory.General, "notifications"));
    }
}
