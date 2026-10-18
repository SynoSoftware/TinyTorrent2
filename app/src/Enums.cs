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

public enum PieceOrder
{
    Sequential,
    FirstLast,
}

public enum FileAction
{
    Move,
    Remove,
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

    // The window cannot show the prompt now, so the engine asks natively.
    Unavailable,
}

public enum VideoKind
{
    Movie,
    Series,
    Episode,
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

internal enum PauseReason
{
    None,
    Adapter,
    Manual,
    Schedule,
    ConnectionTest,
}

internal enum NoticeKind
{
    Status,
    Resume,
    CommandError,
    Completion,
    SubtitleError,
    Outcome,
    SearchLimit,
    WorkResult,
#if CAPTURE
    // A notice a capture journey shows without the state behind it; the
    // model neither describes nor removes it.
    Presentation,
#endif
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
    Memory,
    Footer,
}
#endif
