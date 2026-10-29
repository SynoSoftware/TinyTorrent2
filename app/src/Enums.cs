using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shell;
using Syno.TinyTorrent.Torrents;
namespace Syno.TinyTorrent;

internal enum ImdbPhase
{
    Downloading,
    Episodes,
    Linking,
    Titles,
    Ratings,
    Indexing,
    Optimizing,
    Flushing,
    Installing,
}

internal enum CityPhase
{
    Downloading,
    Unpacking,
    Installing,
}

internal enum UpdatePhase
{
    Checking,
    Downloading,
    Installing,
}

// Why Help > Update left the current version running.
internal enum UpdateFailure
{
    Missing,
    Signature,
    Checksum,
    Setup,
}

internal enum RatingSample
{
    Unknown,
    Small,
    Moderate,
    Large,
}

internal enum MetadataScope
{
    Batch,
    Automatic,
    Inspector,
}

internal enum SubtitleWork
{
    Manual,
    Automatic,
    Discovery,
}

internal enum WorkUnit
{
    Files,
    Folders,
    Bytes,
    Percent,
}

internal enum WorkOutcome
{
    Completed,
    Cancelled,
    Failed,
}

// Where a CancellableWork is in its life; WorkExecution is what the work list
// shows of it.
internal enum WorkPhase
{
    Running,
    Cancelling,
    Ended,
}

// What the window shows after AddDraft receives sources.
internal enum ArrivalOutcome
{
    // Nothing: the sources were added, joined the open Add dialog, or wait in
    // the draft for a later Add dialog.
    Settled,
    Dialog,

    // Every source is already listed, and one has trackers its listed
    // torrent lacks.
    Merge,
}

internal enum MaintenanceWork
{
    Recovery,
    ResetProtection,
    DeleteDataset,
}

public enum FileKind
{
    Video,
    Audio,
    Picture,
    Document,
    Archive,
    Other,
}

// Whether a file browser holds a folder's entries.
internal enum FolderState
{
    // Not read yet, or collapsed and forgotten: its row shows an expand arrow.
    Unloaded,
    Loading,
    // Its entries do not fit the window, so it never opens.
    TooMany,
    Loaded,
}

public enum WindowPage
{
    Torrents,
    Settings,
    About,
    Library,
    Connection,
    Credits,
}

public enum LibraryConfiguration
{
    Videos,
    Music,
    Files,
}

internal enum LibrarySection
{
    Information,
    File,
}

public enum TransferPreset
{
    Reduced,
    Balanced,
    FullSpeed,
    Custom,
}

// The engine stores bytes per second; each value is the bytes in one unit.
public enum RateScale
{
    KibPerSecond = 1024,
    MbPerSecond = 1000000,
}

public enum FilterPane
{
    Hidden,
    Compact,
    Open,
}

public enum Dock
{
    None,
    Top,
    Bottom,
    Left,
    Right,
}

public enum Consumer
{
    Summary,
    Inspector,
    Draft,
    Files,
}

// Who requested an engine command. Only an accepted command the person
// requested ends the command error; the window requests reads, which change
// nothing, and the messages it sends on its own, such as its handshakes.
internal enum Requester
{
    Person,
    Window,
}

public enum PieceOrder
{
    Sequential,
    FirstLast,
}

internal enum AdditionPhase
{
    Idle,
    Preparing,
    Submitting,
}

public enum DeletionMode
{
    Recycle,
    Permanent,
}

public enum TorrentFilter
{
    All,
    Downloading,
    Seeding,
    Paused,
    Queued,
    Errors,
}

public enum SuggestionScope
{
    Torrent,
    Command,
    Settings,
    Navigation,
}

// The saved window placement stores the category as its number, so members keep
// their positions; the tab order on the page is separate.
public enum SettingsCategory
{
    General,
    Transfers,
    Network,
    Limits,
    Appearance,
    Advanced,
    Schedule,
    Plugins,
}

internal enum SubtitleScope
{
    New,
    NoExactMatch,
    All,
}

/// <summary>
/// What a Library provider or subtitle supplier asks the person for, so the
/// app can show the matching account dialog.
/// </summary>
internal enum Access
{
    None,
    Key,
    Password,
}

/// <summary>What the person must supply before a Library provider downloads automatically.</summary>
internal enum ProviderRequirement
{
    None,
    Data,
    Key,
}

internal enum SubtitleSupplier
{
    OpenSubtitles,
    SubDL,
    SubSource,
}

/// <summary>
/// What one of a subtitle supplier's requests is, which decides how the
/// subtitle host signs it, where its failures hold and how large its reply
/// may be. Only <see cref="File"/> and <see cref="Link"/> answer with a
/// subtitle file; the others answer with JSON.
/// </summary>
internal enum SupplierRequest
{
    /// <summary>A request to the supplier's API, signed with the account's key.</summary>
    Api,

    /// <summary>An API request whose reply is the subtitle file.</summary>
    File,

