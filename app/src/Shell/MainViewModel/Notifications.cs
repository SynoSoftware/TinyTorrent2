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
    )
    {
        internal bool HasAction => Action is not null;
    }

    // The status the person closed. Fluent 2 brings a closed message bar
    // back while its problem lasts, so it returns when the problem changes
    // and at the next start.
    private Status? _dismissedStatus;
    private bool _showsStatus;

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
                DeleteDatabase,
                Text.Get("file_action", "delete"),
                Syno.Lucide.Trash2,
                Text.Get("window", "delete_database_tip")
            );
        if (failure.Database == DatabaseCondition.ReadOnlyFile)
            return new(
                title,
                Text.Format("window", "database_read_only", Database.FileName),
                InfoBarSeverity.Error,
                RepairDatabase,
                Text.Get("window", "repair_database"),
                Syno.Lucide.Wrench,
                Text.Get("window", "repair_database_tip")
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
            : Retry(Text.Format("window", cause, Database.FileName), offersDataFolder: true);

        Status Retry(string body, bool offersDataFolder = false) =>
            new(
                title,
                body,
                InfoBarSeverity.Error,
                RetryFeatures,
                Text.Get("inspector", "retry"),
                Syno.Lucide.RotateCcw,
                Text.Get("window", "retry_features_tip"),
                offersDataFolder
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
