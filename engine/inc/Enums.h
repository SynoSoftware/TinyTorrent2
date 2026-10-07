#pragma once

namespace tt
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
    Queue,
    PieceOrder,
    SpeedLimit,
    CheckProxy
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
    Naming,
    Saving
};

enum class RelocationPhase
{
    Preparing,
    Waiting,
    Checking,
    Moving,
    Saving,
    Unknown
};

enum class RenamePhase
{
    Waiting,
    Flushing,
    Moving,
    Naming,
    Recovering
};

enum class NamePhase
{
    Pending,
    Preparing,
    Ready
};

// A deletion saves the list without its torrents, waits for libtorrent to
// release their files, and then deletes them.
enum class DeletionPhase
{
    Saving,
    Waiting,
    Deleting,
    Unknown
};

// Whether a command accepts torrents whose own files are busy.
enum class BusyFiles
{
    Refused,
    Accepted
};

enum class AdditionKind
{
    New,
    Duplicate,
    // The content is already in the list and the source has trackers that
    // torrent lacks. Nothing changes, so the person chooses whether to merge.
    Mergeable
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

// Startup reads the settings before the transfers, so the splash and the
// window know the saved choices while the transfers still load.
enum class Startup
{
    Settings,
    Transfers,
    Ready
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

enum class LimitMode
{
    None,
    Speed,
    Alternative
};

enum class Encryption
{
    Preferred,
    Required,
    Allowed,
    Disabled
};

enum class ProxyType
{
    None,
    Socks5,
    Socks4,
    Http
};

// What the engine learned about a proxy, from a check or from the
// connections that go through it.
enum class ProxyOutcome
{
    Connected,
    SignInFailed,
    Unreachable,
    NotFound,
    WrongType,
    TimedOut
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

// The error code of a failed reply. The window reads these words, so
// renaming one changes the protocol.
enum class ErrorCode
{
    InvalidRequest,
    UnknownCommand,
    InvalidSource,
    InvalidSources,
    InvalidDestination,
    InvalidPriorities,
    InvalidTrackers,
    InvalidTargets,
    ResponseTooLarge,
    UiConnected,
    Starting,
    Stopping,
    Unavailable,
    Overloaded,
    StorageFailed,
    RecoveryRequired,
    FilesBusy,
    SharedFiles,
    DestinationConflict,
    DestinationInUse,
    MetadataUnavailable,
    PreviewExpired,
    PreviewFailed,
    TorrentRemoved,
    AddFailed,
    RegistrationFailed
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

// How a second launch's request to the running engine ended. A different
// version is its own outcome, because the person must exit that engine first.
enum class Forwarding
{
    Accepted,
    Refused,
    OtherVersion
};

namespace desktop
{
// Cold when Open started with this engine, which must load first; warm when
// the engine was already running.
enum class Launch
{
    Cold,
    Warm
};

enum class TrayClick
{
    Idle,
    Waiting,
    Double
};

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
