#pragma once

namespace tt
{
enum class Command
{
    Snapshot,
    Settings,
    ResetSettings,
    SessionPause,
    Preview,
    PreviewDetail,
    CancelPreview,
    Add,
    MergeTrackers,
    Torrent,
    Files,
    Edit,
    Reannounce,
    History,
    Pause,
    Resume,
    Force,
    Verify,
    Retry,
    Remove,
    FileScope,
    Move,
    DeleteFiles,
    Queue,
    PieceOrder,
    SpeedLimit,
    CheckProxy,
    ConnectionTest,
    TurnOff,
    UnfinishedTasks
};

// Who publishes the snapshot after a command succeeds.
enum class Publication
{
    // The command changes nothing.
    None,
    // Execute publishes it.
    Execute,
    // The operation publishes it itself, because it also serves tray actions.
    Operation
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

// Why a connection test failed, which the window explains to the person.
enum class ConnectionFailure
{
    Network,
    Timeout,
    // Neither direction measured a rate after the warm-up.
    Sample,
    // The session did not return to its pause state when the test ended.
    Restore
};

enum class TorrentView
{
    General,
    Files,
    Peers,
    Trackers,
    Pieces
};

enum class PeerState
{
    Connecting,
    Handshake,
    Connected
};

enum class PeerEncryption
{
    Unknown,
    Plain,
    Handshake,
    Rc4,
    Tls
};

enum class TrackerStatus
{
    Disabled,
    Announcing,
    Working,
    Error,
    Waiting
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

// Which query a libtorrent status answers: the routine update, a seed limit
// query, or a detail query.
enum class StatusQuery
{
    Routine,
    Seed,
    Detail
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
    DisableStartup
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
    // Waits until the engine knows where its save folder really is, so its
    // disk work starts on that root.
    Locating,
    Adding,
    Moving,
    Naming,
    Saving
};

// Who starts a move: a request, or the engine starting a move that waited for
// its turn.
enum class MoveStart
{
    Requested,
    Waited
};

// How far a saved move got, which decides what continuing or abandoning it
// may do with the files at its destination.
enum class MoveStage
{
    // Its destination was not checked yet, so a file there can be someone
    // else's.
    Staged,
    // Its destination was checked before any file changed, so a file there
    // beside a source that holds data is the move's own copy.
    Placing,
    // The person abandoned the checked move, so its files go back to the save
    // folder. Each start continues that without asking, because either answer
    // would give them back.
    Returning
};

// How a give-back's try ended, for the owner of its record.
enum class GiveBackEnd
{
    // The files are back.
    Ended,
    // Exit stopped it, and its record continues it at the next start.
    Stopped,
    // It runs again: once its root answers, or as its one automatic retry.
    Again,
    // It failed after its retry and waits for the person.
    Failed
};

// What giving a move's files back did.
enum class GivenBack
{
    // Exit stopped it between files, so some files may still be at the
    // destination.
    Stopped,
    // Only copies were deleted, so the files at the source did not change.
    Deleted,
    // Files were renamed back, so the torrent checks its files.
    Renamed
};

// How the engine stops a move's placement: not at all; cancelling it and
// undoing what it placed, as for a copy that stalled; or cancelling it and
// leaving what it placed, so that a move Exit stops can continue later.
enum class MoveCancel
{
    None,
    Undo,
    Keep
};

// The work that changes a torrent's file paths. At most one runs per torrent,
// and the owner that started it ends it.
enum class PathWork
{
    // A move to another folder, from when it joins the moves, before its
    // marker is saved, until the torrent's own move ends.
    Move,
    // The rename of a finished file from its unfinished name.
    Rename,
    // The give-back of an abandoned move's files, from when the torrent joins
    // the abandonments, also while it waits for its drives.
    Abandon
};

// The work of a torrent that the last end left unfinished: a move to another
// folder, or a check of its files.
enum class TaskKind
{
    Move,
    Check
};

// What the engine does at start with the work that the last end left
// unfinished: ask the person, or continue or abandon it all.
enum class TaskAnswer
{
    Ask,
    Continue,
    Abandon
};

// A move's current member: its worker places the files (Copying), libtorrent
// takes the new folder (Moving), the engine confirms it (Observing) and saves
// the outcome (Saving). A deletion then deletes the sources.
enum class MovePhase
{
    Staging,
    Preflight,
    Ready,
    Copying,
    Moving,
    Observing,
    Saving
};

// The kinds of file work that FileWork records, each with its own rules.
enum class WorkKind
{
    // A move of torrents to another folder, in every phase. It takes drive
    // turns, its copy is timed, and it holds the renames on its roots.
    Move,
    // The give-back of what a cut move placed, for a listed or a removed
    // torrent. It is timed; a listed torrent's give-back takes drive turns.
    GiveBack,
    // One root's part of a deletion: libtorrent's removal of the torrents
    // saved there, then the deletion of their files there. It holds the
    // renames and name choices on its root.
    Deletion,
    // A removed torrent that waits for its own file work to end before
    // libtorrent takes it.
    Removal
};

// How a piece of file work stands, named by what ends each wait.
enum class JobPhase
{
    // It waits for its turn: its drives, other work on its files, other
    // torrents' names or the save that records it.
    Queued,
    // A Windows call or a libtorrent disk job runs in its folders.
    Running,
    // Its root did not answer; the root's probe ends the wait when it opens
    // the root.
    Unanswered,
    // It failed after its retry; the person's Retry ends the wait.
    Failed
};

// Where the files of a failed move are: back in their source folder
// (Restored), or split between both folders, because a rename back failed or
// Exit stopped the move to continue it later.
enum class MoveFailure
{
    Restored,
    Split
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

// How many automatic continues a failure has used: a storage failure or a
// failed move to the final folder. Each happens at most once per failure, so
// a cause that persists never loops.
enum class StorageRetry
{
    // The engine has not continued the torrent. A storage failure's first
    // continue comes at once, after the location answers; a failed move to
    // the final folder starts at the minute check.
    Automatic,
    // The engine continued the torrent at once. If the failure returns before
    // the torrent gets further, the torrent stops.
    Retried,
    // The minute check continued the torrent. If the failure returns before
    // the torrent gets further, only the person continues it.
    Resumed
};

// Where a storage failure stands.
enum class FailureStage
{
    // The torrent is held while the engine releases its files and probes
    // its location each second.
    Recovering,
    // The torrent is held, and once a minute the engine checks, without
    // starting it, whether the cause is gone.
    Stopped,
    // Only the person's answer continues the torrent.
    Person,
    // The engine continued the torrent, which runs until it gets further or
    // the failure returns.
    Continued
};

// A job of a torrent's storage control.
enum class JobKind
{
    // libtorrent's disk fence, which ends the torrent's disk work.
    Release,
    // Whether the location answers, and which files lost their data.
    Probe,
    // The minute check of a stopped torrent's own folder.
    Check,
    // The minute check of the final folder of a failed move.
    FinalCheck,
    // A new handle from the resume data, without the pieces of lost files.
    Reload
};

// The person's command that answers a torrent's failures.
enum class FailureAnswer
{
    // Retry, Start or Force: every failure ends, and so do an alias conflict
    // and an add that failed.
    Retry,
    // Every failure ends, and the check reads the files again.
    Verify,
    // Only the failed move to the final folder ends.
    Stop,
    // A move to another folder replaces the failed move.
    Move,
    // The removal ends every failure.
    Remove
};

// Who asks for a command: the person, whose command answers the torrent's
// failures, or the engine's own policy, which answers none.
enum class Requester
{
    Person,
    Engine
};

// A finished download waits for libtorrent to write its data to disk, then
// for its files to settle in their final names and folder, before the
// Completed toast.
enum class CompletionPhase
{
    Idle,
    Downloading,
    Flushing,
    Settling,
    Checking,
    Checked
};

// How far libtorrent has written a torrent's downloaded data before the
// engine renames its finished files. Releasing the files waits for that
// write, which takes minutes on a slow drive, so the rename waits for it
// first, without holding the files of any other torrent.
enum class CacheWrite
{
    // Data can still be in libtorrent's memory.
    Pending,
    Flushing,
    Written
};

// libtorrent queues a new addition only once its chosen files apply, because
// it is added with none wanted. Until a status after that, the addition
// counts as queued at its accepted place.
enum class QueueReport
{
    Reported,
    AwaitingFiles,
    AwaitingStatus
};

// A deletion saves the list without its torrents (Saving), and then its parts
// wait for libtorrent to release their files and delete them (Waiting). After
// lost alerts, a part waits for its removed handles to expire (Unknown).
enum class DeletionPhase
{
    Saving,
    Waiting,
    Unknown
};

enum class AdditionKind
{
    New,
    Duplicate
};

// The reads libtorrent may still have queued for a torrent's check. Reads
// that stalled stay Stalled until libtorrent reports them done, whatever the
// person does with the torrent, because every other check waits behind them.
enum class CheckReads
{
    None,
    Reading,
    Stalled
};

// Where a torrent's checkpoint is: waiting for libtorrent's resume data, or
// writing that data to the resume file.
enum class CheckpointPhase
{
    Idle,
    Requested,
    Writing
};

// Saving waits for the final checkpoints; Ending gives the work that still
// runs a moment to stop. Cutting waits only for the lines and the document
// that record the work it cuts off. Failing follows a save that failed and
// waits only for the save worker and the log line that says so.
enum class ShutdownPhase
{
    Idle,
    Draining,
    Pausing,
    Sampling,
    Saving,
    Ending,
    Cutting,
    Failing
};

// How a shutdown ended.
enum class ShutdownEnd
{
    // The final save succeeded and the work stopped, so libtorrent's teardown
    // follows.
    Saved,
    // The final save succeeded, but work that uses the session or a drive
    // still runs and may never return, so the process ends at once without
    // libtorrent's teardown. The next start resumes or restarts that work.
    Cut,
    // The final save failed, so the process ends at once, as after a crash.
    Abrupt
};

enum class WatchPhase
{
    Idle,
    Scanning,
    Adding
};

// Startup reads the settings before the transfers, so the splash and the
// window know the saved choices while the transfers still load.
enum class Startup
{
    Settings,
    Transfers,
    Ready,
    // Reading the saved settings or transfers failed, so there is no session.
    Failed
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
    // The check waits until no other job uses its drive.
    WaitingCheck,
    // The move waits for its drives or for busy files to start.
    WaitingMove,
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

// Why the session is paused, when it is.
enum class PauseReason
{
    // Pause all, or the setting that starts transfers paused at launch.
    Manual,
    Schedule,
    Adapter,
    ConnectionTest
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

enum class Theme
{
    System,
    Light,
    Dark
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
    // An interrupted move waits for the person to continue or abandon the
    // work that the last end left unfinished.
    MoveUnfinished,
    MoveFailed,
    DestinationExists,
    // The destination is another path, such as a link, to the files' folder.
    SameLocation,
    AliasConflict,
    TorrentError,
    DownloadPathMissing,
    DownloadPathInvalid,
    DownloadPathUnavailable,
    DiskFull,
    StorageFailed,
    StorageOverloaded,
    CheckpointFailed,
    // A check or a move made no progress for too long.
    DriveStalled
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
    EncryptionFailed,
    RecoveryRequired,
    FilesBusy,
    SharedFiles,
    DestinationConflict,
    DestinationInUse,
    MetadataUnavailable,
    PreviewExpired,
    PreviewFailed,
    TorrentRemoved,
    TorrentRemoving,
    AliasConflict,
    TorrentError,
    AddFailed,
    RegistrationFailed
};

enum class StorageFailure
{
    Failed,
    // The store refused the work because too many jobs were waiting.
    Overloaded
};

enum class ProblemChange
{
    Raised,
    Updated,
    Resolved
};

enum class ToastKind
{
    Completed,
    Error,
    Added,
    Duplicate,
    AddFailed,
    DeleteFailed,
    Background,
    // A torrent or magnet handler starts a program that is missing.
    MissingProgram,
    // Several toasts that arrived together, shown as one popup.
    Aggregate,
    // Work that the last end left unfinished waits for the person's answer,
    // which the window asks for.
    Unfinished
};

// The Settings switches that each turn off one kind of toast.
enum class ToastSwitch
{
    Finished,
    Added,
    Problems,
    Background
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
// version or data folder is its own outcome, because the person must exit that
// engine first.
enum class Forwarding
{
    Accepted,
    Refused,
    OtherVersion,
    OtherStore
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

// What the desktop host waits for from the product window.
enum class WindowWait
{
    // `ready` from a window that starts or reconnects.
    Ready,
    // The reply to `activate`.
    Activation,
    // The answer to `confirm_exit`.
    Confirmation,
    // The window ending for Exit.
    Closing
};

enum class WindowTarget
{
    Settings,
    Torrent
};

enum class TrayClick
{
    Idle,
    Waiting,
    Double
};

// The parts of a popup that the person can select.
enum class PopupPart
{
    Body,
    Action,
    More,
    Close
};

// How long a toast stays: until its timeout, until the person dismisses it,
// or until dismissed while it also takes the focus, because it answers the
// person's own action.
enum class ToastStay
{
    Timed,
    Persistent,
    Interactive
};

// Where the prompt to exit another TinyTorrent stands: asking, waiting for
// Exit's answer, Exit accepted while the other TinyTorrent still runs, Exit
// refused, waiting after Force exit, or Force exit refused by Windows.
enum class Recovery
{
    Asking,
    Exiting,
    Accepted,
    Refused,
    Forcing,
    ForceFailed
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
    // The window could not show the prompt, so the host asks in its popup.
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
    Exit,
#ifdef _DEBUG
    SampleToast,
#endif
};
}
}
