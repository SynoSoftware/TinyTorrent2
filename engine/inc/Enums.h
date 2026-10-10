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
    CheckProxy,
    ConnectionTest
};

enum class ConnectionPhase
{
    Idle,
    Stopping,
    Downloading,
    Uploading,
    Holding,
    Restoring,
    Completed,
    Cancelled,
    Failed
};

enum class TorrentView
{
    General,
    Files,
    Peers,
    Trackers,
    Pieces
};

// A part of a torrent's detail that one libtorrent query answers.
enum class DetailKind
{
    Trackers,
    Peers,
    Progress,
    Priorities,
    Status,
    Availability,
    Downloading
};

enum class QueueMove
{
    Up,
    Down,
    Top,
    Bottom,
    Before
};

enum class RegistrationAction
{
    Observe,
    RegisterHandlers,
    UnregisterHandlers,
    EnableStartup,
    DisableStartup,
    OpenDefaults,
    OpenStartup
};

// The executable a registration starts, compared with this copy.
enum class Copy
{
    This,
    Other,
    Missing
};

enum class AdditionPhase
{
    Adding,
    Moving,
    Naming,
    Saving
};

enum class MovePhase
{
    Staging,
    Preflight,
    Ready,
    Moving,
    Observing,
    Saving
};

enum class RenamePhase
{
    Ready,
    Moving,
    Naming,
    Recovering,
    Observing
};

enum class NamePhase
{
    Pending,
    Preparing,
    Recovering,
    Ready
};

// A finished download waits for libtorrent to write its data to disk, then
// for its files to settle in their final names and folder, before the
// Completed notice.
enum class CompletionPhase
{
    Idle,
    Downloading,
    Flushing,
    Settling,
    Checking,
    Checked
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
    Duplicate
};

// Where a torrent's checkpoint is: waiting for libtorrent's resume data, or
// writing that data to the resume file.
enum class CheckpointPhase
{
    Idle,
    Requested,
    Writing
};

enum class ShutdownPhase
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
    // The torrent would transfer, but a session pause holds every torrent.
    AllPaused,
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

enum class Transport
{
    Both,
    Tcp,
    Utp
};

enum class IpFamily
{
    Both,
    Ipv4,
    Ipv6
};

enum class Layout
{
    Keep,
    Create,
    Strip
};

enum class DuplicatePolicy
{
    Keep,
    Merge
};

enum class DeletionMode
{
    Recycle,
    Permanent
};

enum class SeedRule
{
    Any,
    All
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
    InvalidTorrents,
    ResponseTooLarge,
    WindowConnected,
    Starting,
    ShuttingDown,
    Unavailable,
    Overloaded,
    StorageFailed,
    RecoveryRequired,
    FilesBusy,
    SharedFiles,
    DestinationConflict,
    DestinationInUse,
    MoveInterrupted,
    MetadataUnavailable,
    PreviewExpired,
    PreviewFailed,
    TorrentRemoved,
    AliasConflict,
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
    // The application could not do what the person asked, such as saving on
    // Exit. Unlike torrent problems, it shows even when those are turned off.
    Failure,
    Background,
    // A torrent or magnet handler starts a program that is missing.
    MissingProgram,
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

// The window's answer when the desktop host asks it to confirm Exit.
enum class ExitAnswer
{
    Confirmed,
    Cancelled,
    // The window could not show the prompt, so the host asks natively.
    Unavailable
};

// How far the desktop host is through Exit: the person confirming it in the
// host's prompt or in the window, the window closing and the final save, or
// Windows ending the session.
enum class ExitPhase
{
    Idle,
    Confirming,
    WindowConfirming,
    Exiting,
    SessionEnding
};

// The commands the desktop host answers itself; it passes all others to the
// engine.
enum class Command
{
    Registration,
    Ready,
    WindowClosed,
    ActivateReply,
    CloseReply,
    ExitReply,
    ActivateSources,
    PendingActivations,
    ActivationsReceived,
    Open,
    Exit
};
}
}