    /// <summary>
    /// An API request that also sends the account's session, which the
    /// supplier renews once when it is refused, so a refusal holds nothing.
    /// </summary>
    Session,

    /// <summary>
    /// The request that signs the account in. Its failures hold sign-in, not
    /// the operation it serves.
    /// </summary>
    SignIn,

    /// <summary>
    /// A link that the supplier handed out and that carries its own
    /// permission, so it is sent without the key and holds only its server.
    /// </summary>
    Link,
}

internal enum SubtitleStage
{
    Release,
    Complete,
}

internal enum SubtitleOutcome
{
    Pending,
    NoMatch,
    Unavailable,
    Failed,
    Complete,
    Cancelled,
    Blocked,
}

internal enum SubtitleMatch
{
    None,
    Exact,
    Likely,
}

// What the subtitle supplier last answered about the saved account.
internal enum AccountCheck
{
    Unchecked,
    Verified,
    Refused,
}

// Whether the subtitle supplier could be reached at its last request.
internal enum SupplierReach
{
    Ok,
    Network,
    Route,
}

internal enum SubtitleFailure
{
    None,
    Unconfigured,
    Unavailable,
    Authentication,
    Quota,
    Network,
    Route,
    Database,
    Save,
    Format,
    Exists,
    Missing,
    Delete,
    Changed,
}

internal enum SubtitlePathAccess
{
    Pending,
    Protected,
    Allowed,
}

public enum InspectorSection
{
    General,
    Files,
    Peers,
    Trackers,
    Speed,
    Pieces,
}

public enum PeerLayout
{
    Table,
    Map,
    Both,
}

// Which connections map the window shows over its workspace, if any.
public enum MapScope
{
    None,
    Selected,
    All,
}

public enum MapCorner
{
    BottomLeft,
    TopLeft,
    BottomRight,
}

public enum PieceKind
{
    Unavailable,
    Rare,
    Common,
    Missing,
    Downloading,
    Verified,
}

// A torrent's state as the engine's status word names it; Unknown is a word
// this app does not know.
public enum TorrentState
{
    Unknown,
    Downloading,
    Seeding,
    Completed,
    Paused,
    AllPaused,
    Checking,
    WaitingCheck,
    Moving,
    WaitingMove,
    Metadata,
    Queued,
    Error,
}

// The engine's startup as the snapshot's loading and storage_failed report it.
internal enum EngineStartup
{
    Loading,
    Ready,
    Failed,
}

// A draft that closing the window must save, discard or ask about.
internal enum UnfinishedDraft
{
    Add,
    Move,
    Inspector,
}

internal enum PeerTransport
{
    Unknown,
    Tcp,
    Utp,
    I2p,
    Http,
}

public enum PeerState
{
    Unknown,
    Known,
    Connecting,
    Handshake,
    Connected,
}

internal enum PeerRole
{
    Unknown,
    Seed,
    Leecher,
}

internal enum PeerEncryption
{
    Unknown,
    Plain,
    Handshake,
    Rc4,
    Tls,
}

[Flags]
internal enum PeerSource
{
    None = 0,
    Tracker = 1,
    Dht = 2,
    Pex = 4,
    Lsd = 8,
    Resume = 16,
    Incoming = 32,
}

[Flags]
internal enum TransferWait
{
    None = 0,
    Limit = 1,
    Network = 2,
    Disk = 4,
}

public enum TrackerStatus
{
    Unknown,
    Waiting,
    Announcing,
    Working,
    Error,
    Disabled,
}

public enum ScheduleMode
{
    Normal,
    Alternative,
    Paused,
}

public enum LimitMode
{
    None,
    Speed,
    Alternative,
    Schedule,
}

internal enum LimitProfile
{
    None,
    Speed,
    Alternative,
}

internal enum LimitOrigin
{
    Manual,
    Schedule,
    Override,
}

// The window's answer when the engine asks it to confirm Exit.
internal enum ExitAnswer
{
    Confirmed,
    Cancelled,

    // The window cannot show the prompt now, so the engine asks in its popup.
    Unavailable,
}

// The person's answer for all the disk work that Exit or a crash left
// unfinished. The engine's words are these names in lower case.
internal enum UnfinishedAnswer
{
    Continue,
    Abandon,
}

// What started the window's close.
internal enum CloseRequest
{
    // The person closed the window; the engine keeps running.
    Close,

    // The engine is exiting and waits for the window's reply.
    Exit,

    // Reset all settings: the window closes and a new one opens.
    Reset,
}

public enum VideoKind
{
    Movie,
    Series,
    Episode,
}

/// <summary>How Library's identification of one file ended.</summary>
internal enum IdentificationOutcome
{
    /// <summary>The provider's video is saved for the file.</summary>
    Matched,

    /// <summary>The provider has no confident match for the file.</summary>
    NoMatch,

    /// <summary>A page or record about the file could not be read.</summary>
    Unanswered,

    /// <summary>
    /// Search protection held the lookup back, such as the search limit or a
    /// page known to be unusable.
    /// </summary>
    Blocked,

