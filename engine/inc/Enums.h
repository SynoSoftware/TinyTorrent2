#pragma once

namespace tiny
{
enum class Command
{
    Snapshot,
    Settings,
    SessionPause,
    Preview,
    PreviewDetail,
    CancelPreview,
    Add,
    MergeTrackers,
    Torrent,
    Edit,
    Reannounce,
    History,
    Pause,
    Resume,
    Force,
    Verify,
    Remove,
    FileScope,
    Move,
    DeleteFiles,
    Queue
};

enum class TorrentView
{
    General,
    Files,
    Peers,
    Trackers,
    Pieces
};

enum class QueueMove
{
    Up,
    Down,
    Top,
    Bottom,
    Before
};

enum class RegistrationOperation
{
    Observe,
    RegisterHandlers,
    UnregisterHandlers,
    EnableStartup,
    DisableStartup,
    OpenDefaults,
    OpenStartup
};

enum class AdditionPhase
{
    Adding,
    Moving,
    Saving
};

enum class RelocationPhase
{
    Preparing,
    Waiting,
    Moving,
    Saving,
    Unknown
};

enum class DeletionPhase
{
    Waiting,
    Deleting,
    Unknown
};

// Where a torrent's checkpoint is: waiting for libtorrent's resume data, or
// writing that data to the resume file.
enum class CheckpointPhase
{
    Idle,
    Requested,
    Writing
};

enum class ExitStep
{
    Draining,
    Pausing,
    Saving
};

// What the person last asked a torrent to do. A resumed torrent waits in
// the download queue; a forced one ignores it.
enum class Intent
{
    Paused,
    Resumed,
    Forced
};

enum class Status
{
    Moving,
    Error,
    Paused,
    Checking,
    Metadata,
    Queued,
    Seeding,
    Completed,
    Downloading
};

enum class ScheduleMode
{
    Normal,
    Alternative,
    Paused
};

enum class ProblemKind
{
    MoveInterrupted,
    MoveFailed,
    DestinationExists,
    MoveUncertain,
    AliasConflict,
    TorrentError,
    StorageFailed,
    StorageOverloaded,
    CheckpointFailed
};

enum class NoticeKind
{
    Completed,
    Error,
    Added,
    Duplicate,
    AddFailed,
    DeleteFailed,
    Background,
    // Several notices that arrived together, shown as one balloon.
    Aggregate
};

enum class SplashFailure
{
    Launch,
    Startup,
    Save,
    FilesBusy,
    Unresponsive
};

enum class SplashChoice
{
    Retry,
    Close,
    Cancel,
    ExitAnyway,
    Wait
};

// Tray menu item IDs. TrackPopupMenu returns 0 when the person chooses
// nothing, so no item uses it.
enum class TrayItem
{
    Open = 1,
    Pause,
    Exit,
    Rates,
    Counts
};

namespace desktop
{
enum class CloseState
{
    Waiting,
    Cancelled,
    Closing
};

// The commands the desktop host answers itself; it passes all others to the
// engine.
enum class Command
{
    Registration,
    Ready,
    UiClosed,
    ActivateReply,
    CloseReply,
    ActivateSources,
    PendingSources,
    SourcesReceived,
    Open,
    Exit
};
}
}