    /// <summary>
    /// The file changed or a newer decision won, so the attempt saved
    /// nothing.
    /// </summary>
    Superseded,
}

/// <summary>
/// How an identification attempt ended, as <c>video_attempts.outcome</c> holds
/// it; NULL there is an attempt still in flight.
/// </summary>
internal enum AttemptOutcome
{
    Matched,
    NoMatch,

    /// <summary>Search protection held it back; the attempt names the blocked request.</summary>
    Blocked,
    Unanswered,

    /// <summary>
    /// The attempt was stopped or superseded, or the provider could serve no
    /// request, so the automatic sweep may try the file again.
    /// </summary>
    Cancelled,
    Failed,
}

internal enum RequestOutcome
{
    Answered,
    Missing,
    Unusable,
    NoMatch,
}

/// <summary>
/// Why video information failed, which decides the text the person reads.
/// Library writes the text and fills in the provider's name.
/// </summary>
internal enum VideoFailure
{
    // The provider cannot answer any request now.
    Unavailable,
    Route,
    Refused,
    Exhausted,
    Blocked,
    Damaged,
    Unreadable,

    // The provider has no usable answer for one video.
    InvalidResult,
    Missing,

    // Downloading the provider's data failed, and the installed data stays.
    DownloadFailed,
    DownloadDamaged,
    DiskFull,
    InUse,
    UnknownFormat,
    Interrupted,

    // Library refused the request itself.
    ProviderUnavailable,
    LookupStopped,
    FileChanged,
    EpisodeRequired,
}

internal enum RequestBlockKind
{
    Missing,
    Unusable,
    Limit,
    Backoff,
    RemoteWait,
    Attention,
    Allowance,
    NoMatch,
}

/// <summary>
/// Why a provider call failed with a hold, as the shared HTTP path reads the
/// reply. Library and Subtitles turn it into their own failures.
/// </summary>
internal enum ReplyFailure
{
    Refused,
    Wait,
    Unavailable,

    // The reply exceeds its size limit, or its body is not what the call reads.
    Invalid,
}

internal enum FetchScope
{
    New,
    Missing,
    All,
}

// What one provider last answered for a file, which groups Fetch metadata's batch.
internal enum ProviderAnswer
{
    // Never asked, or the request did not complete.
    Unasked,

    // The provider has no data or no acceptable match for the file.
    NotFound,
    Found,
}

// Lookup policies govern eligibility and whether a match is the person's decision.
internal enum LookupKind
{
    // The background sweep skips decisions and previous attempts.
    Automatic,

    // A batch retries unresolved files; matching still makes the decision.
    Batch,

    // Refresh preserves an existing identification from the same provider.
    Refresh,

    // An individual request reports errors and saves an explicit decision.
    Explicit,
}

internal enum LibraryReadiness
{
    // The engine is disconnected or its data sources are not current, so
    // Library shows the last known rows.
    Offline,

    // The summary is reconciled and cached rows can be browsed.
    Current,

    // Every current origin has had a read attempt; workers check file confirmation.
    Ready,
}

// What a feature failure says about the Library database file, which decides
// the fix the message bar offers.
internal enum DatabaseCondition
{
    Other,

    // An earlier or later version created it; Delete replaces it.
    Incompatible,

    // The file is marked read-only; Repair clears the mark.
    ReadOnlyFile,
}

internal enum PauseReason
{
    None,
    Adapter,
    Manual,
    Schedule,
    ConnectionTest,
}

// What limits the session's transfers: a pause outranks the alternative limits.
internal enum Restriction
{
    None,
    Paused,
    Alternative,
}

internal enum RefusalReason
{
    InvalidPeriod,
    DuplicatePeriod,
    SaveFailed,
}

internal enum PeriodAction
{
    Create,
    Move,
    Start,
    End,
}

// In the order of the Edit proxy server dialog's list.
public enum ProxyType
{
    None,
    Socks5,
    Socks4,
    Http,
}

public enum ProxyOutcome
{
    Connected,
    SignInFailed,
    Unreachable,
    NotFound,
    WrongType,
    TimedOut,
}

public enum SettingKind
{
    Text,
    Boolean,
    Rate,
    Number,
    Integer,
}

internal enum ConnectionPhase
{
    Idle,
    Stopping,
    Downloading,
    Uploading,
    Holding,
    Restoring,
    Completed,
    Cancelled,
    Failed,
}

#if CAPTURE
internal enum CaptureMode
{
    None,
    Full,
    Smoke,
    Shell,
    Schedule,
    Desktop,
    Details,
    DetailsFiles,
    FileControls,
    Files,
    FilesLayout,
    Search,
    Library,
    Traffic,
    Peers,
    LibraryFiles,
    Connections,
    Edits,
    AddLayout,
    SettingsLayout,
    SettingsPrototype,
    SubtitleTasks,
    Memory,
    Footer,
    Workspace,
}
#endif
